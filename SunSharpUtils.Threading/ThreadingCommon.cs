using System;

using System.Threading;

namespace SunSharpUtils.Threading;

/// <summary>
/// Common threading utilities
/// </summary>
public static class ThreadingCommon
{

    /// <summary>
    /// Sets target to new_value if it's bigger than target
    /// </summary>
    /// <param name="target"></param>
    /// <param name="new_value"></param>
    public static void InterlockedSetMax(ref Int64 target, Int64 new_value)
    {
        var old_value = target;
        while (true)
        {
            if (old_value >= new_value)
                return;
            var found_value = Interlocked.CompareExchange(ref target, new_value, old_value);
            if (found_value == old_value)
                return;
            old_value = found_value;
        }
    }

    /// <summary>
    /// Runs an action with the background flag set to the specified value, then resets it to the original value
    /// </summary>
    /// <param name="act"></param>
    /// <param name="new_is_background"></param>
    public static void RunWithBackgroundReset(Action act, Boolean new_is_background)
    {
        var is_background = Thread.CurrentThread.IsBackground;
        Thread.CurrentThread.IsBackground = new_is_background;
		try
		{
            act();
		}
		finally
		{
			Thread.CurrentThread.IsBackground = is_background;
        }
    }

    /// <summary>
    /// Locks the object and runs the get function, returning its result
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="l"></param>
    /// <param name="get"></param>
    /// <returns></returns>
    public static T LockedGet<T>(this Object l, Func<T> get)
    {
        lock (l)
            return get();
    }

}
