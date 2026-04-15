using System;
using System.Diagnostics;

namespace SunSharpUtils.Logs;

/// <summary>
/// WinSvc global log file
/// </summary>
public static class GlobalLog
{
    private static readonly FileLogWithRotation global_file_log = new(Process.GetCurrentProcess().ProcessName);

    /// <summary>
    /// </summary>
    public static String FilePath => global_file_log.FilePathWithExt;

    /// <summary>
    /// Adds a [MESSG] log line (or multiple, \n separated) to global log of this WinSvc
    /// </summary>
    /// <param name="line"></param>
    public static void AddMessage(String line) =>
        global_file_log.EnqueueMessage(FileLogWithRotation.MessageType.MESSG, line);

    /// <summary>
    /// Adds a [ERROR] log line (or multiple, \n separated) to global log of this WinSvc
    /// </summary>
    /// <param name="ex"></param>
    public static void AddError(Exception ex) =>
        global_file_log.EnqueueMessage(FileLogWithRotation.MessageType.ERROR, $"{ex}");

    /// <summary>
    /// Waits for all messages to be written to the file
    /// </summary>
    public static void FlushAll() =>
        global_file_log.FlushAll();

}
