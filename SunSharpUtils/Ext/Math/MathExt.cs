using System;
using System.Collections.Generic;

namespace SunSharpUtils.Ext.Math;

/// <summary>
/// </summary>
public static class MathExt
{

    /// <summary>
    /// </summary>
    public static Boolean InRange<T>(this T x, T a, T b)
        where T : IComparable<T>
    {
        var cmp = Comparer<T>.Default;
        if (cmp.Compare(x, a) < 0)
            return false;
        if (cmp.Compare(x, b) > 0)
            return false;
        return true;
    }

}
