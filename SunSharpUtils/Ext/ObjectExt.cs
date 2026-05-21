using System;
using System.Collections.Generic;

namespace SunSharpUtils.Ext.Objects;

/// <summary>
/// </summary>
public static class ObjectExt
{

    /// <summary>
    /// Infinitely iterates on obj by transforming each element into the next one
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="obj"></param>
    /// <param name="get_next"></param>
    /// <returns></returns>
    public static IEnumerable<T> IterateWhileNotNull<T>(this T obj, Func<T, T?> get_next)
        where T : class
    {
        while (true)
        {
            yield return obj;
            var next = get_next(obj);
            if (next is null)
                break;
            obj = next;
        }
    }

}
