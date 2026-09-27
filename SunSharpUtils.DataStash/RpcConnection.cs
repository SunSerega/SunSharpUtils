using System;
using System.IO;
using System.Linq;
using System.Net.Sockets;
using System.Threading;
using System.Threading.Tasks;

using SunSharpUtils.Ext.Bin;
using SunSharpUtils.Ext.Exceptions;

namespace SunSharpUtils.DataStash;

//TODO Maybe change the namespace using compiler directives?
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#pragma warning disable CS0436 // Type conflicts with imported type
#pragma warning restore IDE0079 // Remove unnecessary suppression

/// <summary>
/// </summary>
public sealed class RpcConnection : IDisposable
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
    public RpcConnection(Socket socket, CancellationToken cancel_token)
    {
        this.socket = socket;
        this.cancel_token = cancel_token;
        this.stream = new NetworkStream(socket);
        this.socket_bw = new BinaryWriter(this.stream);
        this.socket_br = new BinaryReader(this.stream);
        this.description = $"{nameof(RpcConnection)}({this.socket.RemoteEndPoint} => {this.socket.LocalEndPoint})";
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
    public void Dispose() => Err.HandleDuring(() => this.Finish(error_message: this.cancel_token.IsCancellationRequested ? "RpcConnection was canceled" : null));

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
                        throw new RpcConnectionReturnedErrorException(this, error_message);
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
