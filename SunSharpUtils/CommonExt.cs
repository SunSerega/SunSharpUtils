using System;

namespace SunSharpUtils.Ext.Common;

/// <summary>
/// </summary>
public static class CommonExt
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

}
