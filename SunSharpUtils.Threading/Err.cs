﻿using System;

using System.Threading.Tasks;

namespace SunSharpUtils.Threading;

/// <summary>
/// Extension methods for error handling in threaded context
/// </summary>
public static class ErrExt
{

    /// <summary>
    /// Makes sure task does not return an exception, instead passes it to <see cref="Err.Handle(Exception)"/>
    /// </summary>
    /// <param name="t"></param>
    public static Task HandleException(this Task t)
    {
        var trace = Environment.StackTrace;
        return t.ContinueWith(
            t =>
            {
                if (t.Exception?.InnerException is Exception ie)
                    Err.Handle(ie);
                else
                    Err.Handle($"Task faulted, but no exception exists. Handler trace:\n{trace}");
            }, TaskContinuationOptions.OnlyOnFaulted
        );
    }

}
