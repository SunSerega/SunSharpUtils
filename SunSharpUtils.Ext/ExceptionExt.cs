using System;
using System.Collections.Generic;
using System.Linq;

namespace SunSharpUtils.Ext.Exceptions;

/// <summary>
/// </summary>
public static class ExceptionExt
{

    /// <summary>
    /// Enumerates all inner exceptions of AggregateException recursively (excluding all AggregateException)
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

}
