using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using SunSharpUtils.Ext.Bin;
using SunSharpUtils.Ext.Exceptions;
using SunSharpUtils.UniversalBin;
using SunSharpUtils.WinSvc;

namespace SunSharpUtils.DataStash;

//TODO Maybe change the namespace using compiler directives?
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#pragma warning disable CS0436 // Type conflicts with imported type
#pragma warning restore IDE0079 // Remove unnecessary suppression

internal static class RpcEnumerable
{

    internal enum ENextItemStatus : Byte
    {
        Stopped = 1,
        Sending = 2,
        Ping = 3,
    }

}

/// <summary>
/// Represents a stream of values continuously received through RPC
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class RpcEnumerable<T> : IDisposable
    where T : notnull
{
    private readonly RpcConnection connection;
    private readonly CancellationToken read_cancel_token;
    private readonly Queue<T> existing_values_left;
    private Boolean is_finished = false;

    /// <summary>
    /// </summary>
    public RpcEnumerable(RpcConnection connection, CancellationToken read_cancel_token)
    {
        this.connection = connection;
        this.read_cancel_token = read_cancel_token;
        this.existing_values_left = connection.ReadMessage((br, _) =>
        {
            var count = br.ReadInt32();
            var queue = new Queue<T>(count);
            for (var i = 0; i < count; i++)
                queue.Enqueue(br.ReadData<T>());
            return queue;
        });
    }

    /// <summary>
    /// </summary>
    private RpcEnumerable(T[] existing_values)
    {
        this.connection = null!;
        this.read_cancel_token = default;
        this.existing_values_left = new(existing_values);
        this.is_finished = true;
    }
    /// <summary>
    /// Creates a dummy RpcEnumerable that only contains the given existing values and does not read from any connection
    /// </summary>
    /// <param name="existing_values"></param>
    /// <returns></returns>
    public static RpcEnumerable<T> CreateDummy(T[] existing_values) => new(existing_values);

    /// <summary>
    /// Number of unread values that already existed when establishing connection
    /// </summary>
    public Int32 ExistingValuesLeft => this.existing_values_left.Count;

    /// <summary>
    /// Reads items from the RPC stream
    /// </summary>
    /// <param name="only_existing">Only consider values that already existed when establishing connection</param>
    /// <returns></returns>
    /// <exception cref="InvalidOperationException"></exception>
    /// <exception cref="EndOfStreamException"></exception>
    /// <exception cref="InvalidDataException"></exception>
    public async IAsyncEnumerable<T> ReadItemsAsync(Boolean only_existing = false)
    {

        while (this.existing_values_left.TryDequeue(out var existing_value))
            yield return existing_value;

        if (only_existing)
            yield break;

        var mem = new Memory<Byte>(new Byte[1]);
        while (!this.is_finished)
        {
            var next_or_null = await this.connection.ReadMessageAsync<ValueTuple<T>?>((br, _) =>
            {
                var next_status = br.ReadEnum<RpcEnumerable.ENextItemStatus>();
                switch (next_status)
                {
                    case RpcEnumerable.ENextItemStatus.Stopped:
                        this.is_finished = true;
                        return null;
                    case RpcEnumerable.ENextItemStatus.Ping:
                        return null;
                    case RpcEnumerable.ENextItemStatus.Sending:
                        var item = br.ReadData<T>();
                        return new(item);
                    default:
                        throw new InvalidDataException($"Unexpected {nameof(RpcEnumerable.ENextItemStatus)} value: {next_status}");
                }
            }, this.read_cancel_token);
            if (this.is_finished)
                yield break;
            if (next_or_null is not { } next)
                continue;
            yield return next.Item1;
        }

    }

    /// <summary>
    /// Closes the connection and prevents further reading of values
    /// </summary>
    public void Dispose()
    {
        this.is_finished = true;
        this.connection.Dispose();
    }

}

/// <summary>
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class RpcEnumerableSource<T> where T : notnull
{
    private readonly Lock l_subscribers_and_items = new();
    private readonly HashSet<Subscriber> subscribers = [];
    private readonly List<T> existing_items = [];
    private Boolean is_closed = false;
    private String? error_message = null;

    /// <summary>
    /// </summary>
    /// <param name="ping_stop_token"></param>
    /// <param name="ping_config">Default every 1 second if null. Can be set to Timeout.InfiniteTimeSpan to disable pinging</param>
    public RpcEnumerableSource(CancellationToken ping_stop_token, PingConfig? ping_config = null)
    {
        ping_config ??= new()
        {
            Interval = TimeSpan.FromSeconds(1),
        };
        if (ping_config is { Interval: var ping_interval } && ping_interval != Timeout.InfiniteTimeSpan)
        {
            var ping_thr = new Thread(PingLoop)
            {
                Name = $"{this} ping thread",
                IsBackground = true,
            };
            ping_thr.Start();
            void PingLoop()
            {
                try
                {
                    while (true)
                    {
                        Thread.Sleep(ping_interval);
                        using var lock_scope = this.l_subscribers_and_items.EnterScope();
                        this.ForEachSubscriber(subscriber => subscriber.SendPing());
                    }
                }
                catch (Exception ex) when (ex.IsOnlyNestedOpCanceledExceptions(ping_stop_token))
                {
                    // Ignore, service is stopping
                }
                catch (Exception ex)
                {
                    WinSvcCommon.HandleCriticalError(ex, when_doing: $"pinging subscribers of {this}");
                }
            }
        }
    }

    private event Action? on_closed = null;

    /// <summary>
    /// </summary>
    public event Action? OnAllDisconnected = null;

    /// <summary>
    /// </summary>
    public readonly struct PingConfig
    {
        /// <summary>
        /// </summary>
        public required TimeSpan Interval { get; init; }
    }
    /// <summary>
    /// Disabled if null
    /// <para/>
    /// Default is every 1 second
    /// </summary>
    public PingConfig? Pinging { get; init; } = new()
    {
        Interval = TimeSpan.FromSeconds(1),
    };

    // Cannot rely on this, because .Subscribe is called after exiting the client processing method
    //public Boolean IsConnected => this.subscribers.Any(subscriber => subscriber.IsConnected);

    /// <summary>
    /// </summary>
    public void CheckDisconnected()
    {
        using var lock_scope = this.l_subscribers_and_items.EnterScope();
        this.ForEachSubscriber(_ => { });
    }

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
    /// <param name="error_message"></param>
    public void Close(String? error_message)
    {
        using var lock_scope = this.l_subscribers_and_items.EnterScope();
        if (this.is_closed)
            throw new InvalidOperationException($"{this} is already closed");
        this.is_closed = true;
        this.error_message = error_message;
        this.ForEachSubscriber(subscriber => subscriber.Close(error_message));
        this.on_closed?.Invoke();
    }
    /// <summary>
    /// </summary>
    public Boolean IsClosed => this.is_closed;

    /// <summary>
    /// Either runs the given action immediately, or adds it as a handler for <see cref="Close(String?)"/> method
    /// </summary>
    /// <param name="act"></param>
    public void RunOnClosed(Action act)
    {
        using var lock_scope = this.l_subscribers_and_items.EnterScope();
        if (this.is_closed)
            act.Invoke();
        else
            this.on_closed += act;
    }

    internal void ForEachSubscriber(Action<Subscriber> act)
    {
        var removed_count = this.subscribers.RemoveWhere(subscriber =>
        {
            if (!subscriber.IsConnected)
            {
                subscriber.Dispose();
                return true;
            }
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
        if (removed_count != 0 && this.subscribers.Count == 0)
            this.OnAllDisconnected?.Invoke();
    }

    /// <summary>
    /// </summary>
    public Subscriber Subscribe(RpcConnection connection)
    {
        using var lock_scope = this.l_subscribers_and_items.EnterScope();
        var subscriber = new Subscriber(this, connection, this.existing_items);
        if (this.is_closed)
            subscriber.Close(this.error_message);
        else
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
        private readonly RpcConnection connection;

        internal Subscriber(RpcEnumerableSource<T> source, RpcConnection connection, ICollection<T> existing_items)
        {
            this.source = source;
            this.connection = connection;
            connection.WriteMessage((bw, cancel_token) =>
            {
                bw.Write(existing_items.Count);
                foreach (var item in existing_items)
                {
                    cancel_token.ThrowIfCancellationRequested();
                    bw.WriteData(item);
                }
            });
        }

        internal Boolean IsConnected => this.connection.IsConnected;

        internal void Send(T item) => this.connection.WriteMessage((bw, _) =>
        {
            bw.WriteEnum(RpcEnumerable.ENextItemStatus.Sending);
            bw.WriteData(item);
        });

        internal void SendPing() => this.connection.WriteMessage((bw, _) => bw.WriteEnum(RpcEnumerable.ENextItemStatus.Ping));

        internal void Close(String? error_message)
        {
            if (error_message is { })
            {
                this.connection.ReportError(error_message);
                return;
            }
            this.connection.WriteMessage((bw, _) => bw.WriteEnum(RpcEnumerable.ENextItemStatus.Stopped));
            this.connection.FinishWithoutError();
        }

        /// <summary>
        /// </summary>
        public void Dispose()
        {
            //GlobalLog.AddMessage($"Disposing {this}\n{Environment.StackTrace}");
            this.connection.Dispose();
        }

        /// <summary>
        /// </summary>
        public override String ToString() =>
            $"{nameof(RpcEnumerableSource<>)}<{typeof(T).Name}>.{nameof(Subscriber)} => {this.connection}";

    }

}
