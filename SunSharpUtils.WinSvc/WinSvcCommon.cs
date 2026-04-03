using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.ServiceProcess;

using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.WinSvc;

/// <summary>
/// Common utilities for Windows services
/// </summary>
public static class WinSvcCommon
{

    /// <summary>
    /// Initializes
    /// </summary>
    public static void Init(SimpleSunService svc)
    {

        Err.Init(new()
        {
            Handle = ex => FileLog.Log(FileLog.MessageType.ERROR, $"{ex}"),
        });

        Prompt.Init(new()
        {
            Notify = (title, msg) => FileLog.Log(FileLog.MessageType.MESSG, $"{new[] { title, msg }.Where(x => x is not null).JoinToString(": ")}"),
            AskYesNo = (title, msg) => throw new InvalidOperationException(),
            AskAny = (title, msg, def) => throw new InvalidOperationException(),
        });

        Environment.CurrentDirectory = Path.GetDirectoryName(Environment.ProcessPath) ?? throw new InvalidOperationException();

        Prompt.Notify($"==================================================");
        Prompt.Notify($"Command line: {Environment.CommandLine}");
        Prompt.Notify($"==================================================");
        FileLog.FlushAll();

        try
        {
            if (svc.StartCalled)
                throw new InvalidOperationException($"Services passed to {nameof(WinSvcCommon)}.{nameof(Init)} should be a new instance");
            ServiceBase.Run(svc);
            if (svc.StartCalled)
                return;

            Debugger.Launch();
            if (Debugger.IsAttached)
                svc.DebugStart();
        }
        catch (Exception ex)
        {
            Err.Handle(ex);
            Err.Handle($"Critical error starting {svc}, exiting");
            FileLog.FlushAll();
            Environment.Exit(-1);
        }

    }

    /// <summary>
    /// Handles the error and shuts down the svc
    /// </summary>
    /// <param name="ex"></param>
    /// <param name="when_doing">description of what couldn't be done, lowercase</param>
    public static void HandleCriticalError(Exception ex, String when_doing)
    {
        Err.Handle(ex);
        Err.Handle($"Critical error {when_doing}, exiting");
        FileLog.FlushAll();
        Environment.Exit(-1);
    }

}
