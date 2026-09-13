using System;
using System.Collections.Generic;
using System.Text;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.Ext.Linq;

namespace Tests.UniversalBin;

[TestClass]
public class Array
{

    [TestMethod]
    public void Test1Array()
    {
        TestArray(1, 2, 3);
        TestArray("a", "b", "c");
        TestArray(DateTime.Now, DateTime.UtcNow);

        static void TestArray<T>(params T[] array) where T : notnull
        {
            var comparer = EqualityComparer<T[]>.Create((a1, a2) =>
            {
                if (ReferenceEquals(a1, a2))
                    return true;
                if (a1 is null || a2 is null)
                    return false;
                if (a1.Length != a2.Length)
                    return false;
                for (var i = 0; i < a1.Length; i++)
                {
                    if (!EqualityComparer<T>.Default.Equals(a1[i], a2[i]))
                        return false;
                }
                return true;
            });
            TestUtils.TestValue(array, to_string: v => $"[{v.JoinToString(", ")}]", eq_comp: comparer);
        }
    }

}
