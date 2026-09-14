using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;

using SunSharpUtils.Ext.Bin;

namespace SunSharpUtils.DataStash;

/// <summary>
/// </summary>
public static class RpcApiUtils
{

    /// <summary>
    /// Type of result returned from server to client
    /// </summary>
    public enum EServerCommand : Byte
    {

        /// <summary>
        /// </summary>
        Success = 1,
        /// <summary>
        /// </summary>
        Error = 2,

    }

    /// <summary>
    /// A factory for connections from client to server
    /// </summary>
    public sealed class ClientConnector(String target_description)
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
            public override String ToString() =>
                $"{this.Host}:{this.Port}";
        }
        private Config? config = null;
        /// <summary>
        /// </summary>
        public void Init(Config config)
        {
            if (this.config is not null)
                throw new InvalidOperationException($"{nameof(ClientConnector)} already initialized");
            this.config = config;
        }

        /// <summary>
        /// </summary>
        public readonly struct Connection
        {
            /// <summary>
            /// </summary>
            public required BinaryWriter Writer { get; init; }
            /// <summary>
            /// </summary>
            public required BinaryReader Reader { get; init; }
        }
        /// <summary>
        /// </summary>
        public void Connect(Action<Connection> act)
        {
            while (true)
            {
                var should_catch = true;
                try
                {
                    using var socket = this.ConnectNewSocket();
                    var stream = new NetworkStream(socket);
                    var conn = new Connection()
                    {
                        Writer = new BinaryWriter(stream),
                        Reader = new BinaryReader(stream),
                    };
                    act.Invoke(conn);
                    conn.Writer.Flush();
                    var server_cmd = conn.Reader.ReadEnum<EServerCommand>();
                    switch (server_cmd)
                    {
                        case EServerCommand.Success:
                            return;
                        case EServerCommand.Error:
                            var error_message = conn.Reader.ReadString();
                            should_catch = false;
                            throw new InvalidOperationException($"{this}: Server reported error: {error_message}");
                        default:
                            throw new NotImplementedException($"{this}: Unknown server command: {server_cmd}");
                    }
                }
                catch (Exception ex) when (should_catch)
                {
                    var message = $"{this}: Error communicating Client=>Server";
                    Err.Handle(message);
                    Err.Handle(ex);
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }
            }
        }
        /// <summary>
        /// </summary>
        public T Connect<T>(Func<Connection, T> act)
            where T : notnull
        {
            var result = default(T);
            this.Connect(conn =>
            {
                result = act.Invoke(conn);
            });
            return result ?? throw null!;
        }

        private readonly Lock l_new_socket = new();
        private Socket ConnectNewSocket()
        {
            var config = this.config ?? throw new InvalidOperationException($"{nameof(ClientConnector)} not initialized");
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
            $"ClientConnector[{this.target_description}]({this.config?.ToString() ?? "not initialized"})";

    }

}
