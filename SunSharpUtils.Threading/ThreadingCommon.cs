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
        var old_thread = Thread.CurrentThread;
        var old_is_background = old_thread.IsBackground;
        old_thread.IsBackground = new_is_background;
        return new LambdaDisposable(() =>
        {
            if (old_thread != Thread.CurrentThread)
                Err.Handle($"{nameof(ThreadingCommon)}.{nameof(TempSetIsBackground)}: Thread.CurrentThread changed before .Dispose, the thread's IsBackground will not be restored\n{Environment.StackTrace}");
            else
                old_thread.IsBackground = old_is_background;
        }, prevent_double_call: true);
    }

    /// <summary>
    /// Sets the thread's Name property to the specified value
    /// </summary>
    /// <param name="new_name"></param>
    /// <returns>A disposable that resets the thread's Name property to its original value</returns>
    public static IDisposable TempSetName(String new_name)
    {
        var old_thread = Thread.CurrentThread;
        var old_name = old_thread.Name;
        old_thread.Name = new_name;
        return new LambdaDisposable(() =>
        {
            if (old_thread != Thread.CurrentThread)
                Err.Handle($"{nameof(ThreadingCommon)}.{nameof(TempSetName)}: Thread.CurrentThread changed before .Dispose, the thread's Name will not be restored\n{Environment.StackTrace}");
            else
                old_thread.Name = old_name;
        }, prevent_double_call: true);
    }

    /// <summary>
    /// Runs action under this lock
    /// </summary>
    /// <param name="l"></param>
    /// <param name="act"></param>
    public static void LockedDo(this Lock l, Action act)
    {
        //var hash = l.GetHashCode();
        //var trace = Environment.StackTrace;
        //Prompt.Notify($"LockedDo [{hash}] 1:\n{trace}");
        lock (l)
        {
            //Prompt.Notify($"LockedDo [{hash}] 2:\n{trace}");
            act();
            //Prompt.Notify($"LockedDo [{hash}] 3:\n{trace}");
        }
    }

    /// <summary>
    /// Runs the function under this lock, returning its result
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="l"></param>
    /// <param name="get"></param>
    /// <returns></returns>
    public static T LockedGet<T>(this Lock l, Func<T> get)
    {
        //var hash = l.GetHashCode();
        //var trace = Environment.StackTrace;
        //Prompt.Notify($"LockedGet [{hash}] 1:\n{trace}");
        lock (l)
        {
            //Prompt.Notify($"LockedGet [{hash}] 2:\n{trace}");
            var result = get();
            //Prompt.Notify($"LockedGet [{hash}] 3:\n{trace}");
            return result;
        }
    }

}
