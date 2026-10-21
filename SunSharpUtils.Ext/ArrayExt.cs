using System;
using System.Collections.Generic;

using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.Ext.Array;

/// <summary>
/// </summary>
public static class ArrayExt
{

    /// <summary>
    /// </summary>
    public static T[] Generate<T>(Int32 len, Func<Int32, T> generator)
    {
        var arr = new T[len];
        for (var i = 0; i < len; i++)
            arr[i] = generator.Invoke(i);
        return arr;
    }

    /// <summary>
    /// </summary>
    public static T[] SortInPlace<T, TKey>(this T[] arr, Converter<T, TKey> sort_by)
    {
        var keys = arr.ToArray(sort_by);
        System.Array.Sort(keys, arr);
        return arr;
    }

    /// <summary>
    /// </summary>
    public static T[] CombineArrays<T>(this Span<T[]> arrays)
    {
        var total_len = 0;
        foreach (var arr in arrays)
            total_len += arr.Length;
        var res = new T[total_len];
        var i = 0;
        foreach (var arr in arrays)
        {
            System.Array.Copy(arr, 0, res, i, arr.Length);
            i += arr.Length;
        }
        return res;
    }
    /// <summary>
    /// </summary>
    public static T[] CombineWith<T>(this T[] arr1, T[] arr2) => CombineArrays([arr1, arr2]);

    /// <summary>
    /// </summary>
    public static (T[] is_false, T[] is_true) SplitArray<T>(this T[] arr, Predicate<T> condition)
    {
        var if_false = new List<T>(arr.Length);
        var if_true = new List<T>(arr.Length);
        foreach (var item in arr)
            (condition(item) ? if_true : if_false).Add(item);
        return (if_false.ToArray(), if_true.ToArray());
    }

}
