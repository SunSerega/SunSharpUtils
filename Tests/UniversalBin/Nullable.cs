using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.UniversalBin;

namespace Tests.UniversalBin;

[TestClass]
public class Nullable
{

    [TestMethod]
    public void Test1NullableValue()
    {
        TestUtils.TestValue(new TestValue { Value = 123 });
        TestUtils.TestValue(new TestValue { Value = null });
    }

    [TestMethod]
    public void Test2NullableClass()
    {
        TestUtils.TestValue(new TestClass { Value = "abc" });
        TestUtils.TestValue(new TestClass { Value = null });
    }

    [AutoSerializedData]
    private record struct TestValue()
    {
        public required Int32? Value;
    }

    [AutoSerializedData]
    private record struct TestClass()
    {
        public required String? Value;
    }

}
