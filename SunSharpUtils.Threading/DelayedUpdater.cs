using System;

using System.Threading;

using System.Linq;
using System.Collections.Concurrent;

namespace SunSharpUtils.Threading;

/// <summary>
/// Represents a specification for a delayed update
/// </summary>
public readonly struct DelayedUpdateSpec
{
    /// <summary>
    /// </summary>
    public readonly DateTime earliest_time;
    /// <summary>
    /// </summary>
    public readonly DateTime urgent_time;

    private DelayedUpdateSpec(DateTime earliest_time, DateTime urgent_time)
    {
        this.earliest_time = earliest_time;
        this.urgent_time = urgent_time;
    }

    /// <summary>
    /// </summary>
    public static DelayedUpdateSpec FromDelay(TimeSpan earliest_delay, TimeSpan urgent_delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(urgent_delay, earliest_delay);
        ArgumentOutOfRangeException.ThrowIfLessThan(earliest_delay, TimeSpan.Zero);
        var now = DateTime.Now;
        return new(now + earliest_delay, now + urgent_delay);
    }

    /// <summary>
    /// </summary>
    public static DelayedUpdateSpec Now => FromDelay(TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>
    /// Creates delay to postpone the update by <paramref name="delay"/> relative to now
    /// </summary>
    /// <param name="delay"></param>
    /// <returns></returns>
    public static DelayedUpdateSpec Postpone(TimeSpan delay) => FromDelay(delay, TimeSpan.MaxValue);

    /// <summary>
    /// Creates delay to trigger update no later than <paramref name="delay"/> relative to now              <br/>
    /// Additionally, makes sure update doesn't happen earlier, unless overridden by a more urgent trigger  <br/>
    /// </summary>
    /// <param name="delay"></param>
    /// <returns></returns>
    public static DelayedUpdateSpec Urgent(TimeSpan delay) => FromDelay(delay, delay);

    internal TimeSpan GetRemainingWait() => earliest_time - DateTime.Now;

    internal static DelayedUpdateSpec Combine(DelayedUpdateSpec prev, DelayedUpdateSpec next, out bool need_ev_set)
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

    /// <summary>
    /// </summary>
    public static bool operator ==(DelayedUpdateSpec a, DelayedUpdateSpec b) => a.earliest_time == b.earliest_time && a.urgent_time == b.urgent_time;
    /// <summary>
    /// </summary>
    public static bool operator !=(DelayedUpdateSpec a, DelayedUpdateSpec b) => !(a == b);
    /// <summary>
    /// </summary>
    public override bool Equals(object? obj) => obj is DelayedUpdateSpec spec && this == spec;

    /// <summary>
    /// </summary>
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
    /// Triggers an update in future, or delays an already requested one
    /// </summary>
    /// <param name="spec"></param>
    public void Trigger(DelayedUpdateSpec spec)
    {
        if (activation.TryUpdate(spec))
            ev.Set();
    }

    /// <summary>
    /// </summary>
    public void TriggerNow() => Trigger(DelayedUpdateSpec.Now);
    /// <summary>
    /// </summary>
    public void TriggerPostpone(TimeSpan delay) => Trigger(DelayedUpdateSpec.Postpone(delay));
    /// <summary>
    /// </summary>
    public void TriggerUrgent(TimeSpan delay) => Trigger(DelayedUpdateSpec.Urgent(delay));

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
    /// Triggers an update to <paramref name="key"/> in future, or delays an already requested one
    /// </summary>
    /// <param name="key"></param>
    /// <param name="spec"></param>
    public void Trigger(TKey key, DelayedUpdateSpec spec)
    {
        var next_val = spec;
        var need_ev_set = true;
        updatables.AddOrUpdate(key, next_val, (key, prev_val) =>
        {
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
    public void TriggerNow(TKey key) => Trigger(key, DelayedUpdateSpec.Now);
    /// <summary>
    /// </summary>
    public void TriggerPostpone(TKey key, TimeSpan delay) => Trigger(key, DelayedUpdateSpec.Postpone(delay));
    /// <summary>
    /// </summary>
    public void TriggerUrgent(TKey key, TimeSpan delay) => Trigger(key, DelayedUpdateSpec.Urgent(delay));

    /// <summary>
    /// </summary>
    ~DelayedMultiUpdater() => Err.Handle(new MessageException($"{ClassName} is not supposed to ever go out of scope"));

}