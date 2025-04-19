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
    public static DelayedUpdateSpec FromDelay(TimeSpan earliest_delay, TimeSpan? urgent_delay)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(urgent_delay??TimeSpan.MaxValue, earliest_delay);
        ArgumentOutOfRangeException.ThrowIfLessThan(earliest_delay, TimeSpan.Zero);
        var now = DateTime.Now;
        return new(now + earliest_delay, now + urgent_delay ?? DateTime.MaxValue);
    }

    /// <summary>
    /// </summary>
    public static DelayedUpdateSpec Now => FromDelay(TimeSpan.Zero, TimeSpan.Zero);

    /// <summary>
    /// Creates delay to postpone the update by <paramref name="delay"/> relative to now
    /// </summary>
    /// <param name="delay"></param>
    /// <returns></returns>
    public static DelayedUpdateSpec Postpone(TimeSpan delay) => FromDelay(delay, null);

    /// <summary>
    /// Creates delay to trigger update no later than <paramref name="delay"/> relative to now              <br/>
    /// Additionally, makes sure update doesn't happen earlier, unless overridden by a more urgent trigger  <br/>
    /// </summary>
    /// <param name="delay"></param>
    /// <returns></returns>
    public static DelayedUpdateSpec Urgent(TimeSpan delay) => FromDelay(delay, delay);

    internal TimeSpan GetRemainingWait() => earliest_time - DateTime.Now;

    internal static DelayedUpdateSpec Combine(DelayedUpdateSpec prev, DelayedUpdateSpec next, out Boolean need_ev_set)
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
    public static Boolean operator ==(DelayedUpdateSpec a, DelayedUpdateSpec b) => a.earliest_time == b.earliest_time && a.urgent_time == b.urgent_time;
    /// <summary>
    /// </summary>
    public static Boolean operator !=(DelayedUpdateSpec a, DelayedUpdateSpec b) => !(a == b);
    /// <summary>
    /// </summary>
    public override Boolean Equals(Object? obj) => obj is DelayedUpdateSpec spec && this == spec;

    /// <summary>
    /// </summary>
    public override Int32 GetHashCode() => HashCode.Combine(earliest_time, urgent_time);

}

/// <summary>
/// This class is used to delay updates to a single target                                                  <br/>
/// If a new update is requested before the previous one is executed                                        <br/>
/// and can_delay_further is set, then the previous update is discarded                                     <br/>
/// This way a lot of updates can be requested, but only the last one will be executed                      <br/>
///                                                                                                         <br/>
/// Note: The only guarantee is that the update will be executed at some point after the Trigger() call     <br/>
/// There is no guarantee that the update will be delayed                                                   <br/>
/// It might start executing on previous delay after further delay has been requested                       <br/>
/// In that case it will be executed second time after the new delay                                        <br/>
/// </summary>
public sealed class DelayedUpdater
{
    private sealed class ActivationHolder
    {
        private DelayedUpdateSpec? requested;

        public Boolean IsRequested => requested.HasValue;

        public TimeSpan GetRemainingWait() => requested!.Value.GetRemainingWait();

        public void Clear()
        {
            using var this_locker = new ObjectLocker(this);
            requested = null;
        }

        public Boolean TryUpdate(DelayedUpdateSpec next)
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
        Boolean is_background,
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

                ThreadingCommon.RunWithBackgroundReset(update, is_background);
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
    /// </summary>
    /// <param name="update">An action to run when delay expires</param>
    /// <param name="description">Used for thread name</param>
    /// <param name="is_background">If false, the app can't be shut down in the middle of executing update</param>
    public DelayedUpdater(Action update, String description, Boolean is_background)
    {
        var thr = new Thread(MakeThreadStart(is_background, update, this.ev, this.activation))
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
    ~DelayedUpdater() => Err.Handle($"{nameof(DelayedUpdater)} is not supposed to ever go out of scope");

}

/// <summary>
/// This class is used to delay updates to multiple targets                                     <br/>
/// Works similarly to DelayedUpdater, but uses common thread for all updates                   <br/>
/// Note: This means than an update to one target will delay updates to all other targets       <br/>
/// Note2: If update is scheduled, reference to a key will be held until the update is done     <br/>
/// </summary>
public sealed class DelayedMultiUpdater<TKey>
    where TKey : notnull
{
    private readonly ConcurrentDictionary<TKey, DelayedUpdateSpec> updatables = new();
    private readonly ManualResetEventSlim ev = new(false);

    private static String ClassName => $"{nameof(DelayedMultiUpdater<TKey>)}<{typeof(TKey)}>";

    private static ThreadStart MakeThreadStart(
        Boolean is_background,
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

                ThreadingCommon.RunWithBackgroundReset(() => update(kvp.Key), is_background);
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
    /// </summary>
    /// <param name="update">An action to run when delay expires</param>
    /// <param name="description">Used for thread name</param>
    /// <param name="is_background">If false, the app can't be shut down in the middle of executing update</param>
    public DelayedMultiUpdater(Action<TKey> update, String description, Boolean is_background)
    {
        var thr = new Thread(MakeThreadStart(is_background, update, this.ev, this.updatables))
        {
            IsBackground = true,
            Name = $"{ClassName}: {description}",
        };
        thr.SetApartmentState(ApartmentState.STA);
        thr.Start();
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
    ~DelayedMultiUpdater() => Err.Handle($"{ClassName} is not supposed to ever go out of scope");

}