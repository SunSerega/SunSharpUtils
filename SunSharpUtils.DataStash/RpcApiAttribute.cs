using System;

namespace SunSharpUtils.DataStash;

/// <summary>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class RpcApiAttribute : Attribute
{
    /// <summary>
    /// Ignored errors will not count towards the <see cref="RpcApiBlockingAttribute.TriesLimit"/> or <see cref="RpcApiDeferredAttribute.MinInitialConnectErrorCountBeforeReport"/>
    /// <para/>
    /// Default: true
    /// </summary>
    public Boolean IgnoreErrorsWhenServerNotPingable { get; init; } = true;
}

/// <summary>
/// Methods with this attribute will block until the server has finished sending all the data
/// <para/>
/// Assumed for methods that do not use <see cref="RpcEnumerable{T}"/>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class RpcApiBlockingAttribute : Attribute
{
    /// <summary>
    /// -1 = infinite (default)
    /// <para/>
    /// Otherwise should be >=1
    /// </summary>
    public Int32 TriesLimit { get; init; } = -1;
}

/// <summary>
/// Methods with this attribute will start a new thread for comms and return immediately
/// <para/>
/// Not valid for:<br/>
/// - Methods with non-void return type<br/>
/// - Methods that do not use <see cref="RpcEnumerable{T}"/>
/// </summary>
[AttributeUsage(AttributeTargets.Method, AllowMultiple = false, Inherited = false)]
public class RpcApiDeferredAttribute : Attribute
{
    /// <summary>
    /// Must be >=1
    /// <para/>
    /// If 1, the errors will be reported immediately
    /// <para/>
    /// The errors during streaming (after the initial connection) will be reported regardless
    /// <para/>
    /// Retries will continue regardless, until the cancel token throws
    /// </summary>
    public required Int32 MinInitialConnectErrorCountBeforeReport { get; init; }
}
