using System;

namespace SunSharpUtils;

/// <summary>
/// A simple implementation of IDisposable that runs a specified action when disposed
/// </summary>
/// <param name="act"></param>
/// <param name="prevent_double_call"></param>
public sealed class LambdaDisposable(Action act, Boolean prevent_double_call = false) : IDisposable
{
    private readonly Action act = act;
    private readonly Boolean prevent_double_call = prevent_double_call;
    private Boolean ignore_next_calls = false;

    /// <summary>
    /// </summary>
    public void Dispose()
    {
        if (this.ignore_next_calls)
            return;
        this.act.Invoke();
        this.ignore_next_calls = this.prevent_double_call;
    }

}
