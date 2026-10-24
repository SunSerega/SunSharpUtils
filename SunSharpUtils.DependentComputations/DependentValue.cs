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
    internal Boolean IsValueMissing { get; }
    internal IDependentValue? TryRecompute(DependentValueGroup.GroupComputingContext context);
    internal void SetComputeException(Exception ex);
    internal void MarkInvalidated();
    internal void MarkValidated();
    internal void RequestRecompute(DependentValueGroup.GroupComputingContext context);
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

    private readonly List<DependentValueSourceBase> current_direct_sources = []; // NOTE: Only used for resetting is_value_missing/last_ex
    private IDependentValue[] next_compute_requirements;
    private Boolean is_invalidated = true; // NOTE: Can be invalidated but not missing value, if dependency didn't compute yet (incl. if it failed to compute)
    private Boolean is_value_missing = true;
    private Exception? last_ex = null; // NOTE: Stays assigned until dependency changes
    private ValueTuple<T>? last_success_result = null; // NOTE: Can be not-null even if is_value_missing. Used for checking if value change event should fire

    internal DependentValue(DependentValueGroup group, String name, DependentValueGroup.ComputeFunc<T> compute_func, IEqualityComparer<T> equality_comparer, IDependentValue[] initial_requirements)
    {
        this.group = group;
        this.Name = name;
        this.compute_func = compute_func;
        this.equality_comparer = equality_comparer;
        this.next_compute_requirements = initial_requirements;
    }

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

    /// <summary>
    /// Used only for <see cref="ToString"/>, for debugging and logging purposes
    /// </summary>
    public String Name { get; }

    /// <inheritdoc/>
    public override T CurrentValueOrThrow => this.CheckCurrentValue(
        on_value: v => v,
        on_failed: ex => throw new DependenctValueComputeFuncThrewException(this, ex),
        on_not_computed_yet: () => throw new InvalidOperationException($"{this} has not been computed yet. {nameof(DependentValueGroup.IsUpdating)} is not set either in {this.group} or group of one of the (maybe indirect) depencies")
    );

    /// <summary>
    /// Generally should not be used in compute functions, because this circumvents the dependency tracking
    /// </summary>
    /// <typeparam name="TResult"></typeparam>
    /// <param name="on_value"></param>
    /// <param name="on_failed"></param>
    /// <param name="on_not_computed_yet"></param>
    /// <returns></returns>
    public TResult CheckCurrentValue<TResult>(Func<T, TResult> on_value, Func<Exception, TResult> on_failed, Func<TResult> on_not_computed_yet)
    {
        using var lock_scope = this.group.Lock.EnterScope();
        if (this.last_ex is not null)
            return on_failed(this.last_ex);
        if (this.is_invalidated)
            return on_not_computed_yet();
        var result = this.last_success_result ?? throw null!;
        return on_value(result.Item1);
    }

    DependentValueGroup IDependentValue.Group => this.group;

    Boolean IDependentValue.IsValueMissing => this.is_value_missing;

    IDependentValue? IDependentValue.TryRecompute(DependentValueGroup.GroupComputingContext group_context)
    {
        if (!this.is_value_missing)
            return null;
        if (this.last_ex is not null)
            throw new DependenctValueComputeFuncThrewException(this, this.last_ex);
        if (this.current_direct_sources.Count != 0)
            throw new InvalidOperationException($"{this} is in invalid state: recompute is required, but {nameof(this.current_direct_sources)} was not cleared: {this.current_direct_sources.JoinToString()}");

        foreach (var req in this.next_compute_requirements)
        {
            if (!req.IsValueMissing)
                continue;
            return req;
        }

        var value_context = new DependentValueGroup.ValueComputingContext();
        var caught_ex = !Err.TryCatch(() => this.compute_func.Invoke(value_context), out var result, out var ex);

        if (caught_ex && ex is DependencyValueNotYetComputedException dep_ex)
            return dep_ex.Dep;

        var direct_sources = value_context.Seal();
        this.next_compute_requirements = (this.NextRequirementsStrategy ?? this.group.DefaultNextRequirementsStrategy) switch
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

        if (value_context.IsMissingDependencies)
        {
            return direct_sources.OfType<IDependentValue>().First(dep => dep.IsValueMissing);
        }
        else
        {
            foreach (var source in direct_sources)
            {
                source.AddDep(this);
                this.current_direct_sources.Add(source);
            }
        }

        if (caught_ex)
        {
            this.last_ex = ex;
            throw new DependenctValueComputeFuncThrewException(this, ex!);
        }
        else
        {
            if (this.last_success_result is not { Item1: var last_result_value } || !this.equality_comparer.Equals(last_result_value, result))
            {
                this.TriggerOnUpdated(result!);
                this.RequestDepsRecompute(group_context);
            }
            else
            {
                this.MarkDepsValidated();
            }

            this.is_value_missing = false;
            this.is_invalidated = false;
            this.last_success_result = new(result!);
        }

        if (value_context.IsMissingDependencies)
        {
            this.next_compute_requirements = value_context.Seal().OfType<IDependentValue>().ToArray();
            return this.next_compute_requirements.First(dep => dep.IsValueMissing);
        }

        return null;
    }

    void IDependentValue.SetComputeException(Exception ex)
    {
        if (!this.is_value_missing)
            throw new InvalidOperationException($"Cannot set exception for {this}: it is not pending");
        this.last_ex = ex;
    }

    void IDependentValue.MarkInvalidated()
    {
        if (this.is_invalidated)
            return;
        this.is_invalidated = true;
        this.MarkDepsInvalidated();
    }

    void IDependentValue.MarkValidated()
    {
        if (!this.is_invalidated)
            return;
        this.is_invalidated = false;
        this.MarkDepsValidated();
    }

    void IDependentValue.RequestRecompute(DependentValueGroup.GroupComputingContext context)
    {
        using var lock_scope = this.group.Lock.EnterScope();
        if (this.last_ex is not null)
            this.last_ex = null;
        else if (!this.is_value_missing)
            this.is_value_missing = true;
        else
            return;

        for (var i = this.current_direct_sources.Count-1; i >= 0; i--)
        {
            var source = this.current_direct_sources[i];
            source.RemoveDep(this);
            this.current_direct_sources.RemoveAt(i);
        }

        if (context.IsInGroup(this) || this.group.IsUpdating)
            context.MarkForRecompute(this);
        else
            this.group.MarkForRecompute(this);

        this.is_invalidated = true;
        this.MarkDepsInvalidated();
    }

    internal override T UseValueOrThrow(EDependencyValueMissingRequirementStrategy? on_missing, ref Boolean is_missing_dependencies)
    {
        if (this.is_value_missing)
        {
            is_missing_dependencies = true;
            return (on_missing ?? this.ValueMissingStrategy ?? this.group.DefaultMissingRequirementStrategy) switch
            {
                EDependencyValueMissingRequirementStrategy.Throw => throw new DependencyValueNotYetComputedException(this),
                EDependencyValueMissingRequirementStrategy.ReturnDefaultValue => default!,
                _ => throw new NotImplementedException($"Unexpected {nameof(EDependencyValueMissingRequirementStrategy)} value: {on_missing}"),
            };
        }
        return this.last_success_result!.Value.Item1;
    }

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(DependentValue<>)}<{typeof(T)}>({this.Name})";

}
