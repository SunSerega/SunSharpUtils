using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.InteropServices;

using SunSharpUtils.Ext.Expressions;
using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.Ext.UniversalBin;

//TODO Write tests

//TODO Get up to speed with StructSerializer in "vid list" solution and then split this file, so I have 1 per global type here

//TODO Interface to define custom default marshaling in any given type

/// <summary>
/// Marks a type for automatic serialization in <see cref="UniversalBinaryAdapter"/>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class AutoSerializedDataAttribute : Attribute;

/// <summary>
/// Marks a type to be versioned in <see cref="UniversalBinaryAdapter"/>
/// <para/>
/// Requires <see cref="AutoSerializedDataAttribute"/> to have any effect
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class VersionedDataAttribute : Attribute
{
    /// <summary>
    /// Must be a type that can be serialized with <see cref="UniversalBinaryAdapter"/>
    /// <para/>
    /// Must implement <see cref="IComparable{TSelf}"/> and <see cref="IEquatable{TSelf}"/>
    /// <para/>
    /// Usually an <see cref="Int32"/> or a <see cref="String"/>
    /// </summary>
    public required Object Version { get; init; }
}

/// <summary>
/// Specifies a format for an older version of a type marked with <see cref="VersionedDataAttribute"/>
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = true, Inherited = false)]
public sealed class VersionedDataOldVersionAttribute : Attribute
{
    /// <summary>
    /// Must be of the same type as <see cref="VersionedDataAttribute.Version"/>
    /// </summary>
    public required Object Version { get; init; }
    /// <summary>
    /// Must be a type that can be serialized with <see cref="UniversalBinaryAdapter"/>
    /// <para/>
    /// Usually that means it should be marked with <see cref="AutoSerializedDataAttribute"/>
    /// </summary>
    public required Type OldVersionDataType { get; init; }
}

/// <summary>
/// </summary>
public interface IVersionedDataOldVersion<TNextVersion>
{
    /// <summary>
    /// </summary>
    public TNextVersion Upgrade();
}

/// <summary>
/// Common public utils for <see cref="UniversalBinaryAdapter{T}"/>
/// </summary>
public static class UniversalBinaryAdapter
{

    /// <summary>
    /// </summary>
    public sealed class Lambda<T> : UniversalBinaryAdapter<T>
        where T : notnull
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
    public sealed class Unmanaged<T>() : UniversalBinaryAdapter<T>
        where T : unmanaged
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
        public static ConcurrentDictionary<Type, Func<IUniversalBinaryAdapter?>> AllDefaults { get; } = [];
        public static IUniversalBinaryAdapter GetDefaultForType(Type t)
        {
            if (!AllDefaults.TryGetValue(t, out var factory) || factory.Invoke() is not { } adapter)
                throw new InvalidOperationException($"Default {nameof(UniversalBinaryAdapter<>)} is not registered for type {t.FullName}");
            return adapter;
        }

        public static Boolean TryCreateAuto<T>([NotNullWhen(true)] out UniversalBinaryAdapter<T>? adapter)
            where T : notnull
        {
            var auto_attrib = typeof(T).GetCustomAttribute<AutoSerializedDataAttribute>(inherit: false);
            if (auto_attrib is null)
            {
                adapter = null;
                return false;
            }

            var nullability_context = new NullabilityInfoContext();
            var fields = typeof(T).GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                .ToArray(field =>
                {
                    if (field.FieldType.IsPointer)
                        throw new InvalidOperationException($"Type {typeof(T)} has a field {field.Name} of type {field.FieldType}, which is a pointer type and cannot be serialized");

                    var is_nullable_class = field.FieldType.IsClass && nullability_context.Create(field).ReadState != NullabilityState.NotNull;
                    var nullable_struct_base = Nullable.GetUnderlyingType(field.FieldType);

                    return (field, is_nullable_class, nullable_struct_base);
                });

            //TODO Use more expressions to optimize this
            // - Leave not reflection at runtime
            // - Avoid Object type

            void save_unversioned(BinaryWriter bw, T value)
            {
                foreach (var (field, is_nullable_class, nullable_struct_base) in fields)
                {
                    var field_adapter = GetDefaultForType(field.FieldType);
                    var field_value = field.GetValue(value);
                    if (is_nullable_class || nullable_struct_base is { })
                    {
                        if (field_value is null)
                        {
                            bw.Write(false);
                        }
                        else
                        {
                            bw.Write(true);
                            field_adapter.Save(bw, field_value);
                        }
                    }
                    else
                    {
                        field_adapter.Save(bw, field_value ?? throw null!);
                    }
                }
            }

            Func<BinaryReader, T> load_unversioned;
            {
                var p_br = Expression.Parameter(typeof(BinaryReader), "br");
                var ci_empty = typeof(T).GetConstructor(Type.EmptyTypes) ?? throw new InvalidOperationException($"Type {typeof(T)} does not have a parameterless constructor, which is required for auto-serialization");

                var e_lines = new List<Expression>();
                var e_variables = new List<ParameterExpression>();

                var member_inits = new List<MemberBinding>();
                foreach (var (field, is_nullable_class, nullable_struct_base) in fields)
                {
                    var e_variable = Expression.Variable(field.FieldType, field.Name);
                    e_variables.Add(e_variable);

                    var e_adapter = ExpressionInvokeHelper<Type>.Wrap(t => GetDefaultForType(t), Expression.Constant(nullable_struct_base ?? field.FieldType));

                    Expression e_load_obj;
                    if (is_nullable_class || nullable_struct_base is { })
                    {
                        e_load_obj = ExpressionInvokeHelper<IUniversalBinaryAdapter, BinaryReader>.Wrap((adapter, br) => br.ReadBoolean() ? adapter.Load(br) : null, e_adapter, p_br);
                    }
                    else
                    {
                        e_load_obj = ExpressionInvokeHelper<IUniversalBinaryAdapter, BinaryReader>.Wrap((adapter, br) => adapter.Load(br), e_adapter, p_br);
                    }

                    e_lines.Add(Expression.Assign(
                        left: e_variable,
                        right: Expression.Convert(e_load_obj, field.FieldType)

                    ));
                    member_inits.Add(Expression.Bind(field, e_variable));
                }

                e_lines.Add(Expression.MemberInit(Expression.New(ci_empty, arguments: []), member_inits));

                load_unversioned = Expression.Lambda<Func<BinaryReader, T>>(
                    Expression.Block(
                        type: typeof(T),
                        variables: e_variables,
                        expressions: e_lines
                    ),
                    p_br
                ).Compile();
            }

            var version_attrib = typeof(T).GetCustomAttribute<VersionedDataAttribute>(inherit: false);
            if (version_attrib is { Version: var current_version })
            {
                var version_type = current_version.GetType();
                if (!version_type.IsAssignableTo(typeof(IComparable<>).MakeGenericType(version_type)))
                    throw new InvalidOperationException($"The {nameof(VersionedDataAttribute)} on type {typeof(T)} has a Version {current_version} of type {version_type}, which does not implement IComparable<{version_type}>");
                if (!version_type.IsAssignableTo(typeof(IEquatable<>).MakeGenericType(version_type)))
                    throw new InvalidOperationException($"The {nameof(VersionedDataAttribute)} on type {typeof(T)} has a Version {current_version} of type {version_type}, which does not implement IEquatable<{version_type}>");

                var old_version_types = typeof(T).GetCustomAttributes<VersionedDataOldVersionAttribute>(inherit: false)
                    .ToDictionary(attr => attr.Version, attr => attr.OldVersionDataType);
                old_version_types.Keys.ForEach(version =>
                {
                    if (version.GetType() != version_type)
                        throw new InvalidOperationException($"The {nameof(VersionedDataOldVersionAttribute)} on type {typeof(T)} has an old version {version} of type {version.GetType()}, which is not the same as the current version type {version_type}");
                });

                var old_versions = new Dictionary<Object, (Type data_type, Func<Object, Object> upgrade)>
                {
                    [current_version] = (typeof(T), o => o)
                };
                old_version_types.Keys.OrderDescending().Prepend(current_version).PairwiseForEach((v2, v1) =>
                {
                    var data_type1 = old_version_types[v1];
                    var data_type2 = old_version_types[v2];
                    var old_version_interface = typeof(IVersionedDataOldVersion<>).MakeGenericType(data_type1);
                    if (!data_type2.IsAssignableTo(old_version_interface))
                        throw new InvalidOperationException($"The {nameof(VersionedDataOldVersionAttribute)} on type {typeof(T)} has an old version {v2} of type {data_type2}, which does not implement IVersionedDataOldVersion<{data_type1}> for the next version {v1}");
                    var mi_upgrade = old_version_interface.GetInterfaceMap(old_version_interface).TargetMethods.Single();

                    var p_old_obj = Expression.Parameter(typeof(Object), "o");

                    var e_upgrade = Expression.Lambda<Func<Object, Object>>(
                        ExpressionInvokeHelper<Object, Func<Object, Object>>.Wrap(
                            (o, next) => next.Invoke(o),
                            Expression.Convert(
                                Expression.Call(
                                    instance: Expression.Convert(p_old_obj, data_type1),
                                    method: mi_upgrade
                                ),
                                typeof(Object)
                            ),
                            Expression.Constant(old_versions[v1])
                        ),
                        p_old_obj
                    );

                    old_versions.Add(v2, (data_type2, e_upgrade.Compile()));
                });


                adapter = new Lambda<T>
                {
                    Saver = (bw, value) =>
                    {
                        var version_adapter = GetDefaultForType(version_type);
                        version_adapter.Save(bw, current_version);
                        save_unversioned(bw, value);
                    },
                    Loader = br =>
                    {
                        var version_adapter = GetDefaultForType(version_type);
                        var version = version_adapter.Load(br);
                        if (version.Equals(current_version)) //TODO Use IEquatable<>
                            return load_unversioned(br);
                        if (!old_versions.TryGetValue(version, out var old_version))
                            throw new InvalidOperationException($"Type {typeof(T)} has no registered data type for old version {version}");
                        var data_adapter = GetDefaultForType(old_version.data_type);
                        var old_data = data_adapter.Load(br);
                        return (T)old_version.upgrade(old_data);
                    },
                };
            }
            else
            {
                adapter = new Lambda<T>
                {
                    Saver = save_unversioned,
                    Loader = load_unversioned,
                };
            }
            return true;
        }

        public static Boolean TryCreateForUnmanaged<T>([NotNullWhen(true)] out UniversalBinaryAdapter<T>? adapter)
            where T : notnull
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

internal interface IUniversalBinaryAdapter
{
    public void Save(BinaryWriter bw, Object value);
    public Object Load(BinaryReader br);
}
/// <summary>
/// Contains Save and Load methods for a specific type T, to be used with BinaryWriter and BinaryReader
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class UniversalBinaryAdapter<T> : IUniversalBinaryAdapter
    where T : notnull
{

    static UniversalBinaryAdapter()
    {
        if (!UniversalBinaryAdapter.InternalUtils.AllDefaults.TryAdd(typeof(T), () => Default))
            throw new InvalidOperationException($"A IUniversalBinaryAdapter implementation for type {typeof(T)} is already registered");

        if (UniversalBinaryAdapter.InternalUtils.TryCreateAuto<T>(out var auto_adapter))
        {
            Default = auto_adapter;
            return;
        }

        if (typeof(T) == typeof(String))
        {
            UniversalBinaryAdapter<String>.Default = (
                saver: (bw, value) => bw.Write(value),
                loader: br => br.ReadString()
            );
            return;
        }

        if (typeof(T) == typeof(DateTime))
        {
            UniversalBinaryAdapter<DateTime>.Default = (
                saver: (bw, value) => bw.Write(value.ToBinary()),
                loader: br => DateTime.FromBinary(br.ReadInt64())
            );
            return;
        }

        if (UniversalBinaryAdapter.InternalUtils.TryCreateForUnmanaged<T>(out var unmanaged_adapter))
        {
            Default = unmanaged_adapter;
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

    void IUniversalBinaryAdapter.Save(BinaryWriter bw, Object value) => this.Save(bw, (T)value);
    Object IUniversalBinaryAdapter.Load(BinaryReader br) => this.Load(br);

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
        where T : notnull
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
        where T : notnull
    {
        var adapter = UniversalBinaryAdapter<T>.DefaultOrThrow;
        return adapter.Load(br);
    }

}
