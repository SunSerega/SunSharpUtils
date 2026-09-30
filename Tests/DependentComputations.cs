using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.DependentComputations;

namespace Tests;

[TestClass]
public sealed class DependentComputations
{

    [TestMethod]
    public void Test1()
    {
        var source = new DependentValueSourceStatic<Int32>("source") { Value = 1 };

        var g = new DependentValueGroup("g")
        {
            DefaultMissingRequirementStrategy = EDependencyValueMissingRequirementStrategy.ReturnDefaultValue,
            DefaultNextRequirementsStrategy = EDependentValueNextRequirementsStrategy.SetLast,
        };

        var v_computed = false;
        var v_value = default(Int32?);
        var v = g.AddValue("v", context =>
        {
            var v = context.Use(source);
            v_computed = true;
            return v;
        }, [], on_updated: value => v_value = value);

        source.Value = 2;
        Assert.IsFalse(v_computed, $"Value should not be computed yet");

        g.RecomputeChangesOnce();
        Assert.IsTrue(v_computed, $"Value should be computed after recompute");
        Assert.AreEqual(2, v_value, $"Value should be 2 after recompute");
        v_computed = false;

        g.IsUpdating = true;
        Assert.IsFalse(v_computed, $"Value should not be recomputed yet");

        source.Value = 3;
        Assert.IsTrue(v_computed, $"Value should be recomputed after source change");
        Assert.AreEqual(3, v_value, $"Value should be 3 after source change");

    }

    [TestMethod]
    public void Test2CrossGroupChain()
    {
        var source = new DependentValueSourceStatic<Int32>("source") { Value = 1 };

        var g1 = new DependentValueGroup("g1")
        {
            DefaultMissingRequirementStrategy = EDependencyValueMissingRequirementStrategy.ReturnDefaultValue,
            DefaultNextRequirementsStrategy = EDependentValueNextRequirementsStrategy.SetLast,
        };
        var g2 = new DependentValueGroup("g2")
        {
            DefaultMissingRequirementStrategy = EDependencyValueMissingRequirementStrategy.ReturnDefaultValue,
            DefaultNextRequirementsStrategy = EDependentValueNextRequirementsStrategy.SetLast,
        };

        var v1_computed = false;
        var v1_value = default(Int32?);
        var v1 = g1.AddValue("v1", context =>
        {
            var v = context.Use(source);
            v1_computed = true;
            return v;
        }, [], on_updated: value => v1_value = value);

        var v2_computed = false;
        var v2_value = default(Int32?);
        var v2 = g2.AddValue("v2", context =>
        {
            var v = context.Use(v1);
            v2_computed = true;
            return v;
        }, [], on_updated: value => v2_value = value);

        source.Value = 2;
        Assert.IsFalse(v1_computed, $"v1 should not be computed yet");
        Assert.IsFalse(v2_computed, $"v2 should not be computed yet");

        g1.IsUpdating = true;
        Assert.IsTrue(v1_computed, $"v1 should be computed after g1.IsUpdating=true");
        Assert.IsFalse(v2_computed, $"v2 should not be computed yet");
        Assert.AreEqual(2, v1_value, $"v1 should be 2 after g1.IsUpdating=true");
        v1_computed = false;

        g2.IsUpdating = true;
        Assert.IsFalse(v1_computed, $"v1 should not be recomputed after g2.IsUpdating=true");
        Assert.IsTrue(v2_computed, $"v2 should be computed after g2.IsUpdating=true");
        Assert.AreEqual(2, v2_value, $"v2 should be 2 after g2.IsUpdating=true");
        v2_computed = false;

        source.Value = 3;
        Assert.IsTrue(v1_computed, $"v1 should be recomputed after source change");
        Assert.IsTrue(v2_computed, $"v2 should be recomputed after source change");
        Assert.AreEqual(3, v1_value, $"v1 should be 3 after source change");
        Assert.AreEqual(3, v2_value, $"v2 should be 3 after source change");
        v1_computed = false;
        v2_computed = false;

        g1.IsUpdating = false;
        source.Value = 4;
        Assert.IsFalse(v1_computed, $"v1 should not be recomputed after g1.IsUpdating=false");
        Assert.IsFalse(v2_computed, $"v2 should not be recomputed after g1.IsUpdating=false");

    }

    [TestMethod]
    public void Test3Err()
    {
        var g = new DependentValueGroup("g")
        {
            DefaultMissingRequirementStrategy = EDependencyValueMissingRequirementStrategy.ReturnDefaultValue,
            DefaultNextRequirementsStrategy = EDependentValueNextRequirementsStrategy.SetLast,
            IsUpdating = true,
        };

        var ex = Assert.ThrowsException<AggregateException>(() =>
        {
            g.AddValue<Int32>("v", context =>
            {
                throw new ExpectedException();
            }, []);
        });
        Assert.AreEqual(1, ex.InnerExceptions.Count, $"There should be one inner exception");
        Assert.IsInstanceOfType<ExpectedException>(ex.InnerExceptions[0].InnerException, $"Inner exception should be of type ExpectedException");

    }

    private sealed class ExpectedException : Exception;

}
