using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;

using SunSharpUtils.Ext.Linq;
using SunSharpUtils.Logs;

namespace SunSharpUtils.WinSvc;

/// <summary>
/// Common utilities for Windows services
/// </summary>
public static class WinSvcCommon
{

    /// <summary>
    /// Initializes
    /// </summary>
    public static void Init(SimpleSunService svc, Prompt.DelegateStore? prompt_init = null)
    {
        Environment.CurrentDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? throw new InvalidOperationException();

        Err.Init(new()
        {
            Handle = GlobalLog.AddError,
        });

        Prompt.Init(prompt_init ?? new()
        {
            Notify = (title, msg) => GlobalLog.AddMessage(new[] { title, msg }.Where(x => x is not null).JoinToString(": ")),
            AskYesNo = (title, msg) => throw new InvalidOperationException(),
            AskAny = (title, msg, def) => throw new InvalidOperationException(),
        });

        Prompt.Notify($"==================================================");
        Prompt.Notify($"Command line: {Environment.CommandLine}");
        Prompt.Notify($"==================================================");
        GlobalLog.FlushAll();

        try
        {
            if (svc.StartCalled)
                throw new InvalidOperationException($"Services passed to {nameof(WinSvcCommon)}.{nameof(Init)} should be a new instance");
            ServiceBase.Run(svc);
            if (svc.StartCalled)
                return;

            Debugger.Launch();
            svc.DebugStart();
        }
        catch (Exception ex)
        {
            HandleCriticalError(ex, when_doing: $"starting {svc.ServiceName}");
        }

    }

    /// <summary>
    /// </summary>
    public readonly struct SocketListenerConfig()
    {
        /// <summary>
        /// </summary>
        public required Int32 Port { get; init; }
        /// <summary>
        /// Maximum time allowed without receiving data from client before receive fails
        /// Default: 1 minute
        /// </summary>
        public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromMinutes(1);
        /// <summary>
        /// Maximum time allowed to send data to client before send fails
        /// Default: 1 second
        /// </summary>
        public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(1);
        /// <summary>
        /// Handler that would be launched in a new thread for each new client
        /// </summary>
        public required Action<Socket> OnClient { get; init; }
    }
    /// <summary>
    /// Starts listening for socket connections on all local IPs with given port
    /// </summary>
    public static void StartSocketListener(SocketListenerConfig config)
    {
        foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList.Append(IPAddress.Loopback))
        {
            var ep = new IPEndPoint(ip, config.Port);
            Prompt.Notify($"Will listen for clients at {ep}");
            var client_accept_thread = new Thread(ClientAcceptLoop)
            {
                IsBackground = true,
                Name = $"Client listener at {ep}",
            };
            client_accept_thread.Start();
            void ClientAcceptLoop()
            {
                try
                {
                    var listener = new Socket(SocketType.Stream, ProtocolType.Tcp);
                    listener.Bind(ep);
                    listener.Listen(backlog: 100); // Shouldn't matter since we are launching a new thread for each client and don't wait
                    while (true)
                        try
                        {
                            var client_socket = listener.Accept();
                            var remote_ep = client_socket.RemoteEndPoint;

                            // -1 if Timeout.InfiniteTimeSpan
                            client_socket.ReceiveTimeout = (Int32)config.ReceiveTimeout.TotalMilliseconds;
                            client_socket.SendTimeout = (Int32)config.SendTimeout.TotalMilliseconds;

                            var thr = new Thread(() =>
                            {
                                try
                                {
                                    config.OnClient(client_socket);
                                }
                                catch (Exception ex)
                                {
                                    Err.Handle($"Error handling client connected to {ep} from {remote_ep}");
                                    Err.Handle(ex);
                                    Err.HandleDuring(client_socket.Close);
                                }
                            })
                            {
                                IsBackground = true,
                                Name = $"Client connected to {ep} from {remote_ep}",
                            };
                            thr.Start();
                        }
                        catch (Exception ex)
                        {
                            Err.Handle($"Error accepting client at {ep}");
                            Err.Handle(ex);
                        }
                }
                catch (Exception ex)
                {
                    HandleCriticalError(ex, when_doing: $"accepting clients at {ep}");
                }
            }
        }
    }

    /// <summary>
    /// Handles the error and shuts down the svc
    /// </summary>
    /// <param name="ex"></param>
    /// <param name="when_doing">description of what couldn't be done, lowercase</param>
    public static void HandleCriticalError(Exception ex, String when_doing)
    {
        Err.Handle(ex);
        Err.Handle($"Critical error {when_doing}, exiting");
        GlobalLog.FlushAll();
        Environment.Exit(-1);
    }

}
