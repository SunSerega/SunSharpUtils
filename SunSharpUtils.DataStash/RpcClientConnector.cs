using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

using SunSharpUtils.Ext.Bin;
using SunSharpUtils.Ext.Exceptions;
using SunSharpUtils.Ext.Linq;
using SunSharpUtils.WinSvc;

namespace SunSharpUtils.DataStash;

//TODO VRCT does the whole thing with a single streamed rpc method, where it verifies set ip on first succesful connect
// - I can split that design into 2 methods: Ping (initial verify server exists at ip) and then the actual deferred streamed method
//TODO I can make a common abstract class for WPF client RPC methods
// - abstract method TryConnectImpl, to be overriden with a call to TryConnect
// - static method for letting user choose the server ip

//TODO Maybe change the namespace using compiler directives?
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#pragma warning disable CS0436 // Type conflicts with imported type
#pragma warning restore IDE0079 // Remove unnecessary suppression

/// <summary>
/// A factory for connections from client to server
/// </summary>
public sealed class RpcClientConnector(String target_description)
{
    private readonly String target_description = target_description;

    #region Init

    /// <summary>
    /// </summary>
    public readonly struct Config()
    {
        /// <summary>
        /// </summary>
        public required String Host { get; init; }
        /// <summary>
        /// </summary>
        public required Int32 Port { get; init; }
        /// <summary>
        /// </summary>
        public required CancellationToken CancelToken { get; init; }

        /// <summary>
        /// Default: 1 minute
        /// </summary>
        public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromMinutes(1);
        /// <summary>
        /// Default: 1 second
        /// </summary>
        public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(1);

        /// <summary>
        /// </summary>
        public override String ToString() =>
            $"{this.Host}:{this.Port}";
    }
    private Config? config = null;
    /// <summary>
    /// </summary>
    public void Init(Config config)
    {
        if (this.config is not null)
            throw new InvalidOperationException($"{this} is already initialized");
        this.config = config;
    }
    private Config ConfigOrThrow => this.config ?? throw new InvalidOperationException($"{this} is not initialized");

    #endregion

    #region Client-side

    #region Ping

    /// <summary>
    /// </summary>
    public Boolean TryConnect(TimeSpan timeout)
    {
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.ConfigOrThrow.CancelToken);
            cts.CancelAfter(timeout);
            using var socket = this.ConnectNewSocket();
            var stream = new NetworkStream(socket);
            using var connection = new RpcConnection(socket, $"{nameof(TryConnect)} ping", cts.Token);
            connection.WriteMessage((bw, _) => bw.WriteEnum(EConnectionType.Ping));
            return true;
        }
        catch
        {
            return false;
        }
    }
    /// <summary>
    /// Default timeout: 10 seconds
    /// </summary>
    public Boolean TryConnect() => this.TryConnect(TimeSpan.FromSeconds(10));

    #endregion

    #region Invoke

    #region Common

    /// <summary>
    /// </summary>
    public readonly struct ConnectCommonConfig()
    {
        /// <summary>
        /// </summary>
        public required String ActDescription { get; init; }
        /// <summary>
        /// </summary>
        public required Boolean IgnoreErrorsWhenServerNotPingable { get; init; }
        /// <summary>
        /// </summary>
        public required CancellationToken ExtraCancelToken { get; init; }
    }
    private interface IConnectionState
    {
        public void InvokeConnectedAct(RpcConnection connection, Action clear_errors, CancellationToken cancel_token);
        public Boolean ShouldFailFast(Exception ex);
        public Boolean ShouldReportImmediately { get; }
        public void OnEnoughErrorsCollected(Int32 try_count, Exception[] unique_errors, Int32 total_error_count, String err_counts_str);
    }
    private void ConnectForInvokeInternal<TCommand, TConnState>(TCommand command, Func<TConnState> new_conn_state, ConnectCommonConfig common_config, Boolean swalow_op_cancel, Int32? max_errors)
        where TCommand : struct, Enum
        where TConnState : IConnectionState
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.ConfigOrThrow.CancelToken, common_config.ExtraCancelToken);
        var cancel_token = cts.Token;
        var try_i = 0;
        var errors = new List<Exception>();
        while (true)
        {
            try_i += 1;
            var conn_state = new_conn_state.Invoke();
            try
            {
                cancel_token.ThrowIfCancellationRequested();
                using var socket = this.ConnectNewSocket();
                using var connection = new RpcConnection(socket, common_config.ActDescription, cancel_token);
                connection.WriteMessage((bw, _) => bw.WriteEnum(EConnectionType.Invoke));
                connection.WriteMessage((bw, _) => bw.WriteEnum(command));
                connection.ReportErrorsWhile(() => conn_state.InvokeConnectedAct(connection, errors.Clear, cancel_token));
                break;
            }
            catch (Exception ex) when (ex.IsOnlyNestedOpCanceledExceptions(cancel_token))
            {
                if (swalow_op_cancel)
                    break;
                throw;
            }
            catch (Exception ex)
            {
                if (common_config.IgnoreErrorsWhenServerNotPingable && !this.TryConnect())
                {
                    do
                    {
                        cancel_token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
                        cancel_token.ThrowIfCancellationRequested();
                    } while (!this.TryConnect());
                    continue;
                }

                if (conn_state.ShouldFailFast(ex))
                    throw;

                if (conn_state.ShouldReportImmediately)
                {
                    Err.Handle($"{this}: Error executing [{common_config.ActDescription}]\n{ex}");
                    if (errors.Count != 0)
                        throw new InvalidOperationException($"{this}: {nameof(errors)} should be empty when {nameof(conn_state.ShouldReportImmediately)} is true");
                }
                else
                {
                    if (max_errors is null)
                        throw new InvalidOperationException($"{this}: {nameof(max_errors)} must be set when {nameof(conn_state.ShouldReportImmediately)} is false");
                    errors.Add(ex);
                    if (errors.Count >= max_errors)
                    {
                        var error_lookup = errors.ToLookup(ex => ex.ToString());
                        var counts_str = error_lookup.Select(g => g.Count()).JoinToString(',');
                        var unique_errors = error_lookup.Select(g => g.First()).ToArray();
                        conn_state.OnEnoughErrorsCollected(try_i, unique_errors, errors.Count, counts_str);
                        errors.Clear();
                    }
                }

                cancel_token.WaitHandle.WaitOne(TimeSpan.FromSeconds(1));
            }
        }
    }

    #endregion

    #region Blocking

    /// <summary>
    /// </summary>
    public readonly struct ConnectBlockingConfig()
    {
        /// <summary>
        /// </summary>
        public required Int32? TriesLimit { get; init; }
    }
    private readonly struct BlockingConnectionState : IConnectionState
    {
        public required Action<RpcConnection, CancellationToken> ConnectedAct { get; init; }
        public required Boolean ShouldReportImmediately { get; init; }
        public required Boolean ShouldAlwaysFailFast { get; init; }

        public void InvokeConnectedAct(RpcConnection connection, Action clear_errors, CancellationToken cancel_token) =>
            this.ConnectedAct.Invoke(connection, cancel_token);

        public Boolean ShouldFailFast(Exception ex)
        {
            if (this.ShouldAlwaysFailFast)
                return true;
            if (ex is IRpcConnectionNonRetryableException)
                return true;
            return false;
        }

        public void OnEnoughErrorsCollected(Int32 try_count, Exception[] unique_errors, Int32 total_error_count, String err_counts_str) =>
            throw new AggregateException($"{this}: Failed to connect after {try_count} tries with {unique_errors.Length}/{total_error_count} unique errors ([{err_counts_str}] times)", unique_errors);

    }
    /// <summary>
    /// </summary>
    public void ConnectBlocking<TCommand>(TCommand command, Action<RpcConnection, CancellationToken> act, ConnectCommonConfig common_config, ConnectBlockingConfig blocking_config)
        where TCommand : struct, Enum
    {
        this.ConnectForInvokeInternal(
            command,
            () => new BlockingConnectionState
            {
                ConnectedAct = act,
                ShouldReportImmediately = blocking_config.TriesLimit is null,
                ShouldAlwaysFailFast = blocking_config.TriesLimit is 1,
            },
            common_config,
            swalow_op_cancel: false,
            max_errors: blocking_config.TriesLimit
        );
    }
    /// <summary>
    /// </summary>
    public TResult ConnectBlocking<TCommand, TResult>(TCommand command, Func<RpcConnection, CancellationToken, TResult> act, ConnectCommonConfig common_config, ConnectBlockingConfig blocking_config)
        where TCommand : struct, Enum
    {
        var result = default(ValueTuple<TResult>?);
        this.ConnectBlocking(command, (conn, token) =>
        {
            result = new(act.Invoke(conn, token));
        }, common_config, blocking_config);
        return (result ?? throw null!).Item1;
    }

    #endregion

    #region Deferred

    /// <summary>
    /// </summary>
    public readonly struct ConnectDeferredConfig()
    {
        /// <summary>
        /// </summary>
        public required Int32 MinInitialConnectErrorCountBeforeReport { get; init; }
    }
    private struct DeferredConnectionState() : IConnectionState
    {
        public required Func<RpcConnection, CancellationToken, Action> InitAct { get; init; }

        public void InvokeConnectedAct(RpcConnection connection, Action clear_errors, CancellationToken cancel_token)
        {
            var streaming_act = this.InitAct.Invoke(connection, cancel_token);
            this.ShouldReportImmediately = true;
            clear_errors.Invoke();
            streaming_act.Invoke();
        }

        public readonly Boolean ShouldFailFast(Exception ex) => false;

        public required Boolean ShouldReportImmediately { get; set; }

        public readonly void OnEnoughErrorsCollected(Int32 try_count, Exception[] unique_errors, Int32 total_error_count, String err_counts_str) =>
            Err.Handle($"{this}: Failed to init streaming after {try_count} tries with {unique_errors.Length}/{total_error_count} unique errors ([{err_counts_str}] times):\n\n{unique_errors.JoinToString("\n\n")}");

    }
    /// <summary>
    /// </summary>
    public void ConnectDeferred<TCommand>(TCommand command, Func<RpcConnection, CancellationToken, Action> init_act, ConnectCommonConfig common_config, ConnectDeferredConfig deferred_config)
        where TCommand : struct, Enum
    {
        var thr = new Thread(ThreadProc)
        {
            Name = $"{common_config.ActDescription} processing thread",
            IsBackground = false,
        };
        thr.Start();
        void ThreadProc() => Err.HandleDuring(() =>
        {
            this.ConnectForInvokeInternal(
                command,
                () => new DeferredConnectionState
                {
                    InitAct = init_act,
                    ShouldReportImmediately = deferred_config.MinInitialConnectErrorCountBeforeReport is 1,
                },
                common_config,
                swalow_op_cancel: true,
                max_errors: deferred_config.MinInitialConnectErrorCountBeforeReport
            );
        });
    }

    #endregion

    #endregion

    #region Raw connect

    private readonly Lock l_new_socket = new();
    private Socket ConnectNewSocket()
    {
        var config = this.ConfigOrThrow;
        using var lock_scope = this.l_new_socket.EnterScope();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            // -1 if Timeout.InfiniteTimeSpan
            ReceiveTimeout = (Int32)config.ReceiveTimeout.TotalMilliseconds,
            SendTimeout = (Int32)config.SendTimeout.TotalMilliseconds,
        };
        socket.Connect(config.Host, config.Port);
        return socket;
    }

    #endregion

    #endregion

    #region Server-side

    /// <summary>
    /// </summary>
    public interface IClientProcessor
    {
        /// <summary>
        /// Routes the client command to different methods based on the read enums
        /// </summary>
        public void ProcessClient(Socket socket, CancellationToken cancel_token);
    }
    /// <summary>
    /// </summary>
    public sealed class ClientProcessor<TCommand>(String rpc_class_name, KeyValuePair<TCommand, Func<RpcConnection, CancellationToken, Boolean>>[] command_handlers) : IClientProcessor
        where TCommand : struct, Enum
    {
        private readonly String act_description = $"{rpc_class_name}.ProcessClient";
        private readonly FrozenDictionary<TCommand, Func<RpcConnection, CancellationToken, Boolean>> command_handlers_dict = command_handlers.ToFrozenDictionary();

        /// <inheritdoc/>
        public void ProcessClient(Socket socket, CancellationToken cancel_token) =>
            ConnectServer(this.act_description, this.command_handlers_dict, socket, cancel_token);

    }
    private static void ConnectServer<TCommand>(String act_description, FrozenDictionary<TCommand, Func<RpcConnection, CancellationToken, Boolean>> command_handlers, Socket socket, CancellationToken cancel_token)
        where TCommand : struct, Enum
    {
        var keep_connection_open = false;
        var connection = new RpcConnection(socket, act_description, cancel_token);
        using var connection_disposer = new LambdaDisposable(() =>
        {
            if (keep_connection_open)
                return;
            connection.Dispose();
        });

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancel_token);
        connection.OnFinished += () =>
        {
            cts.Cancel();
            cts.Dispose();
        };

        var connection_type = connection.ReadMessage((br, _) => br.ReadEnum<EConnectionType>());
        switch (connection_type)
        {
            case EConnectionType.Ping:
                return;
            case EConnectionType.Invoke:
                break; // Continue to handle_command.Invoke
            default:
                throw new InvalidDataException($"{connection}: Unknown {nameof(EConnectionType)}: {connection_type}");
        }

        var client_cmd = connection.ReadMessage((br, _) => br.ReadEnum<TCommand>());
        if (!command_handlers.TryGetValue(client_cmd, out var command_handler))
            throw new InvalidDataException($"{connection}: Unknown client command: {client_cmd}");

        keep_connection_open = command_handler.Invoke(connection, cancel_token);
    }

    //TODO This has no buisness being in a common lib, that is also used by the client apps
    // - But that is mainly a problem of RPC being part of DataStash lib
    // - When it is separate, I can move this to WinSvcCommon
    /// <summary>
    /// </summary>
    public readonly struct RpcListenerConfig()
    {
        /// <summary>
        /// </summary>
        public required Int32 Port { get; init; }
        /// <summary>
        /// </summary>
        public required IClientProcessor ClientProcessor { get; init; }
        /// <summary>
        /// </summary>
        public required CancellationToken SvcStopToken { get; init; }

        /// <summary>
        /// Default: 1 minute
        /// </summary>
        public TimeSpan ReceiveTimeout { get; init; } = TimeSpan.FromMinutes(1);
        /// <summary>
        /// Default: 1 second
        /// </summary>
        public TimeSpan SendTimeout { get; init; } = TimeSpan.FromSeconds(1);
    }
    /// <summary>
    /// Calls <see cref="WinSvcCommon.StartSocketListener"/> with client handler implemented through given <see cref="IClientProcessor"/>
    /// </summary>
    /// <param name="config"></param>
    public static void StartRpcListener(RpcListenerConfig config)
    {
        var client_processor = config.ClientProcessor;
        var svc_stop_token = config.SvcStopToken;

        WinSvcCommon.StartSocketListener(new()
        {
            Port = config.Port,
            OnClient = socket => client_processor.ProcessClient(socket, svc_stop_token),

            ReceiveTimeout = config.ReceiveTimeout,
            SendTimeout = config.SendTimeout,
        });
    }

    #endregion

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"RpcClientConnector[{this.target_description}]({this.config?.ToString() ?? "not initialized"})";

    private enum EConnectionType : SByte
    {
        Ping = -1,
        Invoke = -2,
    }

}
