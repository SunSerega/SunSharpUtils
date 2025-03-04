using System;

namespace SunSharpUtils;

/// <summary>
/// Common stuff
/// </summary>
public static class Common
{

    private static Int32 is_shutting_down = 0;
    /// <summary>
    /// </summary>
    public static Boolean IsShuttingDown => is_shutting_down != 0;

    /// <summary>
    /// </summary>
    public static event Action<Int32>? OnShutdown = null;

    /// <summary>
    /// </summary>
    /// <param name="exit_code"></param>
    public static void Shutdown(Int32 exit_code)
    {
        if (System.Threading.Interlocked.Exchange(ref is_shutting_down, 1) != 0) return;
        OnShutdown?.Invoke(exit_code);
    }
    /// <summary>
    /// </summary>
    public static void Shutdown() => Shutdown(exit_code: 0);

}
