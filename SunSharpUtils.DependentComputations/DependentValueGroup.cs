using System;
using System.Collections.Generic;
using System.Linq;

using SunSharpUtils.Ext.Linq;

namespace SunSharpUtils.DependentComputations;

/// <summary>
/// Represents a group of <see cref="DependentValue{T}"/>-s
/// <para/>
/// <see cref="IsUpdating"/> (default: false) controls whether the values in this group are immediately recomputed when their dependencies change
/// <para/>
/// You can also <see cref="RecomputeChangesOnce()"/> to batch-recompute all values that had received dependency updates
/// </summary>
/// <param name="name">Only used for <see cref="ToString()"/>, for debugging and logging purposes</param>
public sealed class DependentValueGroup(String name)
{
    private readonly HashSet<IDependentValue> pending_update = [];

    /// <summary>
    /// Used only for <see cref="ToString"/>, for debugging and logging purposes
    /// </summary>
    public String Name { get; } = name;

    /// <summary>
    /// </summary>
    public required EDependencyValueMissingRequirementStrategy DefaultMissingRequirementStrategy { get; set; }
    /// <summary>
    /// </summary>
    public required EDependentValueNextRequirementsStrategy DefaultNextRequirementsStrategy { get; set; }

    /// <summary>
    /// </summary>
    public delegate T ComputeFunc<T>(ValueComputingContext context);

    /// <summary>
    /// Adds a new <see cref="DependentValue{T}"/> to this group
    /// <para/>
    /// Compute function is called both for actual computation and also, before that, for collecting the list of direct dependencies
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="name">Only used for <see cref="DependentValue{T}.ToString()"/>, for debugging and logging purposes</param>
    /// <param name="compute_func"></param>
    /// <param name="initial_requirements">Compute function will not be called until all of these values have been computed</param>
    /// <param name="on_updated">Use this if <see cref="IsUpdating"/> is true, but you want to capture the initial value. Otherwise prefer <see cref="DependentValue{T}.OnUpdated"/></param>
    /// <param name="equality_comparer"></param>
    /// <returns></returns>
    public DependentValue<T> AddValue<T>(String name, ComputeFunc<T> compute_func, IDependentValue?[] initial_requirements, Action<T>? on_updated = null, IEqualityComparer<T>? equality_comparer = null)
    {
        var value = new DependentValue<T>(this, name, compute_func, equality_comparer ?? EqualityComparer<T>.Default, initial_requirements.ToArrayWhere(r => r is not null)!);
        value.OnUpdated += on_updated;
        if (this.IsUpdating)
        {
            var context = new GroupComputingContext(this);
            context.MarkForRecompute(value);
            context.ComputeAll();
        }
        else
        {
            this.MarkForRecompute(value);
        }
        return value;
    }

    /// <summary>
    /// Recomputes all pending changes
    /// <para/>
    /// Does nothing if <see cref="IsUpdating"/> is true
    /// </summary>
    public void RecomputeChangesOnce() => this.RecomputeChangesOnce(expect_pending: false);

    /// <summary>
    /// If true, all values in this group are recomputed immediately when their dependencies change
    /// <para/>
    /// When set to true, calls <see cref="RecomputeChangesOnce()"/>
    /// </summary>
    public Boolean IsUpdating
    {
        get;
        set
        {
            if (field == value)
                return;
            field = value;
            if (value)
                this.RecomputeChangesOnce(expect_pending: true);
        }
    }

    internal void RecomputeChangesOnce(Boolean expect_pending)
    {
        if (!expect_pending && this.IsUpdating && this.pending_update.Count != 0)
            throw new InvalidOperationException($"{nameof(DependentValueGroup)}.{nameof(RecomputeChangesOnce)}: Found pending changes despite {nameof(this.IsUpdating)} being true. Probably a race condition");
        var context = new GroupComputingContext(this);
        foreach (var v in this.pending_update)
            context.MarkForRecompute(v);
        this.pending_update.Clear();
        context.ComputeAll();
    }

    internal void MarkForRecompute(IDependentValue v) =>
        this.pending_update.Add(v);

    /// <summary>
    /// Passed to the compute function of a <see cref="DependentValue{T}"/> to collect and check the dependencies
    /// </summary>
    public sealed class ValueComputingContext
    {
        private readonly HashSet<DependentValueSourceBase> direct_sources = [];
        private Boolean is_missing_dependencies = false;
        private Boolean is_sealed = false;

        internal ValueComputingContext() { }

        /// <summary>
        /// Adds a dependency without a value, that could be triggered manually
        /// </summary>
        public void Use(DependentValueSourceManualUpdate source)
        {
            if (this.is_sealed)
                throw new InvalidOperationException($"{nameof(ValueComputingContext)}.Use can only be called from compute function");
            this.direct_sources.Add(source);
        }

        /// <summary>
        /// Adds a dependency with a value, returns its current value
        /// <para/>
        /// If the value is not yet computed, either throws an exception or returns a default value,
        /// depending on the <paramref name="on_missing"/> strategy (defaulting to value and then group's strategy if null)
        /// </summary>
        public TDep Use<TDep>(DependentValueSource<TDep> source, EDependencyValueMissingRequirementStrategy? on_missing = null)
        {
            if (this.is_sealed)
                throw new InvalidOperationException($"{nameof(ValueComputingContext)}.Use can only be called from compute function");
            this.direct_sources.Add(source);
            return source.GetValueOrThrow(on_missing, ref this.is_missing_dependencies);
        }

        /// <summary>
        /// Use this to optimize the compute function by not running some code when some dependencies are already missing
        /// </summary>
        public Boolean IsMissingDependencies => this.is_missing_dependencies;

        internal DependentValueSourceBase[] Seal()
        {
            this.is_sealed = true;
            return this.direct_sources.ToArray();
        }

    }

    internal sealed class GroupComputingContext(DependentValueGroup? group)
    {
        private readonly DependentValueGroup? group = group;
        private readonly HashSet<IDependentValue> need_update = [];

        internal Boolean IsInGroup(IDependentValue v) => v.Group == this.group;

        internal void MarkForRecompute(IDependentValue v) => this.need_update.Add(v);

        internal void ComputeAll()
        {
            var errors = new List<Exception>();

            while (this.need_update.Count != 0)
            {
                TryCompute(this.need_update.First());

                void TryCompute(IDependentValue starting_v)
                {
                    var dep_chain = new Stack<IDependentValue>();
                    dep_chain.Push(starting_v);

                    void ReportNotComputable() =>
                        dep_chain.ForEach(v => this.need_update.Remove(v));

                    while (dep_chain.TryPop(out var v))
                    {
                        if (!Err<DependenctValueComputeFuncThrewException>.TryCatch(() => v.Recompute(this), out var missing_dep, out var ex))
                        {
                            dep_chain.Push(v);
                            errors.Add(ex);
                            dep_chain.Skip(1).ForEach(v => v.SetComputeException(ex));
                            ReportNotComputable();
                            return;
                        }
                        if (missing_dep is not null)
                        {
                            dep_chain.Push(v);
                            if (!this.IsInGroup(missing_dep) && !missing_dep.Group.IsUpdating)
                            {
                                ReportNotComputable();
                                return;
                            }
                            if (dep_chain.Contains(missing_dep))

                            {
                                errors.Add(new MessageException($"Circular dependency detected: {dep_chain.JoinToString(" => ")}"));
                                ReportNotComputable();
                                return;
                            }
                            dep_chain.Push(missing_dep);
                            continue;
                        }
                        // Could be not removed when v is from another group
                        this.need_update.Remove(v);
                        break;
                    }
                }
            }

            if (errors.Count != 0)
                throw new AggregateException($"Found {errors.Count} errors while computing dependent value updates", errors);
        }
    }

    /// <summary>
    /// </summary>
    public override String ToString() =>
        $"{nameof(DependentValueGroup)}({this.Name})";

}
