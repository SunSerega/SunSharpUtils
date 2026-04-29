using System;
using System.Threading;

namespace SunSharpUtils.Threading;

/// <summary>
/// This class is used to delay updates to a single target                                                  <br/>
/// If a new update is requested before the previous one is executed                                        <br/>
/// and can_delay_further is set, then the previous update is discarded                                     <br/>
/// This way a lot of updates can be requested, but only the last one will be executed                      <br/>
///                                                                                                         <br/>
/// Note: The only guarantee is that the update will be executed at some point after the Trigger() call     <br/>
/// There is no guarantee that the update will be delayed                                                   <br/>
/// It might start executing on previous delay after further delay has been requested                       <br/>
/// In that case it will be executed second time after the new delay                                        <br/>
/// </summary>
public sealed class DelayedUpdater
{
    private sealed class ActivationHolder
    {
        private DelayedUpdateSpec? requested;
        private readonly Lock l_requested = new();

        public Boolean IsRequested => this.requested.HasValue;

        public TimeSpan GetRemainingWait() => this.requested!.Value.GetRemainingWait();

        public void Clear()
        {
            using var lock_scope = this.l_requested.EnterScope();
            this.requested = null;
        }

        public Boolean TryUpdate(DelayedUpdateSpec next)
        {
            using var lock_scope = this.l_requested.EnterScope();
            var need_ev_set = true;
            if (this.requested.HasValue)
            {
                next = DelayedUpdateSpec.Combine(this.requested.Value, next, out need_ev_set);
                if (this.requested == next)
                    return false;
            }
            this.requested = next;
            return need_ev_set;
        }

    }

    private readonly ManualResetEventSlim ev = new(false);
    private readonly ActivationHolder activation = new();

    private static ThreadStart MakeThreadStart(
        Boolean is_background,
        Action update,
        ManualResetEventSlim ev,
        ActivationHolder activation
    ) => () =>
    {
        while (true)
            try
            {
                if (!activation.IsRequested)
                {
                    ev.Wait();
                    ev.Reset();
                    continue;
                }

                {
                    var wait = activation.GetRemainingWait();
                    if (wait > TimeSpan.Zero)
                    {
                        ev.Wait(wait);
                        ev.Reset();
                        continue;
                    }
                }

                activation.Clear();

                using var thread_is_background_resetter = ThreadingCommon.TempSetIsBackground(is_background);
                update();
            }
            catch when (Common.IsShuttingDown)
            {
                break;
            }
            catch (Exception e)
            {
                Err.Handle(e);
            }
    };

    /// <summary>
    /// </summary>
    /// <param name="update">An action to run when delay expires</param>
    /// <param name="description">Used for thread name</param>
    /// <param name="is_background">If false, the app can't be shut down in the middle of executing update</param>
    public DelayedUpdater(Action update, String description, Boolean is_background)
    {
        var thr = new Thread(MakeThreadStart(is_background, update, this.ev, this.activation))
        {
            IsBackground=true,
            Name = $"{nameof(DelayedUpdater)}: {description}",
        };
        thr.SetApartmentState(ApartmentState.STA);
        thr.Start();
    }

    /// <summary>
    /// Triggers an update in future, or delays an already requested one
    /// </summary>
    /// <param name="spec"></param>
    public void Trigger(DelayedUpdateSpec spec)
    {
        if (this.activation.TryUpdate(spec))
            this.ev.Set();
    }

    /// <summary>
    /// </summary>
    public void TriggerNow() => this.Trigger(DelayedUpdateSpec.Now);
    /// <summary>
    /// </summary>
    public void TriggerPostpone(TimeSpan delay) => this.Trigger(DelayedUpdateSpec.Postpone(delay));
    /// <summary>
    /// </summary>
    public void TriggerUrgent(TimeSpan delay) => this.Trigger(DelayedUpdateSpec.Urgent(delay));

    /// <summary>
    /// </summary>
    ~DelayedUpdater() => Err.Handle($"{nameof(DelayedUpdater)} is not supposed to ever go out of scope");

}
