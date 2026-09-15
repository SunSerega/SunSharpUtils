using System;
using System.IO;

using Microsoft.VisualStudio.TestTools.UnitTesting;

using SunSharpUtils.UniversalBin;

namespace Tests.UniversalBin;

[TestClass]
public class Custom
{

    [TestMethod]
    public void Test1Custom()
    {
        TestUtils.TestValue(new Test1CustomType { A = "abc", B = 123 });
    }

    [TestMethod]
    public void Test2CustomWithDeps()
    {
        TestUtils.TestValue(new Test2CustomType { A = "abc", B = 123 });
    }

    private readonly record struct Test1CustomType : ISerializableData<Test1CustomType>
    {
        public required String A { get; init; }
        public required Int32 B { get; init; }

        public static void Save(BinaryWriter bw, Test1CustomType value)
        {
            bw.Write(value.A);
            bw.Write(value.B);
        }

        public static Test1CustomType Load(BinaryReader br)
        {
            var a = br.ReadString();
            var b = br.ReadInt32();
            return new() { A = a, B = b };
        }

    }

    private readonly record struct Test2CustomType : ISerializableDataWithDeps<Test2CustomType>
    {
        public required String A { get; init; }
        public required Int32 B { get; init; }

        static Func<(UniversalBinaryAdapter<Test2CustomType>.SaverDelegate saver, UniversalBinaryAdapter<Test2CustomType>.LoaderDelegate loader)> ISerializableDataWithDeps<Test2CustomType>.DefineSaverAndLoader(UniversalBinaryAdapter.LambdaWithDeps<Test2CustomType>.InitContext context)
        {
            var get_a_adapter = context.AddDependency<String>();
            var get_b_adapter = context.AddDependency<Int32>();
            return () =>
            {
                var a_adapter = get_a_adapter();
                var b_adapter = get_b_adapter();
                void Save(BinaryWriter bw, Test2CustomType value)
                {
                    a_adapter.Save(bw, value.A);
                    b_adapter.Save(bw, value.B);
                }
                Test2CustomType Load(BinaryReader br)
                {
                    var a = a_adapter.Load(br);
                    var b = b_adapter.Load(br);
                    return new() { A = a, B = b };
                }
                return (Save, Load);
            };
        }

    }

}
