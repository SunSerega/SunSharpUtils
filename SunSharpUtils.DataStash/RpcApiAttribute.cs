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
}
