using System;
using System.Collections.Generic;
using System.Text;

namespace SunSharpUtils.DataStash;

internal partial class GenTest1
{

    public GenTest1()
    {
        this.Method2();
        this.Method3();
    }

    [RpcApi]
    private partial void Method2();

    [RpcApi]
    private partial void Method3();

}
