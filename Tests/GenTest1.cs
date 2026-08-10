using System;
using System.Collections.Generic;
using System.Text;

namespace SunSharpUtils.DataStash;

internal partial class GenTest1
{

    public GenTest1()
    {
        this.Method1();
    }
    
    [DummyGen]
    private partial void Method1();

}
