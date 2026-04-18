using System;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;

namespace SunSharpUtils.Threading;

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

    private static String ClassName => $"{nameof(DelayedMultiUpdater<>)}<{typeof(TKey)}>";

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
        this.updatables.AddOrUpdate(key, next_val, (key, prev_val) =>
        {
            next_val = DelayedUpdateSpec.Combine(prev_val, next_val, out need_ev_set);
            if (prev_val == next_val)
                need_ev_set = false;
            return next_val;
        });
        if (need_ev_set)
            this.ev.Set();
    }

    /// <summary>
    /// </summary>
    public void TriggerNow(TKey key) => this.Trigger(key, DelayedUpdateSpec.Now);
    /// <summary>
    /// </summary>
    public void TriggerPostpone(TKey key, TimeSpan delay) => this.Trigger(key, DelayedUpdateSpec.Postpone(delay));
    /// <summary>
    /// </summary>
    public void TriggerUrgent(TKey key, TimeSpan delay) => this.Trigger(key, DelayedUpdateSpec.Urgent(delay));

    /// <summary>
    /// </summary>
    ~DelayedMultiUpdater() => Err.Handle($"{ClassName} is not supposed to ever go out of scope");

}