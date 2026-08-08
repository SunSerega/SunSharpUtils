using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.InteropServices;

namespace SunSharpUtils.Ext.UniversalBin;

//TODO Get up to speed with StructSerializer in "vid list" solution and then split this file, so I have 1 per global type here

//TODO Interface to define own default marshaling in any given type

/// <summary>
/// Common public utils for <see cref="UniversalBinaryAdapter{T}"/>
/// </summary>
public static class UniversalBinaryAdapter
{

    /// <summary>
    /// </summary>
    public sealed class Lambda<T> : UniversalBinaryAdapter<T>
    {
        /// <summary>
        /// </summary>
        public required Action<BinaryWriter, T> Saver { get; init; }
        /// <summary>
        /// </summary>
        public required Func<BinaryReader, T> Loader { get; init; }

        /// <inheritdoc cref="UniversalBinaryAdapter{T}.Save(BinaryWriter, T)"/>
        public override void Save(BinaryWriter bw, T value) => this.Saver(bw, value);
        /// <inheritdoc cref="UniversalBinaryAdapter{T}.Load(BinaryReader)"/>
        public override T Load(BinaryReader br) => this.Loader(br);
    }

    /// <summary>
    /// </summary>
    public sealed class Unmanaged<T>() : UniversalBinaryAdapter<T> where T : unmanaged
    {
        /// <inheritdoc cref="UniversalBinaryAdapter{T}.Save(BinaryWriter, T)"/>
        public override void Save(BinaryWriter bw, T value)
        {
            var input = new Span<T>(ref value);
            var bytes = MemoryMarshal.AsBytes(input);
            bw.Write(bytes);
        }
        /// <inheritdoc cref="UniversalBinaryAdapter{T}.Load(BinaryReader)"/>
        public override T Load(BinaryReader br)
        {
            Span<T> result = stackalloc T[1];
            var bytes = MemoryMarshal.AsBytes(result);
            br.ReadExactly(bytes);
            return result[0];
        }
    }

    internal static class InternalUtils
    {
        //public static ConcurrentDictionary<Type, Func<IUniversalBinaryAdapter?>> AllDefaults { get; } = [];

        // No need to check type.IsByRefLike and the like, because Read/Write generic functions don't allow ref-s in their T
        //public static Boolean HasRefs(Type type) =>
        //    (Boolean)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(type).Invoke(obj: null, parameters: [])!;

        public static Boolean TryCreateForUnmanaged<T>([NotNullWhen(true)] out UniversalBinaryAdapter<T>? adapter)
        {
            if (!Err<ArgumentException>.TryCatch(() => typeof(Unmanaged<>).MakeGenericType(typeof(T)), out var t, out var ex))
            {
                adapter = null;
                return false;
            }
            adapter = (UniversalBinaryAdapter<T>?)Activator.CreateInstance(t) ?? throw null!;
            return true;
        }

    }

}

internal interface IUniversalBinaryAdapter;
/// <summary>
/// Contains Save and Load methods for a specific type T, to be used with BinaryWriter and BinaryReader
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class UniversalBinaryAdapter<T> : IUniversalBinaryAdapter
{

    static UniversalBinaryAdapter()
    {
        //if (!UniversalBinaryAdapterUtils.AllDefaults.TryAdd(typeof(T), () => Default))
        //    throw new InvalidOperationException($"A IUniversalBinaryAdapter implementation for type {typeof(T)} is already registered");

        if (typeof(T) == typeof(String))
        {
            UniversalBinaryAdapter<String>.Default = (
                saver: (bw, value) => bw.Write(value),
                loader: br => br.ReadString()
            );
            return;
        }

        if (UniversalBinaryAdapter.InternalUtils.TryCreateForUnmanaged<T>(out var adapter))
        {
            Default = adapter;
            return;
        }

    }

    /// <summary>
    /// Default adapter for type T, or null if none is registered. Can be overriden by a custom implementation for the whole executable
    /// </summary>
    public static UniversalBinaryAdapter<T>? Default { get; set; } = null;
    /// <summary>
    /// Gets <see cref="Default"/>, or throws an <see cref="InvalidOperationException"/> if no adapter is registered
    /// </summary>
    public static UniversalBinaryAdapter<T> DefaultOrThrow => Default ?? throw new InvalidOperationException($"No {nameof(UniversalBinaryAdapter<>)} implementation registered for type {typeof(T)}");

    /// <summary>
    /// Writes the value to the BinaryWriter using this adapter
    /// </summary>
    /// <param name="bw"></param>
    /// <param name="value"></param>
    public abstract void Save(BinaryWriter bw, T value);
    /// <summary>
    /// Reads the value from the BinaryReader using this adapter
    /// </summary>
    /// <param name="br"></param>
    /// <returns></returns>
    public abstract T Load(BinaryReader br);

    /// <summary>
    /// </summary>
    public static implicit operator UniversalBinaryAdapter<T>((Action<BinaryWriter, T> saver, Func<BinaryReader, T> loader) lambda) => new UniversalBinaryAdapter.Lambda<T> { Saver = lambda.saver, Loader = lambda.loader };

}

/// <summary>
/// </summary>
public static class UniversalBinaryAdapterExt
{

    /// <summary>
    /// Uses the default <see cref="UniversalBinaryAdapter{T}"/> for type T to write the value to the BinaryWriter
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="bw"></param>
    /// <param name="value"></param>
    public static void WriteData<T>(this BinaryWriter bw, T value)
    {
        var adapter = UniversalBinaryAdapter<T>.DefaultOrThrow;
        adapter.Save(bw, value);
    }

    /// <summary>
    /// Uses the default <see cref="UniversalBinaryAdapter{T}"/> for type T to read the value from the BinaryReader
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="br"></param>
    /// <returns></returns>
    public static T ReadData<T>(this BinaryReader br)
    {
        var adapter = UniversalBinaryAdapter<T>.DefaultOrThrow;
        return adapter.Load(br);
    }

}
