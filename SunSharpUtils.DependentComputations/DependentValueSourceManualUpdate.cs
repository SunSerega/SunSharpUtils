using System;

namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Represents a dependency that can be added to a <see cref="DependentValue{T}"/> but doesn't resolve to any value itself
/// <para/>
/// Use this for events outside the dependency graph that could invalidate some dependent values
/// </summary>
/// <param name="name">Only used for <see cref="ToString()"/>, for debugging and logging purposes</param>
public sealed class DependentValueSourceManualUpdate(String name) : DependentValueSourceBase
{

    /// <summary>
    /// Used only for <see cref="ToString"/>, for debugging and logging purposes
    /// </summary>
    public String Name { get; } = name;

    /// <summary>
    /// Notifies all dependent values that they should recompute
    /// </summary>
    public void Trigger() => this.RecomputeDeps();

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(DependentValueSourceManualUpdate)}({this.Name})";

}
