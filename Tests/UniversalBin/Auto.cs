using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

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

}
