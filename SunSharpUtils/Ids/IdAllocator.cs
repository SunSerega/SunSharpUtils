using System;

using System.Numerics;

using System.Linq;
using System.Collections.Generic;
using System.Threading;

namespace SunSharpUtils.Ids;

/// <summary>
/// Represents ID for IdAllocator
/// </summary>
/// <typeparam name="TSelf"></typeparam>
public interface IAllocatableId<TSelf> : IEqualityOperators<TSelf, TSelf, Boolean>, IAdditionOperators<TSelf, UInt32, TSelf>, ISubtractionOperators<TSelf, UInt32, TSelf>, IMinMaxValue<TSelf>
    where TSelf : struct, IAllocatableId<TSelf>
{
    /// <summary>
    /// </summary>
    public static abstract Boolean operator >(TSelf a, TSelf b);
    /// <summary>
    /// </summary>
    public static abstract Boolean operator <(TSelf a, TSelf b);
    /// <summary>
    /// </summary>
    public static abstract Int32 Compare(TSelf a, TSelf b);
}

/// <summary>
/// Manages allocation of IDs, holding unused ranges for fast reuse
/// </summary>
/// <typeparam name="T"></typeparam>
public readonly struct IdAllocator<T>
    where T : struct, IAllocatableId<T>
{
    private sealed class IdRange(T min, T max)
    {
        public T Min { get; set; } = min;
        public T Max { get; set; } = max;
    }
    // From highest to lowest ids, to allow for faster allocation of low ids
    private readonly List<IdRange> unused = [new(T.MinValue, T.MaxValue)];

    /// <summary>
    /// Creates new IdAllocator with the list of already pre-allocated IDs
    /// </summary>
    /// <param name="unsorted_used_ids"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public IdAllocator(IEnumerable<T> unsorted_used_ids)
    {
        var used_ids = unsorted_used_ids.ToArray();
        if (used_ids.Length == 0)
            return;
        this.unused.Clear();
        Array.Sort(used_ids, (id1, id2) => T.Compare(id2, id1));

        var last_id = used_ids[0];
        if (last_id != T.MaxValue)
            this.unused.Add(new(last_id+1, T.MaxValue));
        
        for (Int32 i = 1; i < used_ids.Length; i++)
        {
            var id = used_ids[i];
            if (id == last_id)
                throw new InvalidOperationException($"Id {id} was in storage twice");
            if (id != last_id - 1)
                this.unused.Add(new(id+1, last_id-1));
            last_id = id;
        }

        if (last_id != T.MinValue)
            this.unused.Add(new(T.MinValue, last_id-1));
    }

    private readonly Lock l_alloc_free = new();

    /// <summary>
    /// </summary>
    public T AllocateId()
    {
        using var lock_scope = this.l_alloc_free.EnterScope();
        if (this.unused.Count == 0)
            throw new InvalidOperationException($"No more used_ids available");
        var range = this.unused[^1];
        var id = range.Min;
        if (id == range.Max)
            this.unused.RemoveAt(this.unused.Count - 1);
        else
            range.Min += 1;
        return id;
    }

    /// <summary>
    /// </summary>
    public void FreeId(T id)
    {
        using var lock_scope = this.l_alloc_free.EnterScope();
        var i_hi = this.BinarySearchUsedId(id);
        var i_lo = i_hi - 1;

        IdRange? range_lo = null;
        IdRange? range_hi = null;
        if (i_lo != -1)
        {
            range_lo = this.unused[i_lo];
            if (range_lo.Min == id + 1)
                range_lo = null;
        }
        if (i_hi != this.unused.Count)
        {
            range_hi = this.unused[i_hi];
            if (range_hi.Max == id - 1)
                range_hi = null;
        }

        if (range_lo is not null)
        {
            if (range_hi is not null)
            {
                range_hi.Max = range_lo.Max;
                this.unused.RemoveAt(i_hi);
            }
            else
            {
                range_lo.Min -= 1;
            }
        }
        else
        {
            if (range_hi is not null)
            {
                range_hi.Max += 1;
            }
            else
            {
                this.unused.Insert(i_hi, new IdRange(id, id));
            }
        }

    }

    private Int32 BinarySearchUsedId(T id)
    {
        var i0 = 0;
        var len = this.unused.Count;

        while (len > 0)
        {
            var half_len = len / 2;
            var i = i0 + half_len;
            var range = this.unused[i];
            if (id > range.Max) // i is lower
            {
                len = half_len;
            }
            else if (id < range.Min) // i is higher
            {
                i0 = i + 1;
                len -= half_len + 1;
            }
            else
                throw new InvalidOperationException($"Id {id} is not used");
        }

        return i0;
    }

}
