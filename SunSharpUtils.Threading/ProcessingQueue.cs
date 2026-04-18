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

    /// <summary>
    /// </summary>
    public ProcessingQueue(IEnumerable<T> items) : this()
    {
        foreach (T item in items)
            this.Enqueue(item);
    }

    /// <summary>
    /// </summary>
    public void Enqueue(T item)
    {
        this.items.Enqueue(item);
        this.ev.Set();
    }

    /// <summary>
    /// </summary>
    public IEnumerable<T> DequeueAll()
    {
        while (this.items.TryDequeue(out var item))
            yield return item;
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
            while (!cancel_token.IsCancellationRequested)
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
                    break;
                }
                catch (Exception ex)
                {
                    Err.Handle(ex);
                }
            }
        }
    }

    IEnumerator<T> IEnumerable<T>.GetEnumerator() => this.items.GetEnumerator();
    IEnumerator IEnumerable.GetEnumerator() => this.items.GetEnumerator();

}
