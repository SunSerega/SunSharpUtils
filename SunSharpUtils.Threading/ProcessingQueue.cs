using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

using SunSharpUtils.Ext.Exceptions;

namespace SunSharpUtils.Threading;

/// <summary>
/// ConcurrentQueue + ManualResetEventSlim
/// Allows adding items from many threads and then processing them in one new thread
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class ProcessingQueue<T>() : IEnumerable<T>
{
    private readonly ConcurrentQueue<T> items = [];
    private readonly ManualResetEventSlim ev = new(initialState: false);
    private Boolean processing_started = false;
    private Boolean need_clear = false;
    private readonly ManualResetEventSlim processing_stopped = new(initialState: false);

    /// <summary>
    /// </summary>
    public ProcessingQueue(IEnumerable<T> items) : this()
    {
        foreach (T item in items)
            this.Enqueue(item);
    }

    /// <summary>
    /// </summary>
    public Int32 PendingCount => this.items.Count;

    /// <summary>
    /// </summary>
    public void WaitUntilProcessingFullyStopped(Boolean should_clear)
    {
        if (should_clear)
            this.need_clear = true;
        this.processing_stopped.Wait();
    }

    /// <summary>
    /// </summary>
    public void Enqueue(T item)
    {
        if (this.processing_stopped.IsSet)
            throw new InvalidOperationException($"{nameof(ProcessingQueue<>)} already stopped processing. This is a race condition");
        if (this.need_clear)
            throw new InvalidOperationException($"{nameof(ProcessingQueue<>)} has already been cleared. This might be a race condition");
        this.items.Enqueue(item);
        this.ev.Set();
    }

    private IEnumerable<T> DequeueAll()
    {
        while (true)
        {
            if (this.need_clear)
                this.items.Clear();
            if (!this.items.TryPeek(out var item))
                yield break;
            yield return item;
            if (!this.items.TryDequeue(out var item_deq) || !EqualityComparer<T>.Default.Equals(item, item_deq))
                throw new InvalidOperationException($"Race condition: Multiple threads consuming the queue of {this}");
        }
    }

    /// <summary>
    /// </summary>
    public readonly struct ProcessingThreadConfig()
    {
        /// <summary>
        /// </summary>
        public required String UsedFor { get; init; }
        /// <summary>
        /// </summary>
        public required Action<IEnumerable<T>> OnNewItems { get; init; }
        /// <summary>
        /// </summary>
        public required CancellationToken CancelToken { get; init; }
        /// <summary>
        /// </summary>
        public Action<ManualResetEventSlim, CancellationToken>? DoWait { get; init; } = null;
    }
    /// <summary>
    /// Starts a new background thread, invoking the given action when new items appear
    /// </summary>
    /// <param name="config"></param>
    public Thread StartProcessingThread(ProcessingThreadConfig config)
    {
        if (this.processing_started)
            throw new InvalidOperationException($"{nameof(ProcessingQueue<>)} already started processing");
        this.processing_started = true;

        var on_new_items = config.OnNewItems;
        var cancel_token = config.CancelToken;
        var do_wait = config.DoWait;

        do_wait ??= (ev, cancel_token) => ev.Wait(cancel_token);

        var thr = new Thread(ProcessingLoop)
        {
            IsBackground = true,
            Name = $"{nameof(ProcessingQueue<>)}.{nameof(ProcessingLoop)} for {config.UsedFor}"
        };
        thr.Start();
        return thr;

        void ProcessingLoop()
        {
            while (!cancel_token.IsCancellationRequested || !this.items.IsEmpty)
            {
                try
                {
                    if (this.items.IsEmpty)
                    {
                        do_wait(this.ev, cancel_token);
                        this.ev.Reset();
                        continue;
                    }

                    on_new_items.Invoke(this.DequeueAll());
                }
                catch (Exception ex) when (cancel_token.IsCancellationRequested && ex.GetNestedExceptions().All(ex => ex is OperationCanceledException))
                {
                    continue; // Try process remaining items
                }
                catch (Exception ex)
                {
                    Err.Handle(ex);
                }
            }
            this.processing_stopped.Set();
        }
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => this.items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => this.items.GetEnumerator();

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(ProcessingQueue<>)}<{typeof(T)}>";

}
