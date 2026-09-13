using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.Ext.Linq;
using SunSharpUtils.Ext.UniversalBin;

namespace Tests.UniversalBin;

[TestClass]
public class Auto
{

    [TestMethod]
    public void Test1AutoType()
    {
        TestUtils.TestValue(new Test1Struct { A = "abc", B = 123 });
    }

    [TestMethod]
    public void Test2VersionedType()
    {
        TestUtils.TestValue(new Test2StructV2 { A = "abc", B = 123 });
    }

    [TestMethod]
    public void Test2VersionedTypeUpgrade()
    {
        TestUtils.TestValue(new Test2StructV2 { A = "abc", B = 0 }, write_func: bw =>
        {
            bw.Write(1); // version
            bw.WriteData(new Test2StructV1 { A = "abc" });
        });
    }

    [TestMethod]
    public void Test3LambdaDependencyMissingException()
    {
        Assert.IsNull(UniversalBinaryAdapter<Test3Nested3>.Default, $"Test3Nested3 should not have a default adapter");
        var ex = Assert.ThrowsException<UniversalBinaryAdapter.LambdaDependencyMissingException>(() =>
        {
            TestUtils.TestValue(new Test3Nested1 { Value = new() { Value = new() { Value = "abc" } } });
        });
        Type[] expected_path = [typeof(Test3Nested1), typeof(Test3Nested2), typeof(Test3Nested3)];
        Assert.IsTrue(expected_path.SequenceEqual(ex.Path), $"Path: {expected_path.JoinToString(" => ")} | {ex.Path.JoinToString(" => ")}");
    }

    [AutoSerializedData]
    private record struct Test1Struct()
    {
        public required String A;
        public required Int32 B;
    }

    [AutoSerializedData]
    [VersionedData(Version = 2)]
    [VersionedDataOldVersion(Version = 1, OldVersionDataType = typeof(Test2StructV1))]
    private record struct Test2StructV2()
    {
        public required String A;
        public required Int32 B;
    }
    [AutoSerializedData]
    private record struct Test2StructV1() : IVersionedDataOldVersion<Test2StructV2>
    {
        public required String A;

        public Test2StructV2 Upgrade() =>
            new() { A = this.A, B = 0 };
    }

    [AutoSerializedData]
    public record struct Test3Nested1()
    {
        public required Test3Nested2 Value;
    }
    [AutoSerializedData]
    public record struct Test3Nested2()
    {
        public required Test3Nested3 Value;
    }
    // No [AutoSerializedData] attribute here, so it will throw a LambdaDependencyMissingException
    public record struct Test3Nested3()
    {
        public required String Value;
    }

}
