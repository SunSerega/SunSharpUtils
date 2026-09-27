using System;

//TODO Maybe change the namespace using compiler directives?
#pragma warning disable IDE0079 // Remove unnecessary suppression
#pragma warning disable RS1035 // Do not use APIs banned for analyzers
#pragma warning disable CS0436 // Type conflicts with imported type
#pragma warning restore IDE0079 // Remove unnecessary suppression

namespace SunSharpUtils.DataStash;

/// <summary>
/// </summary>
public sealed class RpcConnectionReturnedErrorException(RpcConnection connection, String error_message) : Exception($"{connection}: Error from remote: {error_message}");
