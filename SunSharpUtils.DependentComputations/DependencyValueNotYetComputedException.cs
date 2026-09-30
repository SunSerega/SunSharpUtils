using System;

namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Exception thrown from <see cref="DependentValueGroup.ValueComputingContext.Use{TDep}(SunSharpUtils.DependentComputations.DependentValueSource{TDep}, SunSharpUtils.DependentComputations.EDependencyValueMissingRequirementStrategy?)"/>
/// when the dependent value has not been computed yet, and the missing requirement strategy is <see cref="EDependencyValueMissingRequirementStrategy.Throw"/>
/// </summary>
/// <param name="dep_value"></param>
public sealed class DependencyValueNotYetComputedException(IDependentValue dep_value)
    : Exception($"Dependent value {dep_value} was not computed yet. Avoid catching this exception in compute function, proper handling is right outside of it"), IDepValHandleableException
{
    /// <summary>
    /// The dependent value that has not been computed yet
    /// </summary>
    public IDependentValue Dep { get; } = dep_value;
}
