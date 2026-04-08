using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.ServiceProcess;
using System.Threading;

using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.WinSvc;

/// <summary>
/// Common utilities for Windows services
/// </summary>
public static class WinSvcCommon
{

    /// <summary>
    /// Initializes
    /// </summary>
    public static void Init(SimpleSunService svc)
    {

        Err.Init(new()
        {
            Handle = GlobalLog.AddError,
        });

        Prompt.Init(new()
        {
            Notify = (title, msg) => GlobalLog.AddMessage(new[] { title, msg }.Where(x => x is not null).JoinToString(": ")),
            AskYesNo = (title, msg) => throw new InvalidOperationException(),
            AskAny = (title, msg, def) => throw new InvalidOperationException(),
        });

        Environment.CurrentDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? throw new InvalidOperationException();

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
            if (Debugger.IsAttached)
                svc.DebugStart();
        }
        catch (Exception ex)
        {
            HandleCriticalError(ex, when_doing: $"starting {svc}");
        }

    }

    /// <summary>
    /// Starts listening for socket connections on all local IPs with given port
    /// </summary>
    public static void StartSocketListener(Int32 port, Action<Socket> on_client, Int32 client_queue_size = 100)
    {
        foreach (var ip in Dns.GetHostEntry(Dns.GetHostName()).AddressList.Append(IPAddress.Loopback))
        {
            var ep = new IPEndPoint(ip, port);
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
                    listener.Listen(client_queue_size);
                    while (true)
                        try
                        {
                            var client_socket = listener.Accept();
                            client_socket.ReceiveTimeout = 60*1000;
                            client_socket.SendTimeout = 1000;

                            var thr = new Thread(() => on_client(client_socket))
                            {
                                IsBackground = true,
                                Name = $"Client connected to {ep} from {client_socket.RemoteEndPoint}",
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
