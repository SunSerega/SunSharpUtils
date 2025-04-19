using System;

using System.Threading;

using System.Linq;
using System.Collections.Generic;

using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.Threading;

/// <summary>
/// This class is used to restart updates to multiple targets on a delay    <br/>
/// Works similarly to Timer, but doesn't hold reference to any target,     <br/>
/// and uses common thread for all targets                                  <br/>
/// </summary>
public sealed class DelayedRestarter<TKey>
    where TKey : notnull
{
    private static String ClassName => $"{nameof(DelayedRestarter<TKey>)}<{typeof(TKey)}>";

    private sealed class KeyHolder(TKey key)
    {
        public TKey Key => key;
    }
    private sealed class KeyData(TKey key, TimeSpan restart_delay)
    {
        public TKey Key { get; } = key;

        public TimeSpan RestartDelay { get; set; } = restart_delay;
        public DateTime? LastStart { get; set; } = null;

        public DateTime? NextStart => LastStart + RestartDelay;
        public TimeSpan RemainingWait => NextStart - DateTime.Now ?? TimeSpan.Zero;

    }

    private readonly ManualResetEventSlim ev = new(false);
    private readonly List<KeyData> restartables = [];

    /// <summary>
    /// </summary>
    public String DebugString => restartables.Select(data => $"{data.Key}: {data.RemainingWait}").JoinToString(" | ");

    /// <summary>
    /// How many restarts can be stacked in wait before on_fall_behind is called and the excess updates are discarded
    /// </summary>
    public static Int32 FallBehindMeasure { get; set; } = 3;

    private static ThreadStart MakeThreadStart(
        Boolean is_background,
        Action<TKey> update,
        Action<TKey> on_fall_behind,
        ManualResetEventSlim ev,
        List<KeyData> restartables
    ) => () =>
    {
        while (true)
            try
            {
                ObjectLocker? locker = new ObjectLocker(restartables);
                void Unlock()
                {
                    locker?.Dispose();
                    locker = null;
                }
                try
                {
                    if (restartables.Count == 0)
                    {
                        Unlock();
                        ev.Wait();
                        ev.Reset();
                        continue;
                    }

                    var data = restartables.MinBy(data => data.NextStart ?? DateTime.MinValue);
                    if (data is null)
                        continue;

                    {
                        var wait = data.RemainingWait;
                        if (wait > TimeSpan.Zero)
                        {
                            Unlock();
                            var was_set = ev.Wait(wait);
                            ev.Reset();
                            if (was_set)
                                continue;
                        }
                    }
                    Unlock();

                    data.LastStart += data.RestartDelay;
                    data.LastStart ??= DateTime.Now;
                    var min_last_start = DateTime.Now - data.RestartDelay*FallBehindMeasure;
                    if (data.LastStart < min_last_start)
                    {
                        on_fall_behind.Invoke(data.Key);
                        data.LastStart = min_last_start;
                    }

                    ThreadingCommon.RunWithBackgroundReset(() => update(data.Key), is_background);
                }
                finally
                {
                    locker?.Dispose();
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
    /// </summary>
    /// <param name="update">An action to run when delay expires</param>
    /// <param name="on_fall_behind">An action to run when FallBehindMeasure restarts for the key get stacked in wait</param>
    /// <param name="description">Used for thread name</param>
    /// <param name="is_background">If false, the app can't be shut down in the middle of executing update</param>
    public DelayedRestarter(Action<TKey> update, Action<TKey> on_fall_behind, String description, Boolean is_background)
    {
        var thr = new Thread(MakeThreadStart(is_background, update, on_fall_behind, this.ev, this.restartables))
        {
            IsBackground = true,
            Name = $"{ClassName}: {description}",
        };
        thr.SetApartmentState(ApartmentState.STA);
        thr.Start();
    }

    private Int32? FindInd(TKey key)
    {
        for (var i = 0; i < restartables.Count; i++)
            if (restartables[i].Key.Equals(key))
                return i;
        return null;
    }

    /// <summary>
    /// </summary>
    public void Add(TKey key, TimeSpan restart_delay)
    {
        using var locker = new ObjectLocker(restartables);
        if (FindInd(key) is not null)
            throw new InvalidOperationException($"Key {key} has already been added");
        restartables.Add(new(key, restart_delay));
        ev.Set();
    }

    /// <summary>
    /// </summary>
    public void Update(TKey key, TimeSpan restart_delay)
    {
        using var locker = new ObjectLocker(restartables);
        var ind = FindInd(key) ?? throw new InvalidOperationException($"Key {key} not found");
        restartables[ind].RestartDelay = restart_delay;
        ev.Set();
    }

    /// <summary>
    /// </summary>
    public void Remove(TKey key)
    {
        using var locker = new ObjectLocker(restartables);
        var ind = FindInd(key) ?? throw new InvalidOperationException($"Key {key} not found");
        restartables.RemoveAt(ind);
        ev.Set();
    }

    /// <summary>
    /// </summary>
    ~DelayedRestarter() => Err.Handle($"{ClassName} is not supposed to ever go out of scope");

}
