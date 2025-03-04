using System;

using System.Collections.Generic;

namespace SunSharpUtils;

/// <summary>
/// </summary>
public static class Linq
{

    /// <summary>
    /// </summary>
    public static T2[] ConvertAll<T1,T2>(this T1[] arr, Converter<T1, T2> conv) => Array.ConvertAll(arr, conv);

    /// <summary>
    /// </summary>
    public static String JoinToString<T>(this IEnumerable<T> seq, Char separator = ' ') => String.Join(separator, seq);
    /// <summary>
    /// </summary>
    public static String JoinToString<T>(this IEnumerable<T> seq, String? separator) => String.Join(separator, seq);

    /// <summary>
    /// </summary>
    public static IEnumerable<TRes> Pairwise<T, TRes>(this IEnumerable<T> seq, Func<T,T, TRes> conv)
    {
        using var en = seq.GetEnumerator();
        if (!en.MoveNext()) yield break;
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
    public static IEnumerable<(T,T)> Pairwise<T, TRes>(this IEnumerable<T> seq) => seq.Pairwise((a, b) => (a, b));

}
