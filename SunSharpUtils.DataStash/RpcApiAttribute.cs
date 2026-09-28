using System;

namespace SunSharpUtils.DataStash;

/// <summary>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class RpcApiAttribute : Attribute
{
    /// <summary>
    /// </summary>
    public Int32 TriesLimit { get; init; } = -1;
    /// <summary>
    /// Do not return from the method until the streaming is finished
    /// <para/>
    /// Cannot be set to true for non-streaming methods
    /// </summary>
    public Boolean BlockWhileStreaming { get; init; } = false;
}
