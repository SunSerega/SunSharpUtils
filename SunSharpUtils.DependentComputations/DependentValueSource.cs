using System;
using System.Collections.Generic;

namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Base class for all types that can be a dependency for <see cref="DependentValue{T}"/>
/// </summary>
public abstract class DependentValueSourceBase
{
    private readonly List<IDependentValue> deps = [];

    internal void AddDep(IDependentValue dep)
    {
        if (this.deps.Contains(dep))
            throw new InvalidOperationException($"Dependent value {dep} is already in the source's deps list");
        this.deps.Add(dep);
    }

    internal void RemoveDep(IDependentValue dep)
    {
        if (!this.deps.Remove(dep))
            throw new InvalidOperationException($"Dependent value {dep} was not found in the source's deps list");
    }

    private protected void InvalidateDeps(DependentValueGroup.GroupComputingContext context)
    {
        foreach (var dep in this.deps.ToArray())
            dep.Invalidate(context);
        if (this.deps.Count != 0)
            throw new InvalidOperationException($"Some dependent values did not remove themselves from the source's deps list");
    }

    private protected void RecomputeDeps()
    {
        var context = new DependentValueGroup.GroupComputingContext(null);
        this.InvalidateDeps(context);
        context.ComputeAll();
    }

}

/// <summary>
/// Base class for all types that can be a dependency for <see cref="DependentValue{T}"/> and resolve to a value of type <typeparamref name="T"/>
/// </summary>
public abstract class DependentValueSource<T> : DependentValueSourceBase
{
    internal abstract T GetValueOrThrow(EDependencyValueMissingRequirementStrategy? on_missing, ref Boolean is_missing_dependencies);
}
