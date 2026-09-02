using System;

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
