using System;
using System.Collections.Generic;
using System.Threading;

using SunSharpUtils.DataStash;

namespace Tests;

internal static partial class GenTest1
{

    static GenTest1()
    {
        Method2(123);
        Method3();
    }

    [RpcApi]
    private static partial void Method2(Int32 asd);

    [RpcApi]
    private static partial Int32 Method3();

}

[AutoDataStash]
internal sealed partial class ExampleDataStash(String states_dir, CancellationToken svc_stop_token) : DataStash<ExampleDataStash.TypedContent>(states_dir, svc_stop_token)
{

    public static class NetworkData
    {

        public readonly struct A
        {
            public required FileData.A Content { get; init; }
        }

        public readonly struct B
        {
            public required String ParentId { get; init; }
            public readonly FileData.B Content { get; init; }
        }

        public readonly struct C
        {
            //TODO How do I properly support nullable classes in RPC?
            public required String? ParentId { get; init; }
            public readonly FileData.C Content { get; init; }
        }

    }

    public static class FileData
    {

        public readonly struct A
        {
            public required String Id { get; init; }
            public required UInt32 X { get; init; }
        }

        public readonly struct B
        {
            public readonly String Id { get; init; }
            public required UInt64 X { get; init; }
        }

        public readonly struct C
        {
            public readonly String Id { get; init; }
            public required UInt64 X { get; init; }
        }

    }

    //TODO This content is not thread safe
    // - But internals of DataStash (like choosing a block location) are thread-safe
    // - I think I need locks in code-generated implementation
    public sealed class TypedContent : ITypedContent<TypedContent, ResaveContext>
        , ITypedContentWithRootBlock<NetworkData.A, FileData.A, TypedContent.A>
        , ITypedContentWithChildBlock<NetworkData.B, FileData.B, TypedContent.A, TypedContent.B>
        , ITypedContentWithChildBlock<NetworkData.C, FileData.C, TypedContent.A?, TypedContent.C>
    {
        public Dictionary<String, A> AllA { get; } = [];
        public Dictionary<String, C> GlobalC { get; } = [];

        //TODO This is also generatable boilerplate
        // - But would I maybe need a different form of ParseNetworkPacket?
        public FileData.A ParseNetworkPacket(NetworkData.A data) => data.Content;

        public FileData.B ParseNetworkPacket(NetworkData.B data, out A found_parent)
        {
            found_parent = this.AllA[data.ParentId];
            return data.Content;
        }

        public FileData.C ParseNetworkPacket(NetworkData.C data, out A? found_parent)
        {
            found_parent = data.ParentId is null ? null : this.AllA[data.ParentId];
            return data.Content;
        }

        public A ReadBlock(FileData.A content)
        {
            var res = new A(content.Id, content.X);
            this.AllA.Add(content.Id, res);
            return res;
        }

        public B ReadBlock(A parent, FileData.B content)
        {
            var res = new B(content.Id, parent, content.X);
            parent.AllB.Add(content.Id, res);
            return res;
        }

        public C ReadBlock(A? parent, FileData.C content)
        {
            var res = new C(content.Id, parent, content.X); 
            (parent?.AllC ?? this.GlobalC).Add(content.Id, res);
            return res;
        }

        void ITypedContent<TypedContent, ResaveContext>.Resave(ResaveContext context)
        {
            foreach (var a in this.AllA.Values)
            {
                var file_data = new FileData.A
                {
                    Id = a.Id,
                    X = a.X,
                };
                context.AddBlock(file_data, context =>
                {
                    foreach (var b in a.AllB.Values)
                    {
                        var file_data = new FileData.B
                        {
                            Id = b.Id,
                            X = b.X,
                        };
                        context.AddBlock(file_data);
                    }
                    foreach (var c in a.AllC.Values)
                    {
                        var file_data = new FileData.C
                        {
                            Id = c.Id,
                            X = c.X,
                        };
                        context.AddBlock(file_data);
                    }
                });
            }
            foreach (var c in this.GlobalC.Values)
            {
                var file_data = new FileData.C
                {
                    Id = c.Id,
                    X = c.X,
                };
                context.AddBlock(file_data);
            }
        }

        public static void ValidateEqual(TypedContent content1, TypedContent content2)
        {
            ITypedContent<TypedContent>.ValidateDictEqual(content1.AllA, content2.AllA, ValidateA);
            ITypedContent<TypedContent>.ValidateDictEqual(content1.GlobalC, content2.GlobalC, ValidateC);

            static void ValidateA(A a1, A a2)
            {
                if (a1.X != a2.X)
                    throw new InvalidOperationException($"{nameof(A)}[{a1.Id}].X: {a1.X} vs {a2.X}");
                ITypedContent<TypedContent>.ValidateDictEqual(a1.AllB, a2.AllB, ValidateB);
                ITypedContent<TypedContent>.ValidateDictEqual(a1.AllC, a2.AllC, ValidateC);
            }

            static void ValidateB(B b1, B b2)
            {
                if (b1.X != b2.X)
                    throw new InvalidOperationException($"{nameof(A)}[{b1.parent.Id}]=>{nameof(B)}[{b1.Id}].X: {b1.X} vs {b2.X}");
            }

            static void ValidateC(C c1, C c2)
            {
                if (c1.X != c2.X)
                    throw new InvalidOperationException($"{nameof(A)}[{c1.parent?.Id}]=>{nameof(C)}[{c1.Id}].X: {c1.X} vs {c2.X}");
            }
        }

        //TODO Add code-generated DateTime field
        public sealed record class A(String Id, UInt32 X)
        {
            public Dictionary<String, B> AllB { get; } = [];
            public Dictionary<String, C> AllC { get; } = [];
        }
        public sealed record class B(String Id, A parent, UInt64 X);
        public sealed record class C(String Id, A? parent, UInt64 X);

    }

}

