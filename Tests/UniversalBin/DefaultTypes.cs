using System;

using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Tests.UniversalBin;

[TestClass]
public class DefaultTypes
{

    [TestMethod]
    public void Test1String()
    {
        TestUtils.TestValue("abc");
    }

    [TestMethod]
    public void Test2DateTime()
    {
        TestUtils.TestValue(DateTime.Now, (v1, v2) => v2.Kind == DateTimeKind.Local);
        TestUtils.TestValue(DateTime.UtcNow, (v1, v2) => v2.Kind == DateTimeKind.Utc);
    }

}
