namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Defines what to do when this <see cref="DependentValue{T}"/> is used from compute function of another value before its own value has been computed
/// </summary>
public enum EDependencyValueMissingRequirementStrategy
{

    /// <summary>
    /// When this value is used (from compute function of another value) before it is computed, throw <see cref="DependencyValueNotYetComputedException"/>
    /// <para/>
    /// This means dependent compute function will exit ASAP, without computing any bogus values (and maybe adding bogus requirements)
    /// <para/>
    /// But performance cost of throwing exceptions can be rather high
    /// <para/>
    /// Especially because compute function will have to rerun from the begining for every requirement that throws
    /// </summary>
    Throw = 1,

    /// <summary>
    /// When this value is used (from compute function of another value) before it is computed, return default()
    /// <para/>
    /// This means dependent compute function will have to continue computing a bogus value that will not be used
    /// <para/>
    /// If no requirements throw, compute function will collect all (possibly incorrect) requirements in a single run
    /// <para/>
    /// This strategy effectively allows coarse-grained requirement collection before a proper compute function run
    /// </summary>
    ReturnDefaultValue = 2,


}
