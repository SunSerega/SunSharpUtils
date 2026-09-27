using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using SunSharpUtils.Ext.Bin;
using SunSharpUtils.Ext.Exceptions;

namespace SunSharpUtils.DataStash;

/// <summary>
/// </summary>
public static class RpcApiUtils
{

    /// <summary>
    /// </summary>
    public sealed class ConnectionReturnedErrorException(Connection connection, String error_message) : Exception($"{connection}: Error from remote: {error_message}");

    /// <summary>
    /// </summary>
    public sealed class Connection : IDisposable
    {
        private readonly Socket socket;
        private readonly CancellationToken cancel_token;
        private readonly NetworkStream stream;
        private readonly BinaryWriter socket_bw;
        private readonly BinaryReader socket_br;
        private readonly String description;
        private Boolean had_error = false;
        private Boolean is_finished = false;

        /// <summary>
        /// </summary>
        public Connection(Socket socket, CancellationToken cancel_token)
        {
            this.socket = socket;
            this.cancel_token = cancel_token;
            this.stream = new NetworkStream(socket);
            this.socket_bw = new BinaryWriter(this.stream);
            this.socket_br = new BinaryReader(this.stream);
            this.description = $"{nameof(Connection)}({this.socket.RemoteEndPoint} => {this.socket.LocalEndPoint})";
        }

        /// <summary>
        /// </summary>
        public Boolean IsConnected => this.socket.Connected;

        /// <summary>
        /// </summary>
        public async Task ReportErrorsWhileAsync(Func<Task> act, CancellationToken extra_cancel_token)
        {
            try
            {
                await act.Invoke();
            }
            catch (Exception ex)
            {
                this.had_error = true;
                if (this.IsConnected)
                {
                    if (!this.cancel_token.IsCancellationRequested && !extra_cancel_token.IsCancellationRequested || !ex.GetNestedExceptions().All(ex => ex is OperationCanceledException))
                        Err.Handle(ex);
                }
                Err.HandleDuring(() => this.Finish(error_message: ex.ToString()));
                throw;
            }
        }

        /// <summary>
        /// </summary>
        public void ReportErrorsWhile(Action act, CancellationToken extra_cancel_token) =>
            this.ReportErrorsWhileAsync(() => { act.Invoke(); return Task.CompletedTask; }, extra_cancel_token).GetAwaiter().GetResult();

        /// <summary>
        /// </summary>
        public void WriteMessage(Action<BinaryWriter, CancellationToken> write_payload) =>
            this.ReportErrorsWhile(() => this.Write(EPacketKind.Message, write_payload), CancellationToken.None);

        /// <summary>
        /// </summary>
        public async Task ReadMessageAsync(Action<BinaryReader, CancellationToken> read_payload, CancellationToken read_cancel_token)
        {
            await this.ReportErrorsWhileAsync(() => this.ReadAsync(on_finish: () => throw new InvalidDataException($"{this}: Finished when expected to read a message"), on_message: read_payload, read_cancel_token), read_cancel_token);
            this.cancel_token.ThrowIfCancellationRequested();
        }

        /// <summary>
        /// </summary>
        public async Task<T> ReadMessageAsync<T>(Func<BinaryReader, CancellationToken, T> read_payload, CancellationToken read_cancel_token)
        {
            var result = default(ValueTuple<T>?);
            await this.ReportErrorsWhileAsync(() => this.ReadAsync(on_finish: () => throw new InvalidDataException($"{this}: Finished when expected to read a message"), on_message: (br, cancel_token) => result = new(read_payload(br, cancel_token)), read_cancel_token), read_cancel_token);
            return (result ?? throw null!).Item1;
        }

        /// <summary>
        /// </summary>
        public T ReadMessage<T>(Func<BinaryReader, CancellationToken, T> read_payload) =>
            this.ReadMessageAsync(read_payload, CancellationToken.None).GetAwaiter().GetResult();

        /// <summary>
        /// </summary>
        public override String ToString() => this.description;

        /// <summary>
        /// </summary>
        public void FinishWithoutError()
        {
            this.Finish(error_message: null);
        }

        /// <summary>
        /// </summary>
        public void Dispose() => Err.HandleDuring(() => this.Finish(error_message: this.cancel_token.IsCancellationRequested ? "Connection was canceled" : null));

        private enum EPacketKind : Byte
        {
            Message = 1,
            Finish = 2,
        }

        private void Write(EPacketKind kind, Action<BinaryWriter, CancellationToken> write_payload)
        {
            if (this.had_error)
                throw new InvalidOperationException($"{this} already had an error, cannot write more data");
            var mem = new MemoryStream(2048);
            var bw = new BinaryWriter(mem);
            bw.WriteEnum(kind);
            write_payload(bw, this.cancel_token);
            mem.Position = 0;

            try
            {
                mem.CopyTo(this.socket_bw.BaseStream);
                this.socket_bw.Flush();
            }
            catch
            {
                this.had_error = true;
                throw;
            }
        }

        private async Task ReadAsync(Action on_finish, Action<BinaryReader, CancellationToken> on_message, CancellationToken read_cancel_token)
        {
            try
            {
                var kind_bytes = new Byte[sizeof(EPacketKind)];
                using (var linked_cts = CancellationTokenSource.CreateLinkedTokenSource(this.cancel_token, read_cancel_token))
                    await this.stream.ReadExactlyAsync(new Memory<Byte>(kind_bytes), linked_cts.Token);
                var kind = (EPacketKind)kind_bytes.Single();
                switch (kind)
                {
                    case EPacketKind.Finish:
                        var error_message = this.socket_br.ReadNullableClass(br => br.ReadString());
                        if (error_message is not null)
                        {
                            this.had_error = true;
                            throw new ConnectionReturnedErrorException(this, error_message);
                        }
                        on_finish.Invoke();
                        break;
                    case EPacketKind.Message:
                        on_message.Invoke(this.socket_br, this.cancel_token);
                        break;
                    default:
                        throw new NotImplementedException($"{this}: Unknown packet kind: {kind}");
                }
            }
            catch
            {
                this.had_error = true;
                throw;
            }
        }

        private void Finish(String? error_message)
        {
            try
            {
                if (this.had_error)
                    return;
                if (this.is_finished)
                    return;
                this.is_finished = true;
                this.Write(EPacketKind.Finish, (bw, _) =>
                {
                    bw.WriteNullableClass(error_message, (bw, error_message) => bw.Write(error_message));
                });
                if (!this.cancel_token.IsCancellationRequested)
                    this.ReadAsync(on_finish: () => { }, on_message: (_, _) => throw new InvalidDataException($"{this}: Expected finish packet, but got message packet"), CancellationToken.None).GetAwaiter().GetResult();
            }
            finally
            {
                Err.HandleDuring(this.socket.Close);
            }
        }

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
        public void Connect(Action<Connection, CancellationToken> act, Boolean ignore_when_canceled, CancellationToken extra_cancel_token)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(this.ConfigOrThrow.CancelToken, extra_cancel_token);
            var cancel_token = cts.Token;
            while (true)
            {
                cancel_token.ThrowIfCancellationRequested();
                try
                {
                    using var socket = this.ConnectNewSocket();
                    using var connection = new Connection(socket, cancel_token);
                    act.Invoke(connection, cancel_token);
                    break;
                }
                catch (Exception ex) when (cancel_token.IsCancellationRequested && ex.GetNestedExceptions().All(ex => ex is OperationCanceledException))
                {
                    if (ignore_when_canceled)
                        break;
                    throw;
                }
                catch (Exception ex) when (ex is not ConnectionReturnedErrorException)
                {
                    Err.Handle($"{this}: Error communicating Client=>Server\n{ex}");
                    cancel_token.ThrowIfCancellationRequested();
                    Thread.Sleep(TimeSpan.FromSeconds(1));
                }
            }
        }
        /// <summary>
        /// </summary>
        public void Connect(Action<Connection, CancellationToken> act, CancellationToken extra_cancel_token) =>
            this.Connect(act, ignore_when_canceled: false, extra_cancel_token);
        /// <summary>
        /// </summary>
        public T Connect<T>(Func<Connection, CancellationToken, T> act, CancellationToken extra_cancel_token)
        {
            var result = default(ValueTuple<T>?);
            this.Connect((conn, token) =>
            {
                result = new(act.Invoke(conn, token));
            }, ignore_when_canceled: false, extra_cancel_token);
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
            $"ClientConnector[{this.target_description}]({this.config?.ToString() ?? "not initialized"})";

    }

}
