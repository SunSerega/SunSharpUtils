using System;

using System.Threading;

using System.Runtime.CompilerServices;

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

    // *LockedState.Begin methods:
    // - Not the constructor, because .End needs to be called even if .Begin fails midway

    #region One

    /// <summary>
    /// </summary>
    public struct OneLockedState(OneToManyLock root) : IDisposable
    {
        private Boolean is_locked = false;
        private Boolean need_dec = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void Inc()
        {
            Interlocked.Increment(ref root.doing_one);
            root.one_wh.Reset();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void Dec()
        {
            if (0 != Interlocked.Decrement(ref root.doing_one))
                return;
            root.one_wh.Set();
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BeginWithPriority()
        {
            Inc();
            need_dec = true;

            while (true)
            {
                if (root.doing_many == 0)
                    break;
                root.many_wh.Wait();
            }

        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private void BeginWithoutPriority()
        {

            while (true)
            {
                Inc();
                if (root.doing_many == 0)
                    break;
                Dec();
                root.many_wh.Wait();
            }

            need_dec = true;
        }

        /// <summary>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Begin(Boolean with_priority)
        {
            if (is_locked)
                throw new InvalidOperationException();

            if (with_priority)
                BeginWithPriority();
            else
                BeginWithoutPriority();

            Monitor.Enter(root.sync_lock);
            is_locked = true;
        }

        /// <summary>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void End()
        {
            if (need_dec)
            {
                Dec();
                need_dec = false;
            }

            if (is_locked)
            {
                Monitor.Exit(root.sync_lock);
                is_locked = false;
            }
        }
        void IDisposable.Dispose() => End();

    }
    /// <summary>
    /// </summary>
    public OneLockedState NewOneState() => new(this);

    /// <summary>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T OneLocked<T>(Func<T> act, Boolean with_priority)
    {
        using var state = NewOneState();
        state.Begin(with_priority);
        return act();
    }

    /// <summary>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void OneLocked(Action act, Boolean with_priority)
    {
        using var state = NewOneState();
        state.Begin(with_priority);
        act();
    }

    #endregion

    #region Many

    /// <summary>
    /// </summary>
    public struct ManyLockedState(OneToManyLock root) : IDisposable
    {
        private Boolean is_locked = false;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void Inc()
        {
            Interlocked.Increment(ref root.doing_many);
            root.many_wh.Reset();
        }
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private readonly void Dec()
        {
            if (0 != Interlocked.Decrement(ref root.doing_many))
                return;
            root.many_wh.Set();
        }

        /// <summary>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void Begin()
        {
            if (is_locked)
                throw new InvalidOperationException();

            while (true)
            {
                Inc();
                if (root.doing_one == 0)
                    break;
                Dec();
                root.one_wh.Wait();
            }

            is_locked = true;
        }

        /// <summary>
        /// </summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public void End()
        {
            if (!is_locked)
                return;
            Dec();
            is_locked = false;
        }
        void IDisposable.Dispose() => End();

    }
    /// <summary>
    /// </summary>
    public ManyLockedState NewManyState() => new(this);

    /// <summary>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public T ManyLocked<T>(Func<T> act)
    {
        using var state = NewManyState();
        state.Begin();
        return act();
    }

    /// <summary>
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public void ManyLocked(Action act)
    {
        using var state = NewManyState();
        state.Begin();
        act();
    }

    #endregion

}
