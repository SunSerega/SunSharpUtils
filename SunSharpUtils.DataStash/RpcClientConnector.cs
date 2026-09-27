using System;
using System.Linq;
using System.Net.Sockets;
using System.Threading;

using SunSharpUtils.Ext.Exceptions;

namespace SunSharpUtils.DataStash;

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

    /// <summary>
    /// </summary>
    public readonly struct Config
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

    /// <summary>
    /// </summary>
    public void Connect(Action<RpcConnection, CancellationToken> act, Int32? tries_limit, Boolean ignore_when_canceled, CancellationToken extra_cancel_token)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.ConfigOrThrow.CancelToken, extra_cancel_token);
        var cancel_token = cts.Token;
        var try_i = 0;
        while (true)
        {
            try_i += 1;
            cancel_token.ThrowIfCancellationRequested();
            try
            {
                using var socket = this.ConnectNewSocket();
                using var connection = new RpcConnection(socket, cancel_token);
                act.Invoke(connection, cancel_token);
                break;
            }
            catch (Exception ex) when (cancel_token.IsCancellationRequested && ex.GetNestedExceptions().All(ex => ex is OperationCanceledException))
            {
                if (ignore_when_canceled)
                    break;
                throw;
            }
            catch (Exception ex) when (!(try_i >= tries_limit) && ex is not RpcConnectionReturnedErrorException)
            {
                Err.Handle($"{this}: Error communicating Client=>Server\n{ex}");
                cancel_token.ThrowIfCancellationRequested();
                Thread.Sleep(TimeSpan.FromSeconds(1));
            }
        }
    }
    /// <summary>
    /// </summary>
    public void Connect(Action<RpcConnection, CancellationToken> act, Int32? tries_limit, CancellationToken extra_cancel_token) =>
        this.Connect(act, tries_limit, ignore_when_canceled: false, extra_cancel_token);
    /// <summary>
    /// </summary>
    public T Connect<T>(Func<RpcConnection, CancellationToken, T> act, Int32? tries_limit, CancellationToken extra_cancel_token)
    {
        var result = default(ValueTuple<T>?);
        this.Connect((conn, token) =>
        {
            result = new(act.Invoke(conn, token));
        }, tries_limit, ignore_when_canceled: false, extra_cancel_token);
        return (result ?? throw null!).Item1;
    }

    private readonly Lock l_new_socket = new();
    private Socket ConnectNewSocket()
    {
        var config = this.ConfigOrThrow;
        using var lock_scope = this.l_new_socket.EnterScope();
        var socket = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp)
        {
            SendTimeout = 1000,
            ReceiveTimeout = 60000,
        };
        socket.Connect(config.Host, config.Port);
        return socket;
    }

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"RpcClientConnector[{this.target_description}]({this.config?.ToString() ?? "not initialized"})";

}
