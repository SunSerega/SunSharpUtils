using System;

using System.Threading;

namespace SunSharpUtils.Threading;

/// <summary>
/// Common threading utilities
/// </summary>
public static class ThreadingCommon
{

    /// <summary>
    /// Runs an action with the background flag set to the specified value, then resets it to the original value
    /// </summary>
    /// <param name="act"></param>
    /// <param name="new_is_background"></param>
    public static void RunWithBackgroundReset(Action act, bool new_is_background)
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

}
