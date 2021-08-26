﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using SocketIOClient;
using SocketIOClient.JsonSerializer;
using SocketIOClient.Newtonsoft.Json;

namespace ElectronNET.API
{
    internal static class BridgeConnector
    {
        internal static class EventTasks<T>
        {
            //Although SocketIO already manage event handlers, we need to manage this here as well for the OnResult calls,
            //because SocketIO will simply replace the existing event handler on every call to On(key, ...) , which means there is 
            //a race condition between On / Off calls that can lead to tasks deadlocking forever without ever triggering their On handler

            private static readonly Dictionary<string, TaskCompletionSource<T>> _taskCompletionSources = new();
            private static readonly Dictionary<string, string> _eventKeys = new();
            private static readonly object _lock = new();

            /// <summary>
            /// Get or add a new TaskCompletionSource<typeparamref name="T"/> for a given event key
            /// </summary>
            /// <param name="key"></param>
            /// <param name="eventKey"></param>
            /// <param name="taskCompletionSource"></param>
            /// <param name="waitThisFirstAndThenTryAgain"></param>
            /// <returns>Returns true if a new TaskCompletionSource<typeparamref name="T"/> was added to the dictionary</returns>
            internal static bool TryGetOrAdd(string key, string eventKey, out TaskCompletionSource<T> taskCompletionSource, out Task waitThisFirstAndThenTryAgain)
            {
                lock (_lock)
                {
                    if (!_taskCompletionSources.TryGetValue(key, out taskCompletionSource))
                    {
                        taskCompletionSource = new(TaskCreationOptions.RunContinuationsAsynchronously);
                        _taskCompletionSources[key] = taskCompletionSource;
                        _eventKeys[key] = eventKey;
                        waitThisFirstAndThenTryAgain = null;
                        return true; //Was added, so we need to also register the socket events
                    }

                    if(_eventKeys.TryGetValue(key, out var existingEventKey) && existingEventKey == eventKey)
                    {
                        waitThisFirstAndThenTryAgain = null;
                        return false; //No need to register the socket events twice
                    }

                    waitThisFirstAndThenTryAgain = taskCompletionSource.Task; //Will need to try again after the previous existing one is done

                    taskCompletionSource = null;

                    return true; //Need to register the socket events, but must first await the previous task to complete
                }
            }

            /// <summary>
            /// Clean up the TaskCompletionSource<typeparamref name="T"/> from the dictionary if and only if it is the same as the passed argument
            /// </summary>
            /// <param name="key"></param>
            /// <param name="eventKey"></param>
            /// <param name="taskCompletionSource"></param>
            internal static void DoneWith(string key, string eventKey, TaskCompletionSource<T> taskCompletionSource)
            {
                lock (_lock)
                {
                    if (_taskCompletionSources.TryGetValue(key, out var existingTaskCompletionSource)
                        && ReferenceEquals(existingTaskCompletionSource, taskCompletionSource))
                    {
                        _taskCompletionSources.Remove(key);
                    }

                    if (_eventKeys.TryGetValue(key, out var existingEventKey) && existingEventKey == eventKey)
                    {
                        _eventKeys.Remove(key);
                    }
                }
            }
        }

        private static SocketIO _socket;

        private static object _syncRoot = new object();

        private static SemaphoreSlim _socketSemaphore = new SemaphoreSlim(1, 1);

        public static void Emit(string eventString, params object[] args)
        {
            //We don't care about waiting for the event to be emitted, so this doesn't need to be async 

            Task.Run(async () =>
            {
                if (App.SocketDebug)
                {
                    Log("Sending event {0}", eventString);
                }

                await _socketSemaphore.WaitAsync();
                try
                {
                    await Socket.EmitAsync(eventString, args);
                }
                finally
                {
                    _socketSemaphore.Release();
                }

                if (App.SocketDebug)
                {
                    Log($"Sent event {eventString}");
                }
            });
        }

        internal static void Log(string formatString, params object[] args)
        {
            if (Logger is object)
            {
                Logger.LogInformation(formatString, args);
            }
            else
            {
                Console.WriteLine(formatString, args);
            }
        }

        /// <summary>
        /// This method is only used on places where we need to be sure the event was sent on the socket, such as Quit, Exit, Relaunch and QuitAndInstall methods
        /// </summary>
        /// <param name="eventString"></param>
        /// <param name="args"></param>
        internal static void EmitSync(string eventString, params object[] args)
        {
            if (App.SocketDebug)
            {
                Log("Sending event {0}", eventString);
            }

            _socketSemaphore.Wait();

            try
            {
                Socket.EmitAsync(eventString, args).Wait();
            }
            finally
            {
                _socketSemaphore.Release();
            }


            if (App.SocketDebug)
            {
                Log("Sent event {0}", eventString);
            }
        }

        public static void Off(string eventString)
        {
            _socketSemaphore.Wait();
            try
            {
                Socket.Off(eventString);
            }
            finally
            {
                _socketSemaphore.Release();
            }
        }

        public static void On(string eventString, Action fn)
        {
            _socketSemaphore.Wait();
            try
            {
                Socket.On(eventString, _ => fn());
            }
            finally
            {
                _socketSemaphore.Release();
            }
        }

        public static void On<T>(string eventString, Action<T> fn)
        {
            _socketSemaphore.Wait();
            try
            {
                Socket.On(eventString, (o) => fn(o.GetValue<T>(0)));
            }
            finally
            {
                _socketSemaphore.Release();
            }
        }

        public static void Once<T>(string eventString, Action<T> fn)
        {
            On<T>(eventString, (o) =>
            {
                Off(eventString);
                fn(o);
            });
        }

        public static async Task<T> OnResult<T>(string triggerEvent, string completedEvent, params object[] args)
        {
            string eventKey = completedEvent;

            if (args is object && args.Length > 0) // If there are arguments passed, we generate a unique event key with the arguments
                                                   // this allow us to wait for previous events first before registering new ones
            {
                var hash = new HashCode();
                foreach(var obj in args)
                {
                    hash.Add(obj);
                }
                eventKey = $"{eventKey}-{(uint)hash.ToHashCode()}";
            }

            if (EventTasks<T>.TryGetOrAdd(completedEvent, eventKey, out var taskCompletionSource, out var waitThisFirstAndThenTryAgain))
            {
                if (waitThisFirstAndThenTryAgain is object)
                {
                    //There was a pending call with different parameters, so we need to wait that first and then call here again
                    try
                    {
                        await waitThisFirstAndThenTryAgain;
                    }
                    catch
                    {
                        //Ignore any exceptions here so we can set a new event below
                        //The exception will also be visible to the original first caller due to taskCompletionSource.Task
                    }

                    //Try again to set the event
                    return await OnResult<T>(triggerEvent, completedEvent, args);
                }
                else
                {
                    //A new TaskCompletionSource was added, so we need to register the completed event here

                    On<T>(completedEvent, (result) =>
                    {
                        Off(completedEvent);
                        taskCompletionSource.SetResult(result);
                        EventTasks<T>.DoneWith(completedEvent, eventKey, taskCompletionSource);
                    });

                    Emit(triggerEvent, args);
                }
            }

            return await taskCompletionSource.Task;
        }


        public static async Task<T> OnResult<T>(string triggerEvent, string completedEvent, CancellationToken cancellationToken, params object[] args)
        {
            string eventKey = completedEvent;

            if (args is object && args.Length > 0) // If there are arguments passed, we generate a unique event key with the arguments
                                                   // this allow us to wait for previous events first before registering new ones
            {
                var hash = new HashCode();
                foreach (var obj in args)
                {
                    hash.Add(obj);
                }
                eventKey = $"{eventKey}-{(uint)hash.ToHashCode()}";
            }

            if (EventTasks<T>.TryGetOrAdd(completedEvent, eventKey, out var taskCompletionSource, out var waitThisFirstAndThenTryAgain))
            {
                if (waitThisFirstAndThenTryAgain is object)
                {
                    //There was a pending call with different parameters, so we need to wait that first and then call here again
                    try
                    {
                        await Task.Run(() => waitThisFirstAndThenTryAgain, cancellationToken);
                    }
                    catch
                    {
                        //Ignore any exceptions here so we can set a new event below
                        //The exception will also be visible to the original first caller due to taskCompletionSource.Task
                    }

                    //Try again to set the event
                    return await OnResult<T>(triggerEvent, completedEvent, cancellationToken, args);
                }
                else
                {
                    using (cancellationToken.Register(() => taskCompletionSource.TrySetCanceled()))
                    {
                        //A new TaskCompletionSource was added, so we need to register the completed event here

                        On<T>(completedEvent, (result) =>
                        {
                            Off(completedEvent);
                            taskCompletionSource.SetResult(result);
                            EventTasks<T>.DoneWith(completedEvent, eventKey, taskCompletionSource);
                        });

                        Emit(triggerEvent, args);
                    }
                }
            }

            return await taskCompletionSource.Task;
        }
        private static SocketIO Socket
        {
            get
            {
                if (_socket is null)
                {
                    if (HybridSupport.IsElectronActive)
                    {
                        lock (_syncRoot)
                        {
                            if (_socket is null && HybridSupport.IsElectronActive)
                            {
                                var socket = new SocketIO($"http://localhost:{BridgeSettings.SocketPort}", new SocketIOOptions()
                                {
                                    EIO = 3,
                                    Reconnection = true,
                                    ReconnectionAttempts = int.MaxValue,
                                    ReconnectionDelay = 1000,
                                    ReconnectionDelayMax = 5000,
                                    RandomizationFactor = 0.1,
                                    ConnectionTimeout = TimeSpan.FromSeconds(10)
                                });

                                socket.JsonSerializer = new CamelCaseNewtonsoftJsonSerializer(socket.Options.EIO);


                                socket.OnConnected += (_, __) =>
                                {
                                    Log("ElectronNET socket connected on port {0}!", BridgeSettings.SocketPort);
                                };

                                socket.OnReconnectAttempt += (_, __) =>
                                {
                                    Log("ElectronNET socket is trying to reconnect on port {0}...", BridgeSettings.SocketPort);
                                };

                                socket.OnReconnectError += (_, ex) =>
                                {
                                    Log("ElectronNET socket failed to connect {0}", ex);
                                };

                                socket.OnReconnected += (_, __) =>
                                {
                                    Log("ElectronNET socket reconnected on port {0}...", BridgeSettings.SocketPort);
                                };


                                socket.OnDisconnected += (_, __) =>
                                {
                                    Log("ElectronNET socket disconnected, trying to reconnect on port {0}!", on port { 0});
                                    socket.ConnectAsync().Wait();
                                };

                                socket.ConnectAsync().Wait();

                                _socket = socket;
                            }
                        }
                    }
                    else
                    {
                        throw new Exception("Missing Socket Port");
                    }
                }

                return _socket;
            }
        }

        internal static ILogger<App> Logger { private get; set; }

        private class CamelCaseNewtonsoftJsonSerializer : NewtonsoftJsonSerializer
        {
            public CamelCaseNewtonsoftJsonSerializer(int eio) : base(eio)
            {
            }

            public override JsonSerializerSettings CreateOptions()
            {
                return new JsonSerializerSettings()
                {
                    ContractResolver = new CamelCasePropertyNamesContractResolver(),
                    NullValueHandling = NullValueHandling.Ignore,
                    DefaultValueHandling = DefaultValueHandling.Ignore
                };
            }
        }
    }
}
