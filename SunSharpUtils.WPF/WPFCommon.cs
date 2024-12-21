using System;

using System.Windows;

namespace SunSharpUtils.WPF;

/// <summary>
/// Common WPF utilities
/// </summary>
public static class WPFCommon
{

    /// <summary>
    /// The current application instance
    /// </summary>
    public static Application? CurrentApp { get; private set; }

    /// <summary>
    /// Initializes common WPF stuff
    /// 1. Adds a handler to Err.OnError, which opens a MessageBox with the error message
    /// </summary>
    public static void Init(Application app)
    {

        Err.OnError += e => CustomMessageBox.Show(title: "ERROR", content: e.ToString());
        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.IsTerminating) return;
            Err.Handle((Exception)e.ExceptionObject);
        };
        
        if (CurrentApp != null)
            throw new InvalidOperationException($"Already initialized");
        CurrentApp = app;

        Common.OnShutdown += exit_code =>
            CurrentApp?.Dispatcher.Invoke(() => CurrentApp.Shutdown(exit_code));
        app.SessionEnding += (o, e) =>
        {
            if (Common.IsShuttingDown) return;
            if (e.ReasonSessionEnding != ReasonSessionEnding.Shutdown)
                MessageBox.Show($"called Application.Shutdown instead of SunSharpUtils.Common.Shutdown");
            Common.Shutdown();
        };

    }

}
