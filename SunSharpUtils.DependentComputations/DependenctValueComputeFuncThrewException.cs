using System;

namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Exception thrown when a dependent value's compute function throws an exception, marking that value uncomputable
/// <para/>
/// Will be thrown as of one the exceptions in <see cref="AggregateException"/>
/// when any update to <see cref="DependentValueSourceBase"/> or <see cref="DependentValueGroup.RecomputeChangesOnce()"/> causes this value to be evaluated
/// </summary>
/// <param name="value"></param>
/// <param name="inner_exception"></param>
public class DependenctValueComputeFuncThrewException(IDependentValue value, Exception inner_exception) : Exception($"Compute function of {value} threw an exception", inner_exception), IDepValHandleableException
{
    /// <summary>
    /// </summary>
    public IDependentValue ThrowingValue => value;
}
