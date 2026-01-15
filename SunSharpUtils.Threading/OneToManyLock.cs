using System;
using System.Runtime.CompilerServices;
using System.Threading;

namespace SunSharpUtils.Threading;

/// <summary>
/// Can be unlocked, or locked in one of two modes: One or Many                     <br/>
/// 1. One: Works like a regular lock, with only one thread allowed at a time       <br/>
/// 2. Many: Any number of threads allowed, but all of them must be in Many mode    <br/>
/// <br/>
/// One mode can be given priority - in that case new threads in Many mode will not queue up until all threads in One mode have finished executing
/// </summary>
public sealed class OneToManyLock()
{
    private readonly Object sync_lock = new();

    // Set when not locked
    private readonly ManualResetEventSlim one_wh = new(true);
    private readonly ManualResetEventSlim many_wh = new(true);

    // How many threads currently have the lock
    private volatile Int32 doing_one = 0;
    private volatile Int32 doing_many = 0;
    // Side A can execute if at any moment doing_B is zero, after doing_A has been set to non-zero

    private Int64 last_many_lock_end_ticks;

    // *LockedState.Begin methods:
    // - Not the constructor, because .End needs to be called even if .Begin fails midway

    #region One

    /// <summary>
    /// </summary>
    public struct OneLockedState(OneToManyLock root) : IDisposable
    {
        private readonly OneToManyLock root = root;
        private Boolean is_locked = false;
        private Boolean need_dec = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void Inc()
        {
            Interlocked.Increment(ref this.root.doing_one);
            this.root.one_wh.Reset();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void Dec()
        {
            if (0 != Interlocked.Decrement(ref this.root.doing_one))
                return;
            this.root.one_wh.Set();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BeginWithPriority()
        {
            this.Inc();
            this.need_dec = true;

            while (true)
            {
                if (this.root.doing_many == 0)
                    break;
                this.root.many_wh.Wait();
            }

        }

        /// <summary>
        /// </summary>
        /// <param name="one_lock_delay">If non-zero, will wait extra time after all many state are unlocked, before trying to lock this one state</param>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void BeginWithoutPriority(TimeSpan one_lock_delay)
        {

            while (true)
            {
                // Extra check, to not slow down the high volume work that needs many-locked state
                if (this.root.doing_many == 0)
                {
                    this.Inc();
                    if (this.root.doing_many == 0)
                        break;
                    this.Dec();
                }
                this.root.many_wh.Wait();
                if (one_lock_delay != TimeSpan.Zero)
                {
                    var end_time = new DateTime(ticks: this.root.last_many_lock_end_ticks) + one_lock_delay;
                    var wait_time = end_time - DateTime.UtcNow;
                    if (wait_time > TimeSpan.Zero)
                        Thread.Sleep(wait_time);
                }
            }

            this.need_dec = true;
        }

        /// <summary>
        /// </summary>
        /// <param name="with_priority">If true, will not allow any new many states to lock, until this one state is unlocked</param>
        /// <exception cref="InvalidOperationException"></exception>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Begin(Boolean with_priority)
        {
            if (this.is_locked)
                throw new InvalidOperationException();

            if (with_priority)
                this.BeginWithPriority();
            else
                this.BeginWithoutPriority(one_lock_delay: TimeSpan.Zero);

            Monitor.Enter(this.root.sync_lock);
            this.is_locked = true;
        }

        /// <summary>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void End()
        {
            if (this.need_dec)
            {
                this.Dec();
                this.need_dec = false;
            }

            if (this.is_locked)
            {
                Monitor.Exit(this.root.sync_lock);
                this.is_locked = false;
            }
        }
        void IDisposable.Dispose() => this.End();

    }
    /// <summary>
    /// </summary>
    public OneLockedState NewOneState() => new(this);

    /// <summary>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T OneLocked<T>(Func<T> act, Boolean with_priority)
    {
        using var state = this.NewOneState();
        state.Begin(with_priority);
        return act();
    }

    /// <summary>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OneLocked(Action act, Boolean with_priority)
    {
        using var state = this.NewOneState();
        state.Begin(with_priority);
        act();
    }

    #endregion

    #region Many

    /// <summary>
    /// </summary>
    public struct ManyLockedState(OneToManyLock root) : IDisposable
    {
        private readonly OneToManyLock root = root;
        private Boolean is_locked = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void Inc()
        {
            Interlocked.Increment(ref this.root.doing_many);
            this.root.many_wh.Reset();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void Dec()
        {
            if (0 != Interlocked.Decrement(ref this.root.doing_many))
                return;
            this.root.many_wh.Set();
        }

        /// <summary>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Begin()
        {
            if (this.is_locked)
                throw new InvalidOperationException();

            while (true)
            {
                this.Inc();
                if (this.root.doing_one == 0)
                    break;
                this.Dec();
                this.root.one_wh.Wait();
            }

            this.is_locked = true;
        }

        /// <summary>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void End()
        {
            if (!this.is_locked)
                return;
            //TODO Only need this if the one lock would be used with 1. no priority, and 2. with delay
            // - The same lock would probably be either always with priority, or always without
            ThreadingCommon.InterlockedSetMax(ref this.root.last_many_lock_end_ticks, DateTime.UtcNow.Ticks);
            this.Dec();
            this.is_locked = false;
        }
        void IDisposable.Dispose() => this.End();

    }
    /// <summary>
    /// </summary>
    public ManyLockedState NewManyState() => new(this);

    /// <summary>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T ManyLocked<T>(Func<T> act)
    {
        using var state = this.NewManyState();
        state.Begin();
        return act();
    }

    /// <summary>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ManyLocked(Action act)
    {
        using var state = this.NewManyState();
        state.Begin();
        act();
    }

    #endregion

}
