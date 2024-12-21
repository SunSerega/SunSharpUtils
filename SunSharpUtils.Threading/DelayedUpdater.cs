using System;

using System.Threading;

using System.Linq;
using System.Collections.Concurrent;

namespace SunSharpUtils.Threading;

internal readonly struct DelayedUpdateSpec
{
    public readonly DateTime earliest_time;
    public readonly DateTime urgent_time;

    private DelayedUpdateSpec(DateTime earliest_time, DateTime urgent_time)
    {
        this.earliest_time = earliest_time;
        this.urgent_time = urgent_time;
    }

    public static DelayedUpdateSpec FromDelay(TimeSpan delay, bool can_delay_further)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delay, TimeSpan.Zero);
        var time = DateTime.Now + delay;
        if (can_delay_further)
            return new(time, DateTime.MaxValue);
        else
            // Why earliest_time is also set:
            // If the previous delay was 1s and the new delay is urgent 2s, do still delay for 2s
            return new(time, time);
    }

    public TimeSpan GetRemainingWait() => earliest_time - DateTime.Now;

    public static DelayedUpdateSpec Combine(DelayedUpdateSpec prev, DelayedUpdateSpec next, out bool need_ev_set)
    {
        need_ev_set = false;

        var earliest_time = prev.earliest_time;
        var urgent_time = prev.urgent_time;

        if (earliest_time < next.earliest_time)
            earliest_time = next.earliest_time;
        if (urgent_time > next.urgent_time)
            urgent_time = next.urgent_time;
        if (earliest_time > urgent_time)
        {
            earliest_time = urgent_time;
            need_ev_set = true;
        }

        return new(earliest_time, urgent_time);
    }

    public static bool operator ==(DelayedUpdateSpec a, DelayedUpdateSpec b) => a.earliest_time == b.earliest_time && a.urgent_time == b.urgent_time;
    public static bool operator !=(DelayedUpdateSpec a, DelayedUpdateSpec b) => !(a == b);
    public override bool Equals(object? obj) => obj is DelayedUpdateSpec spec && this == spec;

    public override int GetHashCode() => HashCode.Combine(earliest_time, urgent_time);

}

/// <summary>
/// This class is used to delay updates to a single target
/// If a new update is requested before the previous one is executed
/// and can_delay_further is set, then the previous update is discarded
/// This way a lot of updates can be requested, but only the last one will be executed
/// 
/// Note: The only guarantee is that the update will be executed at some point after the Trigger() call
/// There is no guarantee that the update will be delayed
/// It might start executing on previous delay after further delay has been requested
/// In that case it will be executed second time after the new delay
/// </summary>
public class DelayedUpdater
{
    private sealed class ActivationHolder
    {
        private DelayedUpdateSpec? requested;

        public bool IsRequested => requested.HasValue;

        public TimeSpan GetRemainingWait() => requested!.Value.GetRemainingWait();

        public void Clear()
        {
            using var this_locker = new ObjectLocker(this);
            requested = null;
        }

        public bool TryUpdate(DelayedUpdateSpec next)
        {
            using var this_locker = new ObjectLocker(this);
            var need_ev_set = true;
            if (requested.HasValue)
            {
                next = DelayedUpdateSpec.Combine(requested.Value, next, out need_ev_set);
                if (requested == next)
                    return false;
            }
            this.requested = next;
            return need_ev_set;
        }

    }

    private readonly ManualResetEventSlim ev = new(false);
    private readonly ActivationHolder activation = new();

    private static ThreadStart MakeThreadStart(
        Action update,
        ManualResetEventSlim ev,
        ActivationHolder activation
    ) => () =>
    {
        while (true)
            try
            {
                if (!activation.IsRequested)
                {
                    ev.Wait();
                    ev.Reset();
                    continue;
                }

                {
                    var wait = activation.GetRemainingWait();
                    if (wait > TimeSpan.Zero)
                    {
                        ev.Wait(wait);
                        ev.Reset();
                        continue;
                    }
                }

                activation.Clear();

                Thread.CurrentThread.IsBackground = false;
                try
                {
                    update();
                }
                finally
                {
                    Thread.CurrentThread.IsBackground = true;
                }

            }
            catch when (Common.IsShuttingDown)
            {
                break;
            }
            catch (Exception e)
            {
                Err.Handle(e);
            }
    };

    /// <summary>
    /// Initializes a new instance of DelayedUpdater
    /// </summary>
    /// <param name="update">An action to run when delay expires</param>
    /// <param name="description">Used for thread name</param>
    public DelayedUpdater(Action update, string description)
    {
        var thr = new Thread(MakeThreadStart(update, ev, activation))
        {
            IsBackground=true,
            Name = $"{nameof(DelayedUpdater)}: {description}",
        };
        thr.SetApartmentState(ApartmentState.STA);
        thr.Start();
    }

    /// <summary>
    /// Triggers an update in the future, or delays the update by <paramref name="delay"/> relative to now if already requested                 <br />
    /// If <paramref name="can_delay_further"/> is false, other calls to this function will not be able to delay the update further             <br />
    /// </summary>
    /// <param name="delay"></param>
    /// <param name="can_delay_further"></param>
    public void Trigger(TimeSpan delay, bool can_delay_further)
    {
        var next_time = DelayedUpdateSpec.FromDelay(delay, can_delay_further);
        if (activation.TryUpdate(next_time))
            ev.Set();
    }

    /// <summary>
    /// </summary>
    ~DelayedUpdater() => Err.Handle(new MessageException($"{nameof(DelayedUpdater)} is not supposed to ever go out of scope"));

}

/// <summary>
/// This class is used to delay updates to multiple targets
/// Works similarly to DelayedUpdater, but uses common thread for all updates
/// Note: This means than an update to one target will delay updates to all other targets
/// </summary>
public class DelayedMultiUpdater<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, DelayedUpdateSpec> updatables = new();
    private readonly ManualResetEventSlim ev = new(false);
    private readonly Action<TKey> update;

    private static string ClassName => $"{nameof(DelayedMultiUpdater<TKey>)}<{typeof(TKey)}>";

    private static ThreadStart MakeThreadStart(
        Action<TKey> update,
        ManualResetEventSlim ev,
        ConcurrentDictionary<TKey, DelayedUpdateSpec> updatables
    ) => () =>
    {
        while (true)
            try
            {
                if (updatables.IsEmpty)
                {
                    ev.Wait();
                    ev.Reset();
                    continue;
                }

                var kvp = updatables.MinBy(kvp => kvp.Value.earliest_time);

                {
                    var wait = kvp.Value.GetRemainingWait();
                    if (wait > TimeSpan.Zero)
                    {
                        ev.Wait(wait);
                        ev.Reset();
                        continue;
                    }
                }

                if (!updatables.TryRemove(kvp))
                    continue;

                try
                {
                    Thread.CurrentThread.IsBackground = false;
                    update(kvp.Key);
                }
                finally
                {
                    Thread.CurrentThread.IsBackground = true;
                }

            }
            catch when (Common.IsShuttingDown)
            {
                break;
            }
            catch (Exception e)
            {
                Err.Handle(e);
            }
    };
    /// <summary>
    /// Initializes a new instance of DelayedMultiUpdater
    /// </summary>
    /// <param name="update">An action to run when delay expires</param>
    /// <param name="description">Used for thread name</param>
    public DelayedMultiUpdater(Action<TKey> update, string description)
    {
        this.update = update;
        new Thread(MakeThreadStart(update, ev, updatables))
        {
            IsBackground = true,
            Name = $"{ClassName}: {description}",
        }.Start();
    }

    /// <summary>
    /// Triggers an update to <paramref name="key"/> in the future, or delays the update by <paramref name="delay"/> relative to now if already requested   <br />
    /// If <paramref name="can_delay_further"/> is false, other calls to this function will not be able to delay the update further                         <br />
    /// If <paramref name="can_delay_further"/> is null, trying to delay the update further will throw an exception                                         <br />
    /// </summary>
    /// <param name="key"></param>
    /// <param name="delay"></param>
    /// <param name="can_delay_further"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public void Trigger(TKey key, TimeSpan delay, bool? can_delay_further)
    {
        var next_val = DelayedUpdateSpec.FromDelay(delay, can_delay_further??false);
        var need_ev_set = true;
        updatables.AddOrUpdate(key, next_val, (key, prev_val) =>
        {
            if (can_delay_further is null)
                throw new InvalidOperationException();
            next_val = DelayedUpdateSpec.Combine(prev_val, next_val, out need_ev_set);
            if (prev_val == next_val)
                need_ev_set = false;
            return next_val;
        });
        if (need_ev_set)
            ev.Set();
    }

    /// <summary>
    /// </summary>
    ~DelayedMultiUpdater() => Err.Handle(new MessageException($"{ClassName} is not supposed to ever go out of scope"));

}