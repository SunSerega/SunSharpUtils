using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Net.Sockets;
using System.Threading;

using SunSharpUtils.Ext.Bin;
using SunSharpUtils.UniversalBin;

namespace SunSharpUtils.DataStash;

//TODO Maybe change the namespace using compiler directives?
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#pragma warning disable CS0436 // Type conflicts with imported type
#pragma warning restore IDE0079 // Remove unnecessary suppression

/// <summary>
/// Represents a stream of values continuously received through RPC
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class RpcEnumerable<T> : IDisposable
    where T : notnull
{
    private readonly Socket socket;
    private readonly BinaryReader br;
    private Int32 existing_values_left;
    private Boolean is_finished = false;

    /// <param name="socket"></param>
    public RpcEnumerable(Socket socket)
    {
        this.socket = socket;
        this.br = new(new NetworkStream(socket));
        this.existing_values_left = this.br.ReadInt32();
    }

    /// <summary>
    /// Number of unread values that already existed when establishing connection
    /// </summary>
    public Int32 ExistingValuesLeft => this.existing_values_left;

    /// <summary>
    /// Tries to get the next value from the stream
    /// <para/>
    /// Fails if the stream is finished or if only_existing=true and all existing values have already been read
    /// </summary>
    /// <param name="item"></param>
    /// <param name="only_existing">Only consider values that already existed when establishing connection</param>
    /// <returns></returns>
    public Boolean TryGetNext([MaybeNullWhen(false)] out T? item, Boolean only_existing = false)
    {
        if (this.existing_values_left < 0)
            throw new InvalidOperationException($"The number of existing values left is negative: {this.existing_values_left}");
        if (this.is_finished || only_existing && this.existing_values_left == 0)
        {
            item = default;
            return false;
        }
        if (this.existing_values_left > 0)
            this.existing_values_left--;
        else
        {
            if (!this.br.ReadBoolean())
            {
                this.is_finished = true;
                item = default;
                return false;
            }
        }
        item = this.br.ReadData<T>();
        return true;
    }

    /// <summary>
    /// Closes the connection and prevents further reading of values
    /// </summary>
    public void Dispose()
    {
        this.is_finished = true;
        this.socket.Close();
    }

}

/// <summary>
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class RpcEnumerableSource<T>()
    where T : notnull
{
    private readonly Lock l_subscribers_and_items = new();
    private readonly HashSet<Subscriber> subscribers = [];
    private readonly List<T> existing_items = [];
    private Boolean is_closed = false;

    /// <summary>
    /// Adds an item and notifies all subscribers
    /// </summary>
    /// <param name="item"></param>
    public void Push(T item)
    {
        using var lock_scope = this.l_subscribers_and_items.EnterScope();
        if (this.is_closed)
            throw new InvalidOperationException($"{this} is already closed");
        this.existing_items.Add(item);
        this.ForEachSubscriber(subscriber => subscriber.Send(item));
    }

    /// <summary>
    /// Closes the source and notifies all subscribers
    /// </summary>
    public void Close()
    {
        using var lock_scope = this.l_subscribers_and_items.EnterScope();
        if (this.is_closed)
            throw new InvalidOperationException($"{this} is already closed");
        this.is_closed = true;
        this.ForEachSubscriber(subscriber => subscriber.Close());
    }

    internal void ForEachSubscriber(Action<Subscriber> act)
    {
        this.subscribers.RemoveWhere(subscriber =>
        {
            if (!subscriber.IsConnected)
                return true;
            try
            {
                act.Invoke(subscriber);
                return false;
            }
            catch (Exception ex)
            {
                if (!subscriber.IsConnected)
                    return true;
                Err.Handle(ex);
                subscriber.Dispose();
                return true;
            }
        });
    }

    /// <summary>
    /// </summary>
    public Subscriber Subscribe(Socket socket)
    {
        using var lock_scope = this.l_subscribers_and_items.EnterScope();
        var subscriber = new Subscriber(this, socket, this.existing_items);
        this.subscribers.Add(subscriber);
        return subscriber;
    }

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(RpcEnumerableSource<>)}<{typeof(T).Name}>";

    /// <summary>
    /// </summary>
    public sealed class Subscriber : IDisposable
    {
        private readonly RpcEnumerableSource<T> source;
        private readonly Socket socket;
        private readonly BinaryWriter bw;

        internal Subscriber(RpcEnumerableSource<T> source, Socket socket, ICollection<T> existing_items)
        {
            this.source = source;
            this.socket = socket;
            this.bw = new BinaryWriter(new NetworkStream(socket));
            this.bw.Write(existing_items.Count);
            foreach (var item in existing_items)
                this.bw.WriteData(item);
        }

        internal Boolean IsConnected => this.socket.Connected;

        internal void Send(T item)
        {
            this.bw.Write(true);
            this.bw.WriteData(item);
            this.bw.Flush();
        }

        internal void Close()
        {
            this.bw.Write(false);
            this.bw.WriteEnum(RpcApiUtils.EServerCommand.Success);
            this.bw.Flush();
            this.socket.Close();
        }

        /// <summary>
        /// </summary>
        public void Dispose()
        {
            this.source.subscribers.Remove(this);
            Err.HandleDuring(this.socket.Dispose);
        }

    }

}
