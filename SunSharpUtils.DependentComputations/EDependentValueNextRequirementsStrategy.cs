namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Defines what to do with dependencies for next recompute when <see cref="DependentValue{T}"/> has been recomputed
/// </summary>
public enum EDependentValueNextRequirementsStrategy
{

    /// <summary>
    /// When value is computed, set required values for next recompute to empty array
    /// <para/>
    /// This means next recompute would cause compute function to rerun (possibly multiple times)
    /// to rebuild the requirements list from <see cref="DependentValueGroup.ValueComputingContext.Use{TDep}(DependentValueSource{TDep}, EDependencyValueMissingRequirementStrategy?)"/> calls
    /// </summary>
    SetEmpty = 1,

    /// <summary>
    /// When value is computed, set required values for next recompute equal to all dependencies used for this recompute
    /// <para/>
    /// This means next recompute would not run the compute function until all the same requirements have been satisfied,
    /// even if changes in some of them make other requirements irrelevant
    /// </summary>
    SetLast = 2,

}
