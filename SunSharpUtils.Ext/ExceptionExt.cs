using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace SunSharpUtils.Ext.Exceptions;

/// <summary>
/// </summary>
public static class ExceptionExt
{

    /// <summary>
    /// Enumerates all inner exceptions of AggregateException recursively (excluding all AggregateException-s themselves)
    /// </summary>
    /// <param name="root_ex"></param>
    /// <returns></returns>
    public static IEnumerable<Exception> GetNestedExceptions(this Exception root_ex)
    {
        if (root_ex is AggregateException agg_ex)
            return agg_ex.InnerExceptions.SelectMany(inner_ex => inner_ex.GetNestedExceptions());
        else
            return [root_ex];
    }

    /// <summary>
    /// </summary>
    public static Boolean AreAllNestedExceptionsOfType<TException>(this Exception root_ex)
        where TException : Exception => root_ex.GetNestedExceptions().All(ex => ex is TException);

    /// <summary>
    /// </summary>
    public static Boolean IsOnlyNestedOpCanceledExceptions(this Exception root_ex, CancellationToken cancel_token) =>
        cancel_token.IsCancellationRequested && root_ex.AreAllNestedExceptionsOfType<OperationCanceledException>();

}
