using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.Ext.Bin;

namespace Tests.UniversalBin;

[TestClass]
public class Unmanaged
{

    [TestMethod]
    public void Test1UnmanagedNumbers()
    {
        TestUtils.TestValue((Byte)1);
        TestUtils.TestValue((Int64)1);
        TestUtils.TestValue((Double)1);
    }

    [TestMethod]
    public void Test1UnmanagedNumbersCompatibility()
    {
        TestUtils.TestValue((Byte)1, write_func: bw => bw.Write((Byte)1));
        TestUtils.TestValue((Int64)1, write_func: bw => bw.Write((Int64)1));
        TestUtils.TestValue((Double)1, write_func: bw => bw.Write((Double)1));
    }

    [TestMethod]
    public void Test2UnmanagedEnum()
    {
        TestUtils.TestValue(ConsoleColor.Red);
        TestUtils.TestValue(ConsoleColor.Red, write_func: bw => bw.WriteEnum(ConsoleColor.Red));
        TestUtils.TestValue(ConsoleColor.Red, read_func: br => br.ReadEnum<ConsoleColor>());
    }

    [TestMethod]
    public void Test2UnmanagedEnumCompatibility()
    {
        TestUtils.TestValue(ConsoleColor.Red, write_func: bw => bw.WriteEnum(ConsoleColor.Red));
    }

    [TestMethod]
    public void Test3UnmanagedTuple()
    {
        TestUtils.TestValue((1, 2));
    }

    [TestMethod]
    public void Test4UnmanagedStruct()
    {
        TestUtils.TestValue(new TestStruct { A = 1, B = 2 });
    }

    private readonly record struct TestStruct
    {
        public Int32 A { get; init; }
        public Int32 B { get; init; }
    }

}
