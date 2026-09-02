using System;
using System.Text;

namespace SunSharpUtils.DataStash.Generators;

internal static class GenConstants
{
    public static readonly Encoding Encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);
    public static readonly String ServerCommandEnumName = $"{nameof(RpcApiUtils)}.{nameof(RpcApiUtils.EServerCommand)}";
    public static readonly String ClientConnectorClassName = $"{nameof(RpcApiUtils)}.{nameof(RpcApiUtils.ClientConnector)}";
}
