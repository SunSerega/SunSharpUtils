using System;
using System.Collections.Generic;

namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Represents a dependency that can be added to a <see cref="DependentValue{T}"/> and resolves to a value of type <typeparamref name="T"/>
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="name">Only used for <see cref="ToString()"/>, for debugging and logging purposes</param>
/// <param name="equality_comparer"></param>
public sealed class DependentValueSourceStatic<T>(String name, IEqualityComparer<T>? equality_comparer = null) : DependentValueSource<T>
{
    private readonly IEqualityComparer<T> equality_comparer = equality_comparer ?? EqualityComparer<T>.Default;

    /// <summary>
    /// Used only for <see cref="ToString"/>, for debugging and logging purposes
    /// </summary>
    public String Name { get; } = name;

    /// <summary>
    /// </summary>
    public required T Value
    {
        get;
        set
        {
            if (this.equality_comparer.Equals(field, value))
                return;
            field = value;
            this.RecomputeDeps();
        }
    }

    internal override T GetValueOrThrow(EDependencyValueMissingRequirementStrategy? on_missing, ref Boolean is_missing_dependencies) => this.Value;

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(DependentValueSourceStatic<>)}<{typeof(T).Name}>({this.Name})";

}
