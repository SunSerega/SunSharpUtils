using System;
using System.Collections.Generic;

namespace SunSharpUtils.Ext.Math;

/// <summary>
/// </summary>
public static class MathExt
{

    /// <summary>
    /// Compares using IComparable
    /// </summary>
    public static T ClampBottom<T>(this T val, T min) where T : IComparable<T>
    {
        if (val.CompareTo(min) < 0)
            return min;
        return val;
    }
    /// <summary>
    /// Compares using IComparable
    /// </summary>
    public static T ClampTop<T>(this T val, T max) where T : IComparable<T>
    {
        if (val.CompareTo(max) > 0)
            return max;
        return val;
    }
    /// <summary>
    /// Compares using IComparable
    /// </summary>
    public static T Clamp<T>(this T val, T min, T max) where T : IComparable<T>
    {
        return val.ClampBottom(min).ClampTop(max);
    }

    /// <summary>
    /// Compares using IComparable
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
