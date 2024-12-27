using System;
using System.Linq;
using System.Text;

namespace SunSharpUtils.Settings;

/// <summary>
/// </summary>
public static class StringSaver
{

    /// <summary>
    /// </summary>
    public static class Utils
    {

        /// <summary>
        /// </summary>
        public static bool HasNewlines(string? value) => value is not null && "\n\r".Any(value.Contains);

        /// <summary>
        /// Escapes null, newline characters, and backslashes, accounting for newlines
        /// </summary>
        public static string EscapeMultiLine(string? value)
        {
            if (value is null)
                return @"\";
            return value.Replace(@"\", @"\\").Replace("\n", @"\n").Replace("\r", @"\r");
        }

        /// <summary>
        /// Reverses the effect of <see cref="EscapeMultiLine"/>
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        /// <exception cref="FormatException"></exception>
        public static string? UnescapeMultiLine(string value)
        {
            var sb = new StringBuilder(value.Length);
            var escaped = false;
            foreach (var ch in value)
            {
                if (escaped)
                {
                    sb.Append(ch switch
                    {
                        '\\' => '\\',
                        'n' => '\n',
                        'r' => '\r',
                        _ => throw new FormatException($"Invalid escape sequence: \\{ch}")
                    });
                    escaped = false;
                    continue;
                }
                if (ch == '\\')
                {
                    escaped = true;
                    continue;
                }
                sb.Append(ch);
            }
            if (escaped)
            {
                if (sb.Length == 0)
                    return null;
                throw new FormatException("Trailing backslash in escaped string");
            }
            return sb.ToString();
        }

        /// <summary>
        /// Escapes null and backslashes, throws on newlines
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        /// <exception cref="FormatException"></exception>
        public static string EscapeSingleLine(string? value)
        {
            if (HasNewlines(value))
                throw new FormatException("Newline characters were not expected");
            if (value?.All(ch => ch=='\\')??false)
                value += @"\";
            return value??"";
        }
        /// <summary>
        /// Reverses the effect of <see cref="EscapeSingleLine"/>
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        public static string? UnescapeSingleLine(string value)
        {
            if (value == "")
                return null;
            if (value.All(ch => ch=='\\'))
                value = value[1..];
            return value;
        }

    }

    /// <summary>
    /// </summary>
    public static SettingsFieldSaver<string?> MultiLine { get; } = (Utils.EscapeMultiLine, Utils.UnescapeMultiLine);

    /// <summary>
    /// </summary>
    public static SettingsFieldSaver<string?> SingleLine { get; } = (Utils.EscapeSingleLine, Utils.UnescapeSingleLine);

}
