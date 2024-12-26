using System;

using System.Linq;

using System.Text;

namespace SunSharpUtils.Settings;

/// <summary>
/// Defines how a field should be saved in settings
/// </summary>
/// <typeparam name="TField"></typeparam>
public abstract class SettingsFieldSaver<TField>
{
    /// <summary>
    /// </summary>
    public static SettingsFieldSaver<TField>? Default { get; set; }

    /// <summary>
    /// </summary>
    protected abstract string SerializeImpl(TField value);
    /// <summary>
    /// </summary>
    public string Serialize(TField value)
    {
        var res = SerializeImpl(value);
        if ("\n\r".Any(res.Contains))
            throw new FormatException("Newline characters are not allowed in setting values");
        return res;
    }

    /// <summary>
    /// </summary>
    protected abstract TField DeserializeImpl(string value);
    /// <summary>
    /// </summary>
    public TField Deserialize(string value)
    {
        if ("\n\r".Any(value.Contains))
            throw new FormatException("Newline characters are not allowed in setting values");
        return DeserializeImpl(value);
    }

    /// <summary>
    /// </summary>
    protected static class Utils
    {

        /// <summary>
        /// Escapes newline characters and backslashes
        /// </summary>
        public static string Escape(string value)
        {
            return value.Replace(@"\", @"\\").Replace("\n", @"\n").Replace("\r", @"\r");
        }

        /// <summary>
        /// Reverses the effect of <see cref="Escape"/>
        /// </summary>
        /// <param name="value"></param>
        /// <returns></returns>
        /// <exception cref="FormatException"></exception>
        public static string Unescape(string value)
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
            return sb.ToString();
        }

    }

    /// <summary>
    /// Custom saver using delegates
    /// </summary>
    public sealed class Dummy(Func<TField, string> ser, Func<string, TField> deser) : SettingsFieldSaver<TField>
    {
        /// <summary>
        /// </summary>
        protected override string SerializeImpl(TField value) => ser(value);
        /// <summary>
        /// </summary>
        protected override TField DeserializeImpl(string value) => deser(value);
    }
    /// <summary>
    /// Defines a custom saver using delegates and sets it as default
    /// </summary>
    /// <param name="ser"></param>
    /// <param name="deser"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public static SettingsFieldSaver<TField> DefineDefaultDummy(Func<TField, string> ser, Func<string, TField> deser)
    {
        if (Default is not null)
            throw new InvalidOperationException("Default saver already defined");
        return Default = new Dummy(ser, deser);
    }
    /// <summary>
    /// </summary>
    public static implicit operator SettingsFieldSaver<TField>((Func<TField, string> ser, Func<string, TField> deser) t) => new Dummy(t.ser, t.deser);

    private sealed class NumberSaver<T> : SettingsFieldSaver<T>
        where T : TField, System.Numerics.IBinaryNumber<T>
    {
        private static readonly System.Globalization.NumberFormatInfo empty_nfi = new();

        protected override string SerializeImpl(T value) => value.ToString(null, empty_nfi);
        protected override T DeserializeImpl(string value) => T.Parse(value, empty_nfi);

    }

    private sealed class SelfReporterSaver<T> : SettingsFieldSaver<T>
        where T : TField, ISettingsSaveable<T>
    {
        protected override string SerializeImpl(T value) => T.SerializeSetting(value);
        protected override T DeserializeImpl(string value) => T.DeserializeSetting(value);
    }

    static SettingsFieldSaver()
    {

        if (typeof(TField) == typeof(string))
            Default = (Dummy)(object)new SettingsFieldSaver<string>.Dummy(Utils.Escape, Utils.Unescape);

        if (typeof(TField) == typeof(bool))
            Default = (Dummy)(object)new SettingsFieldSaver<bool>.Dummy(v => v ? "1" : "0", v => v != "0");

        if (typeof(TField).GetInterfaces().Any(intr => intr.IsGenericType && intr.GetGenericTypeDefinition() == typeof(System.Numerics.IBinaryNumber<>)))
            Default = (SettingsFieldSaver<TField>?)Activator.CreateInstance(typeof(NumberSaver<>).MakeGenericType(typeof(TField), typeof(TField)));

        if (typeof(TField).GetInterfaces().Any(intr => intr.IsGenericType && intr.GetGenericTypeDefinition() == typeof(ISettingsSaveable<>)))
            Default = (SettingsFieldSaver<TField>?)Activator.CreateInstance(typeof(SelfReporterSaver<>).MakeGenericType(typeof(TField), typeof(TField)));
        
    }

}
