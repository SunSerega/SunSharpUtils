using System;
using System.IO;
using System.Linq.Expressions;
using System.Reflection;

namespace SunSharpUtils.Bin;

/// <summary>
/// </summary>
public static class BinExt
{

    #region Enum

    private static class EnumOps<T>
        where T : struct, Enum
    {
        public static readonly Action<BinaryWriter, T> write;
        public static readonly Func<BinaryReader, T> read;

        static EnumOps()
        {
            var int_type = typeof(T).GetEnumUnderlyingType();

            var p_write_bw = Expression.Parameter(typeof(BinaryWriter), "bw");
            var p_write_val = Expression.Parameter(typeof(T), "val");
            var mi_write = typeof(BinaryWriter).GetMethod(
                nameof(BinaryWriter.Write),
                BindingFlags.Instance | BindingFlags.Public,
                [int_type]
            ) ?? throw new InvalidOperationException($"Couldn't find {nameof(BinaryWriter)}.{nameof(BinaryWriter.Write)} with an argument of type {int_type}");
            write = Expression.Lambda<Action<BinaryWriter, T>>(
                Expression.Call(p_write_bw, mi_write, Expression.Convert(p_write_val, int_type)),
                parameters: [p_write_bw, p_write_val]
            ).Compile();

            var p_read_br = Expression.Parameter(typeof(BinaryReader), "br");
            var mi_read_name = "Read"+int_type.Name;
            var mi_read = typeof(BinaryReader).GetMethod(
                mi_read_name,
                BindingFlags.Instance | BindingFlags.Public,
                []
            ) ?? throw new InvalidOperationException($"Couldn't find {nameof(BinaryReader)}.{mi_read_name} method");
            read = Expression.Lambda<Func<BinaryReader, T>>(
                Expression.Convert(Expression.Call(p_read_br, mi_read), typeof(T)),
                parameters: [p_read_br]
            ).Compile();

        }

    }

    /// <summary>
    /// </summary>
    public static void WriteEnum<T>(this BinaryWriter bw, T val)
        where T : struct, Enum => EnumOps<T>.write(bw, val);

    /// <summary>
    /// </summary>
    public static T ReadEnum<T>(this BinaryReader br)
        where T : struct, Enum => EnumOps<T>.read(br);

    #endregion

    #region Nullable

    /// <summary>
    /// </summary>
    public static void WriteNullableStruct<T>(this BinaryWriter bw, T? val_or_null, Action<BinaryWriter, T> write_val)
        where T : struct
    {
        if (val_or_null is T val)
        {
            bw.Write(true);
            write_val(bw, val);
        }
        else
        {
            bw.Write(false);
        }
    }

    /// <summary>
    /// </summary>
    public static T? ReadNullableStruct<T>(this BinaryReader br, Func<BinaryReader, T> read_val)
        where T : struct
    {
        var has_val = br.ReadBoolean();
        return has_val ? read_val(br) : null;
    }

    /// <summary>
    /// </summary>
    public static void WriteNullableClass<T>(this BinaryWriter bw, T? val_or_null, Action<BinaryWriter, T> write_val)
        where T : class
    {
        if (val_or_null is T val)
        {
            bw.Write(true);
            write_val(bw, val);
        }
        else
        {
            bw.Write(false);
        }
    }

    /// <summary>
    /// </summary>
    public static T? ReadNullableClass<T>(this BinaryReader br, Func<BinaryReader, T> read_val)
        where T : class
    {
        var has_val = br.ReadBoolean();
        return has_val ? read_val(br) : null;
    }

    #endregion

    #region Tuple

    /// <summary>
    /// </summary>
    public static void Write2<T>(this BinaryWriter bw, (T, T) val, Action<BinaryWriter, T> write_val)
    {
        write_val(bw, val.Item1);
        write_val(bw, val.Item2);
    }

    /// <summary>
    /// </summary>
    public static (T, T) Read2<T>(this BinaryReader br, Func<BinaryReader, T> read_val)
    {
        var val1 = read_val(br);
        var val2 = read_val(br);
        return (val1, val2);
    }

    #endregion

    #region Array

    /// <summary>
    /// </summary>
    public static void WriteArray<T>(this BinaryWriter bw, T[] items, Action<BinaryWriter, T> write_item)
    {
        bw.Write(items.Length);
        for (Int32 i = 0; i < items.Length; i++)
            write_item(bw, items[i]);
    }

    /// <summary>
    /// </summary>
    public static T[] ReadArray<T>(this BinaryReader br, Func<BinaryReader, T> read_item)
    {
        var length = br.ReadInt32();
        var items = new T[length];
        for (Int32 i = 0; i < length; i++)
            items[i] = read_item(br);
        return items;
    }

    /// <summary>
    /// </summary>
    public static Action<BinaryWriter, T[]> Array<T>(Action<BinaryWriter, T> write_item) =>
        (bw, a) => bw.WriteArray(a, write_item);


    /// <summary>
    /// </summary>
    public static Func<BinaryReader, T[]> Array<T>(Func<BinaryReader, T> read_item) =>
        br => br.ReadArray(read_item);

    #endregion

}
