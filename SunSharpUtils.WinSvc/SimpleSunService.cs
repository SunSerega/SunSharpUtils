using System;
using System.Diagnostics;
using System.ServiceProcess;
using System.Threading;

namespace SunSharpUtils.WinSvc;

/// <summary>
/// Base class for simple services (one per process)
/// </summary>
public abstract class SimpleSunService : ServiceBase
{
    private readonly CancellationTokenSource cts_svc_running = new();

    /// <summary>
    /// </summary>
    protected SimpleSunService()
    {
        this.ServiceName = Process.GetCurrentProcess().ProcessName;
        this.CanStop = true;
        this.CanPauseAndContinue = false;
        this.AutoLog = true;
    }

    /// <summary>
    /// </summary>
    protected abstract void SvcStart(String[] args, CancellationToken svc_stop_token);

    /// <summary>
    /// </summary>
    protected sealed override void OnStart(String[] args)
    {
        this.StartCalled = true;
        this.SvcStart(args, this.cts_svc_running.Token);
    }

    /// <summary>
    /// </summary>
    protected sealed override void OnStop()
    {
        Prompt.Notify("Service stop requested");
        this.cts_svc_running.Cancel();
    }

    internal Boolean StartCalled { get; private set; } = false;
    internal void DebugStart() =>
        this.OnStart(Environment.GetCommandLineArgs());

}
