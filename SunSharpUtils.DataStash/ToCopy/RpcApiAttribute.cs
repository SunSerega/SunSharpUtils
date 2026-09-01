using System;

namespace SunSharpUtils.DataStash;

/// <summary>
/// 
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
internal class RpcApiAttribute(/*Boolean is_streamed*/) : Attribute
{
    //public Boolean IsStreamed { get; } = is_streamed;
}
