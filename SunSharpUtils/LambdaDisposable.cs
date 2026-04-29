using System;

namespace SunSharpUtils;

/// <summary>
/// A simple implementation of IDisposable that runs a specified action when disposed
/// </summary>
/// <param name="act"></param>
public sealed class LambdaDisposable(Action act) : IDisposable
{
    private readonly Action act = act;
    /// <summary>
    /// </summary>
    public void Dispose() => this.act.Invoke();
}
