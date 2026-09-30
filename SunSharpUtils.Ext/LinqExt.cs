using System;

using System.Collections.Generic;
using System.Linq;

using SunSharpUtils.Ext.Math;

namespace SunSharpUtils.Ext.Linq;

/// <summary>
/// </summary>
public static class LinqExt
{

    /// <summary>
    /// </summary>
    public static Int32 CountOf<T>(this IEnumerable<T> seq, T target, IEqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        var count = 0;
        foreach (var item in seq)
        {
            if (comparer.Equals(item, target))
                count++;
        }
        return count;
    }

    ///// <summary>
    ///// </summary>
    //public static IEnumerable<T> RangeIncl<T>(T from, T to) where T : System.Numerics.IBinaryInteger<T>
    //{
    //    if (from > to)
    //        throw new ArgumentException($"[{from}] must be <= [{to}]");
    //    for (var i = from; i <= to; i++)
    //        yield return i;
    //}

    /// <summary>
    /// </summary>
    public static void ForEach<T>(this IEnumerable<T> seq, Action<T> use)
    {
        foreach (var item in seq)
            use(item);
    }

    /// <summary>
    /// </summary>
    public static Stack<T> ToStack<T>(this IEnumerable<T> seq) => new(seq);

    /// <summary>
    /// </summary>
    public static Queue<T> ToQueue<T>(this IEnumerable<T> seq) => new(seq);

    /// <summary>
    /// </summary>
    public static T[] ToArrayWhere<T>(this ICollection<T> coll, Func<T, Boolean> predicate)
    {
        if (coll is T[] arr && coll.All(predicate))
            return arr;
        var res = new T[coll.Count];
        var i = 0;
        foreach (var item in coll)
        {
            if (predicate(item))
                res[i++] = item;
        }
        if (i == 0)
            return [];
        if (i != res.Length)
            Array.Resize(ref res, i);
        return res;
    }

    /// <summary>
    /// </summary>
    public static T2[] ToArray<T1, T2>(this IReadOnlyCollection<T1> coll, Converter<T1, T2> conv)
    {
        var res = new T2[coll.Count];
        var i = 0;
        foreach (var item in coll)
            res[i++] = conv(item);
        if (i != res.Length)
            throw new ArgumentException($"Collection size changed {res.Length}=>{i} during conversion");
        if (i == 0)
            return [];
        return res;
    }

    /// <summary>
    /// </summary>
    public delegate T2 ConditionalConverter<T1, T2>(T1 item, out Boolean success);
    /// <summary>
    /// </summary>
    public static T2[] ToArray<T1, T2>(this IReadOnlyCollection<T1> coll, ConditionalConverter<T1, T2> conv)
    {
        var res = new T2[coll.Count];
        var i = 0;
        foreach (var item in coll)
        {
            var value = conv(item, out var success);
            if (success)
                res[i++] = value;
        }
        if (i == 0)
            return [];
        if (i != res.Length)
            Array.Resize(ref res, i);
        return res;
    }

    /// <summary>
    /// </summary>
    public static T[] SortInPlace<T, TKey>(this T[] arr, Converter<T, TKey> sort_by)
    {
        var keys = arr.ToArray(sort_by);
        Array.Sort(keys, arr);
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
            Array.Copy(arr, 0, res, i, arr.Length);
            i += arr.Length;
        }
        return res;
    }
    /// <summary>
    /// </summary>
    public static T[] CombineWith<T>(this T[] arr1, T[] arr2) => CombineArrays([arr1, arr2]);

    /// <summary>
    /// </summary>
    public static Dictionary<T, Int32> ToIndex<T>(this IList<T> seq)
        where T : notnull
    {
        var dict = new Dictionary<T, Int32>(seq.Count);
        for (var i = 0; i < seq.Count; i++)
            dict.Add(seq[i], i);
        return dict;
    }

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

    /// <summary>
    /// </summary>
    public static String JoinToString<T>(this IEnumerable<T> seq, Char separator = ' ') => String.Join(separator, seq);
    /// <summary>
    /// </summary>
    public static String JoinToString<T>(this IEnumerable<T> seq, String? separator) => String.Join(separator, seq);

    /// <summary>
    /// </summary>
    public static void PairwiseForEach<T>(this IEnumerable<T> seq, Action<T, T> use)
    {
        using var en = seq.GetEnumerator();
        if (!en.MoveNext())
            return;
        var prev = en.Current;
        while (en.MoveNext())
        {
            var curr = en.Current;
            use(prev, curr);
            prev = curr;
        }
    }
    /// <summary>
    /// </summary>
    public static IEnumerable<TRes> Pairwise<T, TRes>(this IEnumerable<T> seq, Func<T, T, TRes> conv)
    {
        using var en = seq.GetEnumerator();
        if (!en.MoveNext())
            yield break;
        var prev = en.Current;
        while (en.MoveNext())
        {
            var curr = en.Current;
            yield return conv(prev, curr);
            prev = curr;
        }
    }
    /// <summary>
    /// </summary>
    public static IEnumerable<(T, T)> Pairwise<T>(this IEnumerable<T> seq) => seq.Pairwise((a, b) => (a, b));

    /// <summary>
    /// </summary>
    /// <returns>Number of removed items</returns>
    public static Int32 RemoveWhere<TKey, TValue>(this IDictionary<TKey, TValue> dict, Func<TKey, TValue, Boolean> predicate)
    {
        if (dict.Count == 0)
            return 0;
        var to_remove = new List<TKey>(dict.Count);
        foreach (var kvp in dict)
        {
            if (predicate(kvp.Key, kvp.Value))
                to_remove.Add(kvp.Key);
        }
        foreach (var key in to_remove)
        {
            if (!dict.Remove(key))
                throw new InvalidOperationException($"Failed to remove key [{key}] from dictionary");
        }
        return to_remove.Count;
    }

    /// <summary>
    /// Groups adjacent elements of a sequence by a specified key selector and projects the results
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <typeparam name="TKey"></typeparam>
    /// <typeparam name="TResult"></typeparam>
    /// <param name="seq"></param>
    /// <param name="key_selector"></param>
    /// <param name="result_selector"></param>
    /// <param name="comparer"></param>
    /// <returns></returns>
    public static IEnumerable<TResult> AdjacentGroupBy<T, TKey, TResult>(this IEnumerable<T> seq, Func<T, TKey> key_selector, Func<TKey, IReadOnlyList<T>, TResult> result_selector, IEqualityComparer<TKey>? comparer = null)
    {
        comparer ??= EqualityComparer<TKey>.Default;

        var curr_key = default(TKey);
        var curr_group = new List<T>();
        if (seq is IReadOnlyCollection<T> coll)
            curr_group.EnsureCapacity(coll.Count.ClampTop(1024));
        foreach (var item in seq)
        {
            var item_key = key_selector(item);

            if (curr_group.Count == 0)
            {
                curr_key = item_key;
                curr_group.Add(item);
                continue;
            }

            if (comparer.Equals(curr_key, item_key))
            {
                curr_group.Add(item);
                continue;
            }

            yield return result_selector(curr_key!, curr_group);
            curr_key = item_key;
            curr_group.Clear();
            curr_group.Add(item);
        }

        if (curr_group.Count != 0)
            yield return result_selector(curr_key!, curr_group);
    }

    /// <summary>
    /// Groups adjacent equal elements of a sequence and returns minimal info for each group: unique item + count of its repetitions
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="seq"></param>
    /// <param name="comparer"></param>
    /// <returns></returns>
    public static IEnumerable<(T item, Int32 count)> AdjacentGroup<T>(this IEnumerable<T> seq, IEqualityComparer<T>? comparer = null) =>
        seq.AdjacentGroupBy(item => item, (key, group) => (item: key, count: group.Count), comparer);

}
