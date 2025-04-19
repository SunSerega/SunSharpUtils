using System;

namespace SunSharpUtils;

/// <summary>
/// Helper class for forcing Common.DebugChecks to be true
/// </summary>
public static class DebugChecks
{
    internal static Boolean AreForcedOn { get; private set; } = false;

    /// <summary>
    /// </summary>
    /// <exception cref="InvalidOperationException">if already forced on</exception>
    public static void ForceOn()
    {
        if (AreForcedOn)
            throw new InvalidOperationException("DebugChecks are already forced on");
        AreForcedOn = true;
    }

}

/// <summary>
/// Common stuff
/// </summary>
public static class Common
{

    #region Shutdown

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

    #endregion

    #region Debug

    /// <summary>
    /// </summary>
#if DEBUG
    public static readonly Boolean NeedDebugChecks = DebugChecks.AreForcedOn || System.Diagnostics.Debugger.IsAttached;
#else
    public static readonly Boolean NeedDebugChecks = DebugChecks.AreForcedOn;
#endif

    #endregion

}
