using System;

using System.Collections.Generic;

namespace SunSharpUtils.Ext.Linq;

/// <summary>
/// </summary>
public static class LinqExt
{

    /// <summary>
    /// </summary>
    public static Boolean SequenceEqual<T>(this IEnumerable<T> seq1, IEnumerable<T> seq2, EqualityComparer<T>? comparer = null)
    {
        comparer ??= EqualityComparer<T>.Default;
        if (seq1 is IReadOnlyCollection<T> c1 && seq2 is IReadOnlyCollection<T> c2)
        {
            if (c1.Count != c2.Count)
                return false;
        }
        using var en1 = seq1.GetEnumerator();
        using var en2 = seq2.GetEnumerator();
        while (true)
        {
            var mv1 = en1.MoveNext();
            var mv2 = en2.MoveNext();
            if (mv1 != mv2)
                return false;
            if (!mv1)
                break;
            if (!comparer.Equals(en1.Current, en2.Current))
                return false;
        }
        return true;
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
    public static T[] SortInPlace<T, TKey>(this T[] arr, Converter<T, TKey> sort_by)
    {
        var keys = arr.ToArray(sort_by);
        Array.Sort(keys, arr);
        return arr;
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

}
