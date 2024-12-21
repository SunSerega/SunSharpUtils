using System;

namespace SunSharpUtils;

/// <summary>
/// Common stuff
/// </summary>
public static class Common
{

    /// <summary>
    /// </summary>
    public static bool IsShuttingDown { get; private set; } = false;

    /// <summary>
    /// </summary>
    public static event Action<int>? OnShutdown = null;

    /// <summary>
    /// </summary>
    /// <param name="exit_code"></param>
    public static void Shutdown(int exit_code)
    {
        if (IsShuttingDown) return;
        IsShuttingDown = true;
        OnShutdown?.Invoke(exit_code);
    }
    /// <summary>
    /// </summary>
    public static void Shutdown() => Shutdown(exit_code: 0);

}
