using System;
using System.Linq;
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

#pragma warning disable CS1591 // Missing XML comment for publicly visible type or member
    public readonly record struct InitConfig()
    {
        public required Application App { get; init; }
        //public required Window Window { get; init; }
        public Err.DelegateStore? ErrInit { get; init; } = null;
        public Prompt.DelegateStore? PromptInit { get; init; } = null;
    }
#pragma warning restore CS1591 // Missing XML comment for publicly visible type or member
    /// <summary>
    /// Initializes common WPF stuff
    /// 0. Sets some internal SunSharpUtils values, like WPFCommon.CurrentApp
    /// 1. Adds a handler to Err.OnError, which opens a MessageBox with the error message
    /// 2. Adds handlers to Prompt to show CustomMessageBox-es
    /// 3. Adds AppDomain.CurrentDomain.UnhandledException handler
    /// </summary>
    public static void Init(InitConfig config)
    {

        if (CurrentApp != null)
            throw new InvalidOperationException($"Already initialized");
        CurrentApp = config.App;

        Err.Init(config.ErrInit ?? new()
        {
            Handle = e => CustomMessageBox.ShowOK(title: "ERROR", content: e.ToString())
        });

        Prompt.Init(config.PromptInit ?? new()
        {
            Notify = CustomMessageBox.ShowOK,
            AskYesNo = CustomMessageBox.ShowYesNo,
            AskAny = CustomMessageBox.Show
        });

        AppDomain.CurrentDomain.UnhandledException += (s, e) =>
        {
            if (e.IsTerminating) return;
            Err.Handle((Exception)e.ExceptionObject);
        };

        Common.OnShutdown += exit_code =>
            CurrentApp?.Dispatcher.Invoke(() => CurrentApp.Shutdown(exit_code));
        config.App.SessionEnding += (o, e) =>
        {
            if (Common.IsShuttingDown) return;
            if (e.ReasonSessionEnding != ReasonSessionEnding.Shutdown)
                MessageBox.Show($"called Application.Shutdown instead of SunSharpUtils.Common.Shutdown");
            Common.Shutdown();
        };

    }

    /// <summary>
    /// 
    /// </summary>
    /// <param name="handler"></param>
    public static void TrackAllRoutedEvents(RoutedEventHandler handler)
    {
        var events = EventManager.GetRoutedEvents();
        foreach (var routedEvent in events)
        {
            var events_in_type = routedEvent.OwnerType.GetFields(System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public).Select(pi => pi.GetValue(null));
            if (!events_in_type.Contains(routedEvent))
                continue;
            EventManager.RegisterClassHandler(
                typeof(Window),
                routedEvent,
                handler
            );
        }
    }

}
