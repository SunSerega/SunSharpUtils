using System;
using System.IO;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace SunSharpUtils.Ext.Json;

/// <summary>
/// </summary>
public static class JsonExt
{

    /// <summary>
    /// Writes the JToken to a StringWriter and returns the resulting string
    /// </summary>
    /// <param name="token"></param>
    /// <returns></returns>
    public static String WriteToString(this JToken token)
    {
        var sw = new StringWriter();
        token.WriteTo(new JsonTextWriter(sw));
        return sw.ToString();
    }

}
