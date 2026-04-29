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
    /// Sets the thread's IsBackground property to the specified value
    /// </summary>
    /// <param name="new_is_background"></param>
    /// <returns>A disposable that resets the thread's IsBackground property to its original value</returns>
    public static IDisposable TempSetIsBackground(Boolean new_is_background)
    {
        var old_is_background = Thread.CurrentThread.IsBackground;
        Thread.CurrentThread.IsBackground = new_is_background;
        return new LambdaDisposable(() => Thread.CurrentThread.IsBackground = old_is_background);
    }

    /// <summary>
    /// Sets the thread's Name property to the specified value
    /// </summary>
    /// <param name="new_name"></param>
    /// <returns>A disposable that resets the thread's Name property to its original value</returns>
    public static IDisposable TempSetName(String new_name)
    {
        var old_name = Thread.CurrentThread.Name;
        Thread.CurrentThread.Name = new_name;
        return new LambdaDisposable(() => Thread.CurrentThread.Name = old_name);
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
