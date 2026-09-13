using System;
using System.Collections.Generic;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.Ext.UniversalBin;

namespace Tests.UniversalBin;

internal static class TestUtils
{

    public static void TestValue<T>(T value, Func<T, T, Boolean>? extra_check = null, Action<BinaryWriter>? write_func = null, Func<T, String?>? to_string = null, IEqualityComparer<T>? eq_comp = null)
        where T : notnull
    {
        extra_check ??= (v1, v2) => true;
        write_func ??= bw => bw.WriteData(value);
        to_string ??= v => v.ToString();
        eq_comp ??= EqualityComparer<T>.Default;
        var mem = new MemoryStream();
        var bw = new BinaryWriter(mem);
        write_func.Invoke(bw);
        mem.Position = 0;
        var br = new BinaryReader(mem);
        var result = br.ReadData<T>();
        if (mem.Position != mem.Length || !eq_comp.Equals(value, result) || !extra_check.Invoke(value, result))
            Assert.Fail($"Default type {typeof(T)} didn't serialize/deserialize correctly: ({mem.Position}/{mem.Length} bytes read) {to_string.Invoke(value)} => {to_string.Invoke(result)}");
    }

}
