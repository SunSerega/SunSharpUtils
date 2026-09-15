using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

using SunSharpUtils.Ext.Expressions;
using SunSharpUtils.Ext.Linq;
using SunSharpUtils.Threading;

namespace SunSharpUtils.UniversalBin;

//TODO Implement UnmanagedArray
// - Faster to read everything as just one block of bytes (+length prefix)

//TODO Get up to speed with StructSerializer in "vid list" solution and then split this file, so I have 1 per global type here

//TODO Maybe use this for the settings?
// - Might be too specific for the use case (can't think of too specific part of the top of my head)
// - But maybe if I introduce assembly overrides...

/// <summary>
/// Marks a type for automatic serialization in <see cref="UniversalBinaryAdapter"/>
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class AutoSerializedDataAttribute(Boolean AllowEmpty = false) : Attribute
{
    /// <summary>
    /// </summary>
    public Boolean AllowEmpty { get; init; } = AllowEmpty;
}

/// <summary>
/// Marks a type to be versioned in <see cref="UniversalBinaryAdapter"/>
/// <para/>
/// Requires <see cref="AutoSerializedDataAttribute"/> to have any effect
/// </summary>
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
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
[AttributeUsage(AttributeTargets.Struct | AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = true, Inherited = false)]
public sealed class VersionedDataOldVersionAttribute : Attribute
{
    /// <summary>
    /// Must be of the same type as <see cref="VersionedDataAttribute.Version"/>
    /// </summary>
    public required Object Version { get; init; }
    /// <summary>
    /// Must be serializable with <see cref="UniversalBinaryAdapter"/>
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
/// Marks an abstract class or interface to be serialized with an enum header determining the concrete implementation type
/// <para/>
/// Requires <see cref="AutoSerializedDataAttribute"/> to have any effect
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Interface, AllowMultiple = false, Inherited = false)]
public sealed class AbstractDataAttribute : Attribute
{
    /// <summary>
    /// Each implementation type must derive/implement this type and be serializable with <see cref="UniversalBinaryAdapter"/><br/>
    /// Usually that means it should be marked with <see cref="AutoSerializedDataAttribute"/>
    /// <para/>
    /// Within one version, the only allowed change is adding new items to the end<br/>
    /// Moving/reordering items breaks binary compatibility and so requires defining a new version
    /// </summary>
    public required Type[] ExpectedImplementations { get; init; }
}

/// <summary>
/// Marks type as serializable via <see cref="UniversalBinaryAdapter"/> with a custom default Save/Load implementation
/// </summary>
/// <typeparam name="TSelf"></typeparam>
public interface ISerializableData<TSelf>
    where TSelf : ISerializableData<TSelf>
{
    /// <summary>
    /// </summary>
    public static abstract void Save(BinaryWriter bw, TSelf value);
    /// <summary>
    /// </summary>
    public static abstract TSelf Load(BinaryReader br);
}

/// <summary>
/// Marks type as serializable via <see cref="UniversalBinaryAdapter"/> with a custom default Save/Load implementation and explicit dependencies on other <see cref="UniversalBinaryAdapter{T}.Default"/> values<br/>
/// </summary>
/// <typeparam name="TSelf"></typeparam>
public interface ISerializableDataWithDeps<TSelf>
    where TSelf : ISerializableDataWithDeps<TSelf>
{
    /// <summary>
    /// </summary>
    public static abstract Func<(UniversalBinaryAdapter<TSelf>.SaverDelegate saver, UniversalBinaryAdapter<TSelf>.LoaderDelegate loader)> DefineSaverAndLoader(UniversalBinaryAdapter.LambdaWithDeps<TSelf>.InitContext context);
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
        public required SaverDelegate Saver { get; init; }
        /// <summary>
        /// </summary>
        public required LoaderDelegate Loader { get; init; }

        /// <inheritdoc cref="UniversalBinaryAdapter{T}.Save(BinaryWriter, T)"/>
        public override void Save(BinaryWriter bw, T value) => this.Saver(bw, value);
        /// <inheritdoc cref="UniversalBinaryAdapter{T}.Load(BinaryReader)"/>
        public override T Load(BinaryReader br) => this.Loader(br);
    }

    /// <summary>
    /// </summary>
    public sealed class LambdaWithDeps<T> : UniversalBinaryAdapter<T>
        where T : notnull
    {
        private readonly Lock l_cache = new();
        private (SaverDelegate saver, LoaderDelegate loader)? cache = null;
        private readonly Func<(SaverDelegate saver, LoaderDelegate loader)> factory;

        /// <summary>
        /// </summary>
        public LambdaWithDeps(Func<InitContext, Func<(SaverDelegate saver, LoaderDelegate loader)>> factory_factory)
        {
            using var init_context = new InitContext(this);
            try
            {
                this.factory = factory_factory.Invoke(init_context);
            }
            catch (Exception ex)
            {
                throw new InvalidDataException($"Failed to create saver/loader factory of {nameof(LambdaWithDeps<>)}<{typeof(T)}>", ex);
            }
        }
        /// <summary>
        /// </summary>
        public sealed class InitContext(LambdaWithDeps<T> result) : IDisposable
        {
            private readonly LambdaWithDeps<T> result = result;
            private Boolean is_disposed = false;

            /// <summary>
            /// </summary>
            public Func<UniversalBinaryAdapter<TDep>> AddDependency<TDep>()
                where TDep : notnull
            {
                if (this.is_disposed)
                    throw new InvalidOperationException($"Cannot add dependency {typeof(TDep)} to {nameof(LambdaWithDeps<>)}<{typeof(T)}> because init context is already disposed");
                var result = this.result;
                UniversalBinaryAdapter<TDep>.DefaultChanged += () =>
                {
                    lock (result.l_cache)
                        result.cache = null;
                };
                return () => UniversalBinaryAdapter<TDep>.Default ?? throw new LambdaDependencyMissingException([typeof(TDep)]);
            }

            internal (Type t_adapter, Func<IUniversalBinaryAdapter> get_adapter, MethodInfo mi_save, MethodInfo mi_load) AddDependency(Type t)
            {
                var t_adapter = typeof(UniversalBinaryAdapter<>).MakeGenericType(t);

                var mi = typeof(InitContext).GetMethod(nameof(AddDependency), BindingFlags.Public | BindingFlags.Instance, Type.EmptyTypes) ?? throw null!;
                var get_adapter = (Func<IUniversalBinaryAdapter>)(mi.MakeGenericMethod(t).Invoke(this, parameters: []) ?? throw null!);

                var mi_save = t_adapter.GetMethod(nameof(UniversalBinaryAdapter<>.Save), BindingFlags.Public | BindingFlags.Instance, types: [typeof(BinaryWriter), t]) ?? throw null!;
                var mi_load = t_adapter.GetMethod(nameof(UniversalBinaryAdapter<>.Load), BindingFlags.Public | BindingFlags.Instance, types: [typeof(BinaryReader)]) ?? throw null!;

                return (t_adapter, get_adapter, mi_save, mi_load);
            }

            /// <summary>
            /// Preventing any further dependencies from being added
            /// </summary>
            public void Dispose() => this.is_disposed = true;

        }

        private (SaverDelegate saver, LoaderDelegate loader) GetDelegates() =>
            this.l_cache.LockedGet(() => this.cache ??= this.factory());

        /// <inheritdoc cref="UniversalBinaryAdapter{T}.Save(BinaryWriter, T)"/>
        public override void Save(BinaryWriter bw, T value)
        {
            try
            {
                this.GetDelegates().saver.Invoke(bw, value);
            }
            catch (LambdaDependencyMissingException ex)
            {
                throw new LambdaDependencyMissingException([typeof(T), .. ex.Path]);
            }
        }
        /// <inheritdoc cref="UniversalBinaryAdapter{T}.Load(BinaryReader)"/>
        public override T Load(BinaryReader br)
        {
            try
            {
                return this.GetDelegates().loader(br);
            }
            catch (LambdaDependencyMissingException ex)
            {
                throw new LambdaDependencyMissingException([typeof(T), .. ex.Path]);
            }
        }

    }
    /// <summary>
    /// Thrown when trying to use <see cref="LambdaWithDeps{T}"/> to save/load a value, but one of the dependant <see cref="UniversalBinaryAdapter{T}.Default"/> is null
    /// </summary>
    /// <param name="path"></param>
    public sealed class LambdaDependencyMissingException(Type[] path)
        : Exception($"{nameof(UniversalBinaryAdapter<>)}<{path.First()}> cannot be used because it depends on a {nameof(UniversalBinaryAdapter<>)}<{path.Last()}>.{nameof(UniversalBinaryAdapter<>.Default)} to not be null. Dependency path: {path.JoinToString(" => ")}")
    {
        /// <summary>
        /// From the root type being saved/loaded to the type that does not have a <see cref="UniversalBinaryAdapter{T}.Default"/>
        /// </summary>
        public Type[] Path { get; } = path;
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

        private static Boolean CheckImplementsSelfRefInterface(Type t, Type interface_generic_type)
        {
            foreach (var interface_type in t.GetInterfaces())
            {
                if (!interface_type.IsGenericType)
                    continue;
                if (interface_type.GetGenericTypeDefinition() != interface_generic_type)
                    continue;
                var generic_args = interface_type.GetGenericArguments();
                if (generic_args.Length != 1)
                    throw new InvalidOperationException($"Interface {interface_generic_type} on type {t} does not have exactly one generic argument");
                if (generic_args[0] != t)
                    continue;
                return true;
            }
            return false;
        }

        private static LambdaWithDeps<T> CreateCustomWithDeps<T>()
            where T : ISerializableDataWithDeps<T>
        {
            return new(T.DefineSaverAndLoader);
        }
        public static Boolean TryCreateCustomWithDeps<T>([NotNullWhen(true)] out UniversalBinaryAdapter<T>? adapter)
            where T : notnull
        {
            if (!CheckImplementsSelfRefInterface(typeof(T), typeof(ISerializableDataWithDeps<>)))
            {
                adapter = null;
                return false;
            }
            var mi_CreateCustomWithDeps = typeof(InternalUtils).GetMethod(nameof(CreateCustomWithDeps), BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes) ?? throw null!;
            adapter = (LambdaWithDeps<T>?)mi_CreateCustomWithDeps.MakeGenericMethod(typeof(T)).Invoke(null, null) ?? throw null!;
            return true;
        }

        private static Lambda<T> CreateCustom<T>()
            where T : ISerializableData<T>
        {
            return new()
            {
                Saver = T.Save,
                Loader = T.Load
            };
        }
        public static Boolean TryCreateCustom<T>([NotNullWhen(true)] out UniversalBinaryAdapter<T>? adapter)
            where T : notnull
        {
            if (!CheckImplementsSelfRefInterface(typeof(T), typeof(ISerializableData<>)))
            {
                adapter = null;
                return false;
            }
            var mi_CreateCustom = typeof(InternalUtils).GetMethod(nameof(CreateCustom), BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes) ?? throw null!;
            adapter = (Lambda<T>?)mi_CreateCustom.MakeGenericMethod(typeof(T)).Invoke(null, null) ?? throw null!;
            return true;
        }

        private static LambdaWithDeps<T[]> CreateArray<T>()
            where T : notnull
        {
            return new(context =>
            {
                var (t_element_adapter, get_element_adapter, mi_save, mi_load) = context.AddDependency(typeof(T));

                var p_bw = Expression.Parameter(typeof(BinaryWriter), "bw");
                var p_array = Expression.Parameter(typeof(T[]), "array");
                var p_br = Expression.Parameter(typeof(BinaryReader), "br");
                var v_result = Expression.Variable(typeof(T[]), "result");

                var p_element_adapter = Expression.Parameter(t_element_adapter, "element_adapter");

                var e_saver_lines = new List<Expression>();
                var e_loader_lines = new List<Expression>();

                var v_length = Expression.Variable(typeof(Int32), "length");
                var v_i = Expression.Variable(typeof(Int32), "i");

                e_saver_lines.Add(
                    Expression.Assign(
                        left: v_length,
                        right: Expression.ArrayLength(p_array)
                    )
                );
                e_saver_lines.Add(
                    ExpressionInvokeHelper<BinaryWriter, Int32>.Wrap((bw, length) => bw.Write(length), p_bw, v_length)
                );
                e_loader_lines.Add(
                    Expression.Assign(
                        left: v_length,
                        right: ExpressionInvokeHelper<BinaryReader>.Wrap(br => br.ReadInt32(), p_br)
                    )
                );
                e_loader_lines.Add(
                    Expression.Assign(
                        left: v_result,
                        right: Expression.NewArrayBounds(typeof(T), v_length)
                    )
                );

                var l_loop_begin = Expression.Label("loop_begin");
                var l_loop_end = Expression.Label("loop_end");

                void AddToBoth(Expression line)
                {
                    e_saver_lines.Add(line);
                    e_loader_lines.Add(line);
                }

                AddToBoth(Expression.Assign(v_i, Expression.Constant(0)));
                AddToBoth(Expression.Label(l_loop_begin));
                AddToBoth(
                    Expression.IfThen(
                        test: Expression.GreaterThanOrEqual(v_i, v_length),
                        ifTrue: Expression.Break(l_loop_end)
                    )
                );

                e_saver_lines.Add(
                    Expression.Call(p_element_adapter, mi_save, p_bw, Expression.ArrayAccess(p_array, v_i))
                );
                e_loader_lines.Add(
                    Expression.Assign(
                        left: Expression.ArrayAccess(v_result, v_i),
                        right: Expression.Call(p_element_adapter, mi_load, p_br)
                    )
                );

                AddToBoth(Expression.PreIncrementAssign(v_i));
                AddToBoth(Expression.Goto(l_loop_begin));
                AddToBoth(Expression.Label(l_loop_end));
                e_loader_lines.Add(v_result);

                var e_saver_lambda = Expression.Lambda(Expression.Block(variables: [v_length, v_i], e_saver_lines), parameters: [p_element_adapter]);
                var e_loader_lambda = Expression.Lambda(Expression.Block(variables: [v_length, v_i, v_result], e_loader_lines), parameters: [p_element_adapter]);

                return () =>
                {
                    var e_element_adapter = Expression.Constant(get_element_adapter());
                    var e_saver = Expression.Lambda<UniversalBinaryAdapter<T[]>.SaverDelegate>(Expression.Invoke(e_saver_lambda, e_element_adapter), p_bw, p_array);
                    var e_loader = Expression.Lambda<UniversalBinaryAdapter<T[]>.LoaderDelegate>(Expression.Invoke(e_loader_lambda, e_element_adapter), p_br);
                    return (e_saver.Compile(), e_loader.Compile());
                };
            });
        }
        public static Boolean TryCreateArray<T>([NotNullWhen(true)] out UniversalBinaryAdapter<T>? adapter)
            where T : notnull
        {
            if (!typeof(T).IsArray || typeof(T).GetArrayRank() is not 1)
            {
                adapter = null;
                return false;
            }
            var mi_CreateArray = typeof(InternalUtils).GetMethod(nameof(CreateArray), BindingFlags.NonPublic | BindingFlags.Static, Type.EmptyTypes) ?? throw null!;
            adapter = (UniversalBinaryAdapter<T>?)mi_CreateArray.MakeGenericMethod(typeof(T).GetElementType() ?? throw null!).Invoke(null, null) ?? throw null!;
            return true;
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
            var allow_empty = auto_attrib.AllowEmpty; //TODO Maybe make it a separate attrib, incompatible with [AbstractData]

            adapter = new LambdaWithDeps<T>(context =>
            {
                var p_bw = Expression.Parameter(typeof(BinaryWriter), "bw");
                var p_br = Expression.Parameter(typeof(BinaryReader), "br");

                var e_dep_arguments_getters = new List<Func<ConstantExpression>>();
                var e_dep_parameters = new List<ParameterExpression>();
                (LambdaExpression e_save, LambdaExpression e_load) AddDependency(Type t)
                {
                    var (t_adapter, get_adapter, mi_save, mi_load) = context.AddDependency(t);

                    var p_adapter = Expression.Parameter(t_adapter, $"adapter_for_{t.Name}");
                    e_dep_arguments_getters.Add(() => Expression.Constant(get_adapter.Invoke()));
                    e_dep_parameters.Add(p_adapter);

                    var p_value = Expression.Parameter(t, "value");

                    //TODO I can optimize futher, by getting Save/Load expression from p_adapter (which would be simplified for another auto type)
                    var e_save = Expression.Lambda(Expression.Call(p_adapter, mi_save, p_bw, p_value), p_value);
                    var e_load = Expression.Lambda(Expression.Call(p_adapter, mi_load, p_br));

                    return (e_save, e_load);
                }

                var abstract_attrib = typeof(T).GetCustomAttribute<AbstractDataAttribute>(inherit: false);
                var t_is_abstract = typeof(T).IsAbstract || typeof(T).IsInterface;
                if (t_is_abstract != (abstract_attrib is not null))
                {
                    if (t_is_abstract)
                        throw new InvalidOperationException($"[{nameof(AutoSerializedDataAttribute)}] Abstract (or interface) type {typeof(T)} needs to have an {nameof(AbstractDataAttribute)} attribute");
                    else
                        throw new InvalidOperationException($"[{nameof(AutoSerializedDataAttribute)}] Non-abstract type {typeof(T)} cannot have an {nameof(AbstractDataAttribute)} attribute");
                }

                var p_value = Expression.Parameter(typeof(T), "value");

                Expression e_saver_body;
                Expression e_loader_body;
                if (abstract_attrib is { ExpectedImplementations: var expected_implementations })
                {
                    #region Abstract
                    var type_to_index = new Dictionary<Type, Int32>();
                    for (var i = 0; i < expected_implementations.Length; i++)
                    {
                        var t_impl = expected_implementations[i];
                        if (!t_impl.IsAssignableTo(typeof(T)))
                            throw new InvalidOperationException($"[{nameof(AbstractDataAttribute)}] Expected implementation type {t_impl} has to be assignable to variables of base type {typeof(T)}");
                        type_to_index.Add(t_impl, i);
                    }
                    Int32 GetTypeIndexToSave(T value)
                    {
                        var t = value.GetType();
                        if (!type_to_index.TryGetValue(t, out var result))
                            throw new InvalidOperationException($"[{nameof(AbstractDataAttribute)}] Cannot save value ({value}) of type {t} as implementation of {typeof(T)} because it is not defined in the list of expected implementations");
                        return result;
                    }

                    var e_saver_lines = new List<Expression>();
                    var e_loader_lines = new List<Expression>();

                    var v_type_ind = Expression.Variable(typeof(Int32), "type_index");
                    {
                        Delegate d_GetTypeIndexToSave = GetTypeIndexToSave;
                        e_saver_lines.Add(
                            Expression.Assign(
                                left: v_type_ind,
                                right: Expression.Call(Expression.Constant(d_GetTypeIndexToSave.Target), d_GetTypeIndexToSave.Method, p_value)
                            )
                        );
                        e_saver_lines.Add(
                            ExpressionInvokeHelper<BinaryWriter, Int32>.Wrap((bw, type_ind) => bw.Write(type_ind), p_bw, v_type_ind)
                        );

                        e_loader_lines.Add(
                            Expression.Assign(
                                left: v_type_ind,
                                right: ExpressionInvokeHelper<BinaryReader>.Wrap(br => br.ReadInt32(), p_br)
                            )
                        );
                    }

                    {
                        var e_saver_cases = new List<SwitchCase>();
                        var e_loader_cases = new List<SwitchCase>();

                        for (var i = 0; i < expected_implementations.Length; i++)
                        {
                            var t_impl = expected_implementations[i];
                            var (e_save, e_load) = AddDependency(t_impl);

                            e_saver_cases.Add(
                                Expression.SwitchCase(
                                    Expression.Invoke(e_save, Expression.Convert(p_value, t_impl)),
                                    Expression.Constant(i)
                                )
                            );
                            e_loader_cases.Add(
                                Expression.SwitchCase(
                                    Expression.Convert(Expression.Invoke(e_load), typeof(T)),
                                    Expression.Constant(i)
                                )
                            );
                        }

                        var e_ex_invalid_type_index = ExpressionInvokeHelper<Int32>.Wrap(type_ind => new InvalidDataException($"[{nameof(AbstractDataAttribute)}] Invalid type index {type_ind} for abstract type {typeof(T)}"), v_type_ind);

                        e_saver_lines.Add(
                            Expression.Switch(
                                switchValue: v_type_ind,
                                defaultBody: Expression.Throw(
                                    ExpressionInvokeHelper<Int32>.Wrap(type_ind => new InvalidDataException($"[{nameof(AbstractDataAttribute)}] Invalid type index {type_ind} for abstract type {typeof(T)}"), v_type_ind)
                                ),
                                comparison: null,
                                cases: e_saver_cases.ToArray()
                            )
                        );
                        e_loader_lines.Add(
                            Expression.Switch(
                                type: typeof(T),
                                switchValue: v_type_ind,
                                defaultBody: Expression.Throw(
                                    ExpressionInvokeHelper<Int32>.Wrap(type_ind => new InvalidDataException($"[{nameof(AbstractDataAttribute)}] Invalid type index {type_ind} for abstract type {typeof(T)}"), v_type_ind),
                                    typeof(T)
                                ),
                                comparison: null,
                                cases: e_loader_cases.ToArray()
                            )
                        );
                    }

                    e_saver_body = Expression.Block(variables: [v_type_ind], e_saver_lines);
                    e_loader_body = Expression.Block(typeof(T), variables: [v_type_ind], e_loader_lines);
                    #endregion
                }
                else
                {
                    #region Field-wise
                    var bf = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance;
                    var fields = typeof(T).GetFields(bf);
                    if (fields.Length == 0 && typeof(T).GetProperties(bf).Length != 0 && !allow_empty)
                        throw new InvalidOperationException($"[{nameof(AutoSerializedDataAttribute)}] Must define fields, not properties");

                    var nullability_context = new NullabilityInfoContext();

                    var e_saver_lines = new List<Expression>();
                    var e_loader_lines = new List<Expression>();

                    var v_saver_vars = new List<ParameterExpression>();
                    var v_loader_vars = new List<ParameterExpression>();
                    var e_member_inits = new List<MemberBinding>();

                    var ci_read_value = typeof(T).GetConstructor(Type.EmptyTypes) ?? throw new InvalidOperationException($"Type {typeof(T)} does not have a parameterless constructor, which is required for auto-serialization");
                    var e_new_read_value = Expression.New(ci_read_value);

                    foreach (var field in fields)
                    {
                        if (field.FieldType.IsPointer)
                            throw new InvalidOperationException($"[{nameof(AutoSerializedDataAttribute)}] Type {typeof(T)} has a field {field.Name} of type {field.FieldType}, which is a pointer type and cannot be serialized");

                        var nullability_info = nullability_context.Create(field);
                        var is_nullable_class = field.FieldType.IsClass && nullability_info.ReadState != NullabilityState.NotNull;
                        var nullable_struct_base = Nullable.GetUnderlyingType(field.FieldType);

                        var t_nullable_base = nullable_struct_base ?? field.FieldType;
                        var (e_save, e_load) = AddDependency(t_nullable_base);

                        var v_read_field = Expression.Parameter(field.FieldType, field.Name);
                        v_loader_vars.Add(v_read_field);
                        e_member_inits.Add(Expression.Bind(field, v_read_field));

                        var e_field = Expression.Field(p_value, field);

                        if (is_nullable_class || nullable_struct_base is { })
                        {
                            var v_has_value = Expression.Variable(typeof(Boolean), $"{field.Name}_has_value");
                            v_saver_vars.Add(v_has_value);
                            v_loader_vars.Add(v_has_value);

                            e_saver_lines.Add(
                                Expression.Assign(
                                    left: v_has_value,
                                    right: Expression.NotEqual(e_field, Expression.Constant(null, field.FieldType))
                                )
                            );
                            e_saver_lines.Add(
                                ExpressionInvokeHelper<BinaryWriter, Boolean>.Wrap(
                                    (bw, has_value) => bw.Write(has_value),
                                    p_bw, v_has_value
                                )
                            );
                            e_loader_lines.Add(
                                Expression.Assign(
                                    left: v_has_value,
                                    right: ExpressionInvokeHelper<BinaryReader>.Wrap(br => br.ReadBoolean(), p_br)
                                )
                            );

                            e_saver_lines.Add(
                                Expression.IfThen(
                                    test: v_has_value,
                                    ifTrue: Expression.Invoke(e_save, Expression.Convert(e_field, t_nullable_base))
                                )
                            );
                            e_loader_lines.Add(
                                Expression.Assign(
                                    left: v_read_field,
                                    right: Expression.Condition(
                                        test: v_has_value,
                                        ifTrue: Expression.Convert(Expression.Invoke(e_load), field.FieldType),
                                        ifFalse: Expression.Constant(null, field.FieldType)
                                    )
                                )
                            );

                        }
                        else
                        {
                            e_saver_lines.Add(
                                Expression.Invoke(e_save, e_field)
                            );
                            e_loader_lines.Add(
                                Expression.Assign(
                                    left: v_read_field,
                                    right: Expression.Invoke(e_load)
                                )
                            );
                        }
                    }

                    e_loader_lines.Add(
                        Expression.MemberInit(e_new_read_value, e_member_inits)
                    );

                    e_saver_body = Expression.Block(variables: v_saver_vars, e_saver_lines);
                    e_loader_body = Expression.Block(typeof(T), variables: v_loader_vars, e_loader_lines);
                    #endregion
                }

                var version_attrib = typeof(T).GetCustomAttribute<VersionedDataAttribute>(inherit: false);
                if (version_attrib is { Version: var current_version })
                {
                    #region Versionable

                    var version_type = current_version.GetType();
                    if (!version_type.IsAssignableTo(typeof(IComparable<>).MakeGenericType(version_type)))
                        throw new InvalidOperationException($"The {nameof(VersionedDataAttribute)} on type {typeof(T)} has Version={current_version} of type {version_type}, which does not implement IComparable<{version_type}>");
                    if (!version_type.IsAssignableTo(typeof(IEquatable<>).MakeGenericType(version_type)))
                        throw new InvalidOperationException($"The {nameof(VersionedDataAttribute)} on type {typeof(T)} has Version={current_version} of type {version_type}, which does not implement IEquatable<{version_type}>");

                    var version_types = typeof(T).GetCustomAttributes<VersionedDataOldVersionAttribute>(inherit: false)
                        .ToDictionary(attr => attr.Version, attr => attr.OldVersionDataType);
                    version_types.Keys.ForEach(version =>
                    {
                        if (version.GetType() != version_type)
                            throw new InvalidOperationException($"The {nameof(VersionedDataOldVersionAttribute)} on type {typeof(T)} has an old Version={version} of type {version.GetType()}, which is not the same as the current version type {version_type}");
                    });
                    version_types.Add(current_version, typeof(T));

                    var e_version_upgraders = new Dictionary<Object, LambdaExpression>();
                    {
                        var p_data = Expression.Parameter(typeof(T), "data");
                        var e_upgrade = Expression.Lambda(p_data, p_data);
                        e_version_upgraders.Add(current_version, e_upgrade);
                    }
                    version_types.Keys.OrderDescending().PairwiseForEach((v2, v1) =>
                    {
                        var data_type1 = version_types[v1];
                        var data_type2 = version_types[v2];
                        var old_version_interface = typeof(IVersionedDataOldVersion<>).MakeGenericType(data_type2);
                        if (!data_type1.IsAssignableTo(old_version_interface))
                            throw new InvalidOperationException($"The {nameof(VersionedDataOldVersionAttribute)} on type {typeof(T)} has an old Version={v1} of type {data_type1}, which does not implement {nameof(IVersionedDataOldVersion<>)}<{data_type2}> for the next Version={v2}");
                        var mi_upgrade = data_type1.GetInterfaceMap(old_version_interface).TargetMethods.Single();

                        var p_old_data = Expression.Parameter(data_type1, "old_data");

                        var e_upgrade = Expression.Lambda(
                            Expression.Invoke(
                                e_version_upgraders[v2],
                                Expression.Call(
                                    instance: p_old_data,
                                    method: mi_upgrade
                                )
                            ),
                            p_old_data
                        );

                        e_version_upgraders.Add(v1, e_upgrade);
                    });

                    var (e_version_save, e_version_load) = AddDependency(version_type);
                    var e_loader_lines = new List<Expression>();

                    var v_version = Expression.Variable(version_type, "version");
                    e_loader_lines.Add(
                        Expression.Assign(
                            left: v_version,
                            right: Expression.Invoke(e_version_load)
                        )
                    );

                    var e_loader_cases = new List<SwitchCase>();
                    foreach (var (version, e_upgrade) in e_version_upgraders.OrderBy(kv => kv.Key))
                    {
                        var data_type = version_types[version];

                        var e_load = data_type == typeof(T)
                            ? e_loader_body
                            : Expression.Invoke(
                                e_upgrade,
                                Expression.Invoke(AddDependency(data_type).e_load)
                            );
                        e_loader_cases.Add(Expression.SwitchCase(e_load, Expression.Constant(version)));
                    }
                    var e_throw_invalid_version = Expression.Throw(
                        ExpressionInvokeHelper<Object>.Wrap(
                            version => new InvalidDataException($"[{nameof(VersionedDataAttribute)}] Invalid Version={version} for type {typeof(T)}"),
                            Expression.Convert(v_version, typeof(Object))
                        ),
                        typeof(T)
                    );
                    e_loader_lines.Add(
                        Expression.Switch(typeof(T), v_version, defaultBody: e_throw_invalid_version, comparison: null, e_loader_cases.ToArray())
                    );

                    e_saver_body = Expression.Block([
                        Expression.Invoke(e_version_save, Expression.Constant(current_version)),
                        e_saver_body
                    ]);
                    e_loader_body = Expression.Block(variables: [v_version], e_loader_lines);

                    #endregion
                }

                var e_saver_lambda = Expression.Lambda(e_saver_body, e_dep_parameters);
                var e_loader_lambda = Expression.Lambda(e_loader_body, e_dep_parameters);

                return () =>
                {
                    var e_dep_arguments = e_dep_arguments_getters.ToArray(getter => getter.Invoke());
                    var e_saver = Expression.Lambda<UniversalBinaryAdapter<T>.SaverDelegate>(Expression.Invoke(e_saver_lambda, e_dep_arguments), p_bw, p_value);
                    var e_loader = Expression.Lambda<UniversalBinaryAdapter<T>.LoaderDelegate>(Expression.Invoke(e_loader_lambda, e_dep_arguments), p_br);
                    return (e_saver.Compile(), e_loader.Compile());
                };
            });

            return true;
        }

        private static readonly Lock l_contains_forbidden_type_cache = new();
        private static readonly Dictionary<Type, Boolean> contains_forbidden_type_cache = [];
        public static Boolean TryCreateForUnmanaged<T>([NotNullWhen(true)] out UniversalBinaryAdapter<T>? adapter)
            where T : notnull
        {
            if (ContainsRefs(typeof(T)) || ContainsForbiddenType(typeof(T)))
            {
                adapter = null;
                return false;
            }
            adapter = (UniversalBinaryAdapter<T>?)Activator.CreateInstance(typeof(Unmanaged<>).MakeGenericType(typeof(T))) ?? throw null!;
            return true;

            static Boolean ContainsRefs(Type type) =>
                (Boolean)typeof(RuntimeHelpers).GetMethod(nameof(RuntimeHelpers.IsReferenceOrContainsReferences))!.MakeGenericMethod(type).Invoke(obj: null, parameters: [])!;
            Boolean ContainsForbiddenType(Type type)
            {
                using var lock_scope = l_contains_forbidden_type_cache.EnterScope();
                if (contains_forbidden_type_cache.TryGetValue(type, out var result))
                    return result;
                result = true; // For the finally, in case we crash somehow
                contains_forbidden_type_cache.Add(type, false); // For recursion, because standard structs like Int32 contain themselves as a field
                try
                {
                    result = ContainsForbiddenTypeUncached(type);
                }
                finally
                {
                    contains_forbidden_type_cache[type] = result;
                }
                return result;
            }
            Boolean ContainsForbiddenTypeUncached(Type type)
            {
                if (Nullable.GetUnderlyingType(type) is { })
                    return true;
                foreach (var field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (ContainsForbiddenType(field.FieldType))
                        return true;
                }
                return false;
            }
        }

    }

}

internal interface IUniversalBinaryAdapter;
/// <summary>
/// Contains Save and Load methods for a specific type T, to be used with BinaryWriter and BinaryReader
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class UniversalBinaryAdapter<T> : IUniversalBinaryAdapter
    where T : notnull
{

    /// <summary>
    /// </summary>
    public delegate void SaverDelegate(BinaryWriter bw, T value);
    /// <summary>
    /// </summary>
    public delegate T LoaderDelegate(BinaryReader br);

    static UniversalBinaryAdapter()
    {

        if (UniversalBinaryAdapter.InternalUtils.TryCreateCustomWithDeps<T>(out var custom_with_deps_adapter))
        {
            Default = custom_with_deps_adapter;
            return;
        }

        if (UniversalBinaryAdapter.InternalUtils.TryCreateCustom<T>(out var custom_adapter))
        {
            Default = custom_adapter;
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

        if (UniversalBinaryAdapter.InternalUtils.TryCreateArray<T>(out var array_adapter))
        {
            Default = array_adapter;
            return;
        }

        if (UniversalBinaryAdapter.InternalUtils.TryCreateAuto<T>(out var auto_adapter))
        {
            Default = auto_adapter;
            return;
        }

        if (UniversalBinaryAdapter.InternalUtils.TryCreateForUnmanaged<T>(out var unmanaged_adapter))
        {
            Default = unmanaged_adapter;
            return;
        }

    }

    /// <summary>
    /// </summary>
    public static event Action? DefaultChanged = null;
    /// <summary>
    /// Default adapter for type T, or null if none is registered. Can be overriden by a custom implementation for the whole executable
    /// </summary>
    public static UniversalBinaryAdapter<T>? Default
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            DefaultChanged?.Invoke();
        }
    }
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
    public static implicit operator UniversalBinaryAdapter<T>((SaverDelegate saver, LoaderDelegate loader) lambda) => new UniversalBinaryAdapter.Lambda<T> { Saver = lambda.saver, Loader = lambda.loader };

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
