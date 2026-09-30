using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Linq;

using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Implemented only by <see cref="DependentValue{T}"/>
/// </summary>
public interface IDependentValue
{
    internal DependentValueGroup Group { get; }
    internal Boolean HasValue { get; }
    internal IDependentValue? Recompute(DependentValueGroup.GroupComputingContext context);
    internal void SetComputeException(Exception ex);
    internal void Invalidate(DependentValueGroup.GroupComputingContext context);
}

/// <summary>
/// Represents a value that depends on arbitrary number of <see cref="DependentValueSourceStatic{T}"/>, <see cref="DependentValueSourceManualUpdate"/> and <see cref="DependentValue{T}"/> instances
/// <para/>
/// Computed lazily when any of the dependencies are updated and <see cref="DependentValueGroup.IsUpdating"/> is true, or when <see cref="DependentValueGroup.RecomputeChangesOnce()"/> is called
/// </summary>
/// <typeparam name="T"></typeparam>
public sealed class DependentValue<T> : DependentValueSource<T>, IDependentValue
{
    private readonly DependentValueGroup group;
    private readonly DependentValueGroup.ComputeFunc<T> compute_func;
    private readonly IEqualityComparer<T> equality_comparer;

    private IDependentValue[] next_compute_requirements;
    private Exception? compute_ex = null;
    private ComputeResult? computed = null;
    private ValueTuple<T>? last_result = null;

    internal DependentValue(DependentValueGroup group, String name, DependentValueGroup.ComputeFunc<T> compute_func, IEqualityComparer<T> equality_comparer, IDependentValue[] initial_requirements)
    {
        this.group = group;
        this.Name = name;
        this.compute_func = compute_func;
        this.equality_comparer = equality_comparer;
        this.next_compute_requirements = initial_requirements;
    }

    /// <summary>
    /// Used only for <see cref="ToString"/>, for debugging and logging purposes
    /// </summary>
    public String Name { get; }

    /// <summary>
    /// Triggered when the value changes after a recompute, with the new value as argument
    /// </summary>
    public event Action<T>? OnUpdated = null;

    /// <summary>
    /// Strategy to use when this value has not been computed yet but is requested in another value's compute function
    /// <para/>
    /// If null, the group's <see cref="DependentValueGroup.DefaultMissingRequirementStrategy"/> is used
    /// </summary>
    public EDependencyValueMissingRequirementStrategy? ValueMissingStrategy { get; set; } = null;

    /// <summary>
    /// Strategy to use for next recompute requirements after this value has been computed
    /// </summary>
    public EDependentValueNextRequirementsStrategy? NextRequirementsStrategy { get; set; } = null;

    DependentValueGroup IDependentValue.Group => this.group;

    Boolean IDependentValue.HasValue => this.computed is not null;

    IDependentValue? IDependentValue.Recompute(DependentValueGroup.GroupComputingContext group_context)
    {
        if (this.computed is not null)
            throw new InvalidOperationException($"{this} is already computed");
        if (this.compute_ex is not null)
            throw new DependenctValueComputeFuncThrewException(this, this.compute_ex);

        foreach (var req in this.next_compute_requirements)
        {
            if (req.HasValue)
                continue;
            return req;
        }

        var value_context = new DependentValueGroup.ValueComputingContext();
        if (!Err.TryCatch(() => this.compute_func.Invoke(value_context), out var result, out var ex) && !value_context.IsMissingDependencies)
        {
            if (ex is DependencyValueNotYetComputedException dep_ex)
                return dep_ex.Dep;
            this.compute_ex = ex;
            throw new DependenctValueComputeFuncThrewException(this, ex);
        }
        if (value_context.IsMissingDependencies)
            return value_context.Seal().OfType<IDependentValue>().First(dep => !dep.HasValue);

        if (this.last_result is not { Item1: var last_result_value } || !this.equality_comparer.Equals(last_result_value, result))
        {
            this.OnUpdated?.Invoke(result!);
            this.InvalidateDeps(group_context);
            this.last_result = new(result!);
        }

        var direct_sources = value_context.Seal();
        foreach (var source in direct_sources)
            source.AddDep(this);
        this.computed = new()
        {
            DirectSources = direct_sources
        };

        this.next_compute_requirements =(this.NextRequirementsStrategy ?? this.group.DefaultNextRequirementsStrategy) switch
        {
            EDependentValueNextRequirementsStrategy.SetEmpty => [],
            EDependentValueNextRequirementsStrategy.SetLast => direct_sources.ToArray((dep, [MaybeNullWhen(false)] out success) =>
            {
                var dep_value = dep as IDependentValue;
                success = dep_value is not null;
                return dep_value!;
            }),
            _ => throw new NotImplementedException($"Unexpected {nameof(EDependentValueNextRequirementsStrategy)} value: {this.NextRequirementsStrategy}"),
        };
        return null;
    }

    void IDependentValue.SetComputeException(Exception ex) => this.compute_ex = ex;

    void IDependentValue.Invalidate(DependentValueGroup.GroupComputingContext context)
    {
        this.compute_ex = null;
        if (this.computed is not { DirectSources: var direct_sources })
            return;
        this.computed = null;

        foreach (var source in direct_sources)
            source.RemoveDep(this);

        if (context.IsInGroup(this) || this.group.IsUpdating)
            context.MarkForRecompute(this);
        else
            this.group.MarkForRecompute(this);

        // Don't invalidate until this is recomputed and the new value is different from the last value
        // - Otherwise we recompute values whose inputs didn't change
        //this.InvalidateDeps(context);
    }

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(DependentValue<>)}<{typeof(T)}>({this.Name})";

    internal override T GetValueOrThrow(EDependencyValueMissingRequirementStrategy? on_missing, ref Boolean is_missing_dependencies)
    {
        if (this.computed is null)
        {
            is_missing_dependencies = true;
            return (on_missing ?? this.ValueMissingStrategy ?? this.group.DefaultMissingRequirementStrategy) switch
            {
                EDependencyValueMissingRequirementStrategy.Throw => throw new DependencyValueNotYetComputedException(this),
                EDependencyValueMissingRequirementStrategy.ReturnDefaultValue => default!,
                _ => throw new NotImplementedException($"Unexpected {nameof(EDependencyValueMissingRequirementStrategy)} value: {on_missing}"),
            };
        }
        return this.last_result!.Value.Item1;
    }

    private readonly struct ComputeResult
    {
        public required DependentValueSourceBase[] DirectSources { get; init; }
    }

}
