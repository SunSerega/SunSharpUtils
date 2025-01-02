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
    public static SettingsFieldSaver<TField>? Default { get; set; } = null;
    /// <summary>
    /// </summary>
    public static SettingsFieldSaver<TField> DefaultOrThrow => Default ?? throw new InvalidOperationException($"No default saver for {typeof(TField)}");

    /// <summary>
    /// </summary>
    protected abstract string SerializeImpl(TField value);
    /// <summary>
    /// </summary>
    public string Serialize(TField value)
    {
        var res = SerializeImpl(value);
        if (StringSaver.Utils.HasNewlines(res))
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
        if (StringSaver.Utils.HasNewlines(value))
            throw new FormatException("Newline characters are not allowed in setting values");
        return DeserializeImpl(value);
    }
    
    private sealed class Dummy(Func<TField, string> ser, Func<string, TField> deser) : SettingsFieldSaver<TField>
    {
        protected override string SerializeImpl(TField value) => ser(value);
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
        where T : struct, TField, System.Numerics.IBinaryNumber<T>
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
            Default = (Dummy)(object)StringSaver.SingleLine;

        if (typeof(TField) == typeof(bool))
            Default = (Dummy)(object)new SettingsFieldSaver<bool>.Dummy(v => v ? "1" : "0", v => v != "0");

        if (typeof(TField).GetInterfaces().Any(intr => intr.IsGenericType && intr.GetGenericTypeDefinition() == typeof(System.Numerics.IBinaryNumber<>)))
            Default = (SettingsFieldSaver<TField>?)Activator.CreateInstance(typeof(NumberSaver<>).MakeGenericType(typeof(TField), typeof(TField)));

        if (typeof(TField).GetInterfaces().Any(intr => intr.IsGenericType && intr.GetGenericTypeDefinition() == typeof(ISettingsSaveable<>)))
            Default = (SettingsFieldSaver<TField>?)Activator.CreateInstance(typeof(SelfReporterSaver<>).MakeGenericType(typeof(TField), typeof(TField)));
        
    }

}
