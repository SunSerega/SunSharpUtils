using System;

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

    internal TimeSpan GetRemainingWait() => this.earliest_time - DateTime.Now;

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
    public override Int32 GetHashCode() => HashCode.Combine(this.earliest_time, this.urgent_time);

}
