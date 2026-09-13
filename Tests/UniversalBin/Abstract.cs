using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.Ext.UniversalBin;

namespace Tests.UniversalBin;

[TestClass]
public class Abstract
{

    [TestMethod]
    public void Test1Interface()
    {
        TestUtils.TestValue((ITest1Interface)new Test1InterfaceDerived1Struct { Value = 1, Value2 = "test" });
        TestUtils.TestValue((ITest1Interface)new Test1InterfaceDerived2Class { Value = 2, Value3 = Math.PI });
    }

    [TestMethod]
    public void Test2Class()
    {
        TestUtils.TestValue((Test2ClassBase)new Test2ClassDerived1 { Value = 1, Value2 = "test" });
        TestUtils.TestValue((Test2ClassBase)new Test2ClassDerived2 { Value = 2, Value3 = Math.PI });
    }

    [TestMethod]
    public void Test3VersionedInterface()
    {
        TestUtils.TestValue((ITest3VersionedInterfaceBase)new Test3VersionedInterfaceDerived1 { Value = 1, Value2 = "test" });
        TestUtils.TestValue((ITest3VersionedInterfaceBase)new Test3VersionedInterfaceDerived2 { Value = 2, Value2 = Math.PI });
        TestUtils.TestValue((ITest3VersionedInterfaceBase)new Test3VersionedInterfaceDerived1 { Value = 3, Value2 = "Upgraded" }, write_func: bw =>
        {
            bw.Write(1); // version
            bw.WriteData(new Test3VersionedInterfaceOld { Value = 3 });
        });
    }

    #region Test1

    [AutoSerializedData]
    [AbstractData(ExpectedImplementations = [typeof(Test1InterfaceDerived1Struct), typeof(Test1InterfaceDerived2Class)])]
    private interface ITest1Interface : IEquatable<ITest1Interface>
    {
        public Int32 Value { get; set; }
    }

    [AutoSerializedData]
    private record struct Test1InterfaceDerived1Struct() : ITest1Interface
    {
        public required Int32 Value;
        public required String Value2;
        Int32 ITest1Interface.Value
        {
            readonly get => this.Value;
            set => this.Value = value;
        }
        readonly Boolean IEquatable<ITest1Interface>.Equals(ITest1Interface? obj) =>
            obj is Test1InterfaceDerived1Struct other && this == other;
    }

    [AutoSerializedData]
    private sealed record class Test1InterfaceDerived2Class() : ITest1Interface
    {
        public required Int32 Value;
        public required Double Value3;
        Int32 ITest1Interface.Value
        {
            get => this.Value;
            set => this.Value = value;
        }
        Boolean IEquatable<ITest1Interface>.Equals(ITest1Interface? obj) =>
            obj is Test1InterfaceDerived2Class other && this == other;
    }

    #endregion

    #region Test2

    [AutoSerializedData]
    [AbstractData(ExpectedImplementations = [typeof(Test2ClassDerived1), typeof(Test2ClassDerived2)])]
    private abstract record class Test2ClassBase
    {
        public required Int32 Value;
    }

    [AutoSerializedData]
    private sealed record class Test2ClassDerived1() : Test2ClassBase
    {
        public required String Value2;
    }

    [AutoSerializedData]
    private sealed record class Test2ClassDerived2() : Test2ClassBase
    {
        public required Double Value3;
    }

    #endregion

    #region Test3

    [AutoSerializedData]
    [VersionedData(Version = 2)]
    [VersionedDataOldVersion(Version = 1, OldVersionDataType = typeof(Test3VersionedInterfaceOld))]
    [AbstractData(ExpectedImplementations = [typeof(Test3VersionedInterfaceDerived1), typeof(Test3VersionedInterfaceDerived2)])]
    private interface ITest3VersionedInterfaceBase : IEquatable<ITest3VersionedInterfaceBase>
    {
        public Int32 Value { get; set; }
    }

    [AutoSerializedData]
    private record struct Test3VersionedInterfaceDerived1() : ITest3VersionedInterfaceBase
    {
        public required Int32 Value;
        public required String Value2;
        Int32 ITest3VersionedInterfaceBase.Value
        {
            readonly get => this.Value;
            set => this.Value = value;
        }
        readonly Boolean IEquatable<ITest3VersionedInterfaceBase>.Equals(ITest3VersionedInterfaceBase? obj) =>
            obj is Test3VersionedInterfaceDerived1 other && this == other;
    }

    [AutoSerializedData]
    private record struct Test3VersionedInterfaceDerived2() : ITest3VersionedInterfaceBase
    {
        public required Int32 Value;
        public required Double Value2;
        Int32 ITest3VersionedInterfaceBase.Value
        {
            readonly get => this.Value;
            set => this.Value = value;
        }
        readonly Boolean IEquatable<ITest3VersionedInterfaceBase>.Equals(ITest3VersionedInterfaceBase? obj) =>
            obj is Test3VersionedInterfaceDerived2 other && this == other;
    }

    [AutoSerializedData]
    private struct Test3VersionedInterfaceOld() : IVersionedDataOldVersion<ITest3VersionedInterfaceBase>
    {
        public required Int32 Value;

        public ITest3VersionedInterfaceBase Upgrade() =>
            new Test3VersionedInterfaceDerived1 { Value = this.Value, Value2 = "Upgraded" };

    }

    #endregion

}
