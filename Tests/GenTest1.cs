using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
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

        public readonly struct AddA
        {
            public required FileData.A Content { get; init; }
        }
        public readonly struct CloseA
        {
            public required String Id { get; init; }
        }

        public readonly struct AddB
        {
            public required String ParentId { get; init; }
            public readonly FileData.B Content { get; init; }
        }

        public readonly struct AddC
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

    public sealed partial class TypedContent : ITypedContent<TypedContent, TypedResaveContext>
        , ITypedContentWithRootBlock<NetworkData.AddA, FileData.A, TypedContent.A>, ITypedContentWithCloseableBlock<NetworkData.CloseA, TypedContent.A>
        , ITypedContentWithChildBlock<NetworkData.AddB, FileData.B, TypedContent.A, TypedContent.B>
        , ITypedContentWithChildBlock<NetworkData.AddC, FileData.C, TypedContent.A?, TypedContent.C>
    {
        private Dictionary<BlockLocation, A> LocationToA { get; } = [];
        private Dictionary<String, A> AllA { get; } = [];
        private Dictionary<String, C> GlobalC { get; } = [];
        private List<IGlobalContentModel> OrderedChildren { get; } = [];

        //TODO There is a bunch of generatable boilerplate still here
        // - But for these methods, I'm not sure if I might want a custom implementation at some point
        // - Need to first implement this for Kate and VRCT, to see an example of less test-y usage

        public Boolean TryGetOpenModel(NetworkData.CloseA data, [MaybeNullWhen(false)] out A result) =>
            this.AllA.TryGetValue(data.Id, out result);
        public Boolean TryGetParent(NetworkData.AddB data, [MaybeNullWhen(false)] out A result) =>
            this.AllA.TryGetValue(data.ParentId, out result);
        public Boolean TryGetParent(NetworkData.AddC data, [MaybeNullWhen(false)] out A? result)
        {
            if (data.ParentId is null)
            {
                result = null;
                return false;
            }
            return this.AllA.TryGetValue(data.ParentId, out result);
        }

        public Boolean TryGetModel(BlockLocation location, [MaybeNullWhen(false)] out A model) =>
            this.LocationToA.TryGetValue(location, out model);

        public static FileData.A ParseNetworkPacket(NetworkData.AddA data) => data.Content;
        public static FileData.B ParseNetworkPacket(NetworkData.AddB data) => data.Content;
        public static FileData.C ParseNetworkPacket(NetworkData.AddC data) => data.Content;

        private void AddA(A a)
        {
            this.LocationToA.Add(a.CommonInfo.Location, a);
            this.AllA.Add(a.Id, a);
            this.OrderedChildren.Add(a);
        }
        public void AddC(C c)
        {
            this.GlobalC.Add(c.Id, c);
            this.OrderedChildren.Add(c);
        }

        public A ReadBlock(CommonTypedModelInfo common_info, FileData.A content)
        {
            var res = new A
            {
                CommonInfo = common_info,
                Id = content.Id,
                X = content.X,
            };
            this.AddA(res);
            return res;
        }

        public B ReadBlock(CommonTypedModelInfo common_info, A parent, FileData.B content)
        {
            var res = new B
            {
                CommonInfo = common_info,
                Parent = parent,
                Id = content.Id,
                X = content.X,
            };
            parent.AddB(res);
            return res;
        }

        public C ReadBlock(CommonTypedModelInfo common_info, A? parent, FileData.C content)
        {
            var res = new C
            {
                CommonInfo = common_info,
                Parent = parent,
                Id = content.Id,
                X = content.X,
            };
            if (parent is { })
                parent.AddC(res);
            else
                this.AddC(res);
            return res;
        }

        public void Resave(TypedResaveContext context)
        {
            foreach (var child in this.OrderedChildren)
                child.ResaveTo(context);
        }

        public static void ValidateEqual(TypedContent content1, TypedContent content2)
        {
            ITypedContent<TypedContent>.ValidateDictEqual("AllA", content1.AllA, content2.AllA, A.ValidateEqual);
            ITypedContent<TypedContent>.ValidateDictEqual("GlobalC", content1.GlobalC, content2.GlobalC, C.ValidateEqual);
        }

        private interface IGlobalContentModel
        {
            public void ResaveTo(TypedResaveContext context);
        }
        private interface IAContentModel
        {
            public void ResaveTo(TypedResaveContext_A context);
        }

        public sealed partial class A : ITypedModel<FileData.A>, IGlobalContentModel
        {
            public required String Id { get; init; }
            public required UInt32 X { get; init; }
            private Dictionary<String, B> AllB { get; } = [];
            private Dictionary<String, C> AllC { get; } = [];
            private List<IAContentModel> OrderedChildren { get; } = [];

            public void AddB(B b)
            {
                this.AllB.Add(b.Id, b);
                this.OrderedChildren.Add(b);
            }
            public void AddC(C c)
            {
                this.AllC.Add(c.Id, c);
                this.OrderedChildren.Add(c);
            }

            public FileData.A ConvertToFileData() => new()
            {
                Id = this.Id,
                X = this.X,
            };

            public void ResaveTo(TypedResaveContext context) => context.AddBlock(this, context =>
            {
                foreach (var child in this.OrderedChildren)
                    child.ResaveTo(context);
            });

            public static void ValidateEqual(String path_description, A a1, A a2)
            {
                if (a1.X != a2.X)
                    throw new InvalidOperationException($"{path_description}: {a1.X} vs {a2.X}");
                ITypedContent<TypedContent>.ValidateDictEqual($"{path_description} => AllB", a1.AllB, a2.AllB, B.ValidateEqual);
                ITypedContent<TypedContent>.ValidateDictEqual($"{path_description} => AllC", a1.AllC, a2.AllC, C.ValidateEqual);
            }

            public override String ToString() => $"{nameof(A)}[{this.Id}]";
        }
        public sealed partial class B : ITypedModel<FileData.B>, IAContentModel
        {
            public required String Id { get; init; }
            public required UInt64 X { get; init; }

            public FileData.B ConvertToFileData() => new()
            {
                Id = this.Id,
                X = this.X,
            };

            public void ResaveTo(TypedResaveContext_A context) => context.AddBlock(this);

            public static void ValidateEqual(String path_description, B b1, B b2)
            {
                if (b1.X != b2.X)
                    throw new InvalidOperationException($"{path_description}: {b1.X} vs {b2.X}");
            }

            public override String ToString() => $"{nameof(B)}[{this.Id}]";
        }
        public sealed partial class C : ITypedModel<FileData.C>, IGlobalContentModel, IAContentModel
        {
            public required String Id { get; init; }
            public required UInt64 X { get; init; }

            public FileData.C ConvertToFileData() => new()
            {
                Id = this.Id,
                X = this.X,
            };

            public void ResaveTo(TypedResaveContext context) => context.AddBlock(this);
            public void ResaveTo(TypedResaveContext_A context) => context.AddBlock(this);

            public static void ValidateEqual(String path_description, C c1, C c2)
            {
                if (c1.X != c2.X)
                    throw new InvalidOperationException($"{path_description}: {c1.X} vs {c2.X}");
            }

            public override String ToString() => $"{nameof(C)}[{this.Id}]";
        }

    }

}

