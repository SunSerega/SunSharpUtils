using System;

using System.Linq;
using System.Reflection;

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
    protected abstract String SerializeImpl(TField value);
    /// <summary>
    /// </summary>
    public String Serialize(TField value)
    {
        var res = this.SerializeImpl(value);
        if (StringSaver.Utils.HasNewlines(res))
            throw new FormatException("Newline characters are not allowed in setting values");
        return res;
    }

    /// <summary>
    /// </summary>
    protected abstract TField DeserializeImpl(String value);
    /// <summary>
    /// </summary>
    public TField Deserialize(String value)
    {
        if (StringSaver.Utils.HasNewlines(value))
            throw new FormatException("Newline characters are not allowed in setting values");
        return this.DeserializeImpl(value);
    }

    private sealed class LambdaSaver(Func<TField, String> ser, Func<String, TField> deser) : SettingsFieldSaver<TField>
    {
        protected override String SerializeImpl(TField value) => ser(value);
        protected override TField DeserializeImpl(String value) => deser(value);
    }
    /// <summary>
    /// Defines a custom saver using delegates and sets it as default
    /// </summary>
    /// <param name="ser"></param>
    /// <param name="deser"></param>
    /// <exception cref="InvalidOperationException"></exception>
    public static SettingsFieldSaver<TField> DefineDefaultLambda(Func<TField, String> ser, Func<String, TField> deser)
    {
        if (Default is not null)
            throw new InvalidOperationException("Default saver already defined");
        return Default = new LambdaSaver(ser, deser);
    }
    /// <summary>
    /// </summary>
    public static implicit operator SettingsFieldSaver<TField>((Func<TField, String> ser, Func<String, TField> deser) t) => new LambdaSaver(t.ser, t.deser);

    private sealed class NumberSaver<T> : SettingsFieldSaver<T>
        where T : struct, TField, System.Numerics.IBinaryNumber<T>
    {
        private static readonly System.Globalization.NumberFormatInfo empty_nfi = new();

        protected override String SerializeImpl(T value) => value.ToString(null, empty_nfi);
        protected override T DeserializeImpl(String value) => T.Parse(value, empty_nfi);

    }

    private sealed class SelfReporterSaver<T> : SettingsFieldSaver<T>
        where T : TField, ISettingsSaveable<T>
    {
        protected override String SerializeImpl(T value) => T.SerializeSetting(value);
        protected override T DeserializeImpl(String value) => T.DeserializeSetting(value);
    }

    private sealed class NullableSaver<T>(SettingsFieldSaver<T> base_saver) : SettingsFieldSaver<T?>
        where T : struct, TField
    {
        private readonly SettingsFieldSaver<T> base_saver = base_saver;

        protected override String SerializeImpl(T? nvalue) => nvalue is { } value ? "+" + this.base_saver.Serialize(value) : "-";
        protected override T? DeserializeImpl(String s)
        {
            switch (s.FirstOrDefault())
            {
                case '+':
                    return this.base_saver.Deserialize(s[1..]);
                case '-':
                    return null;
                default:
                    try
                    {
                        return this.base_saver.Deserialize(s);
                    }
                    catch (FormatException ex)
                    {
                        throw new FormatException($"Failed to deserialize nullable value even ignoring lack of nullable header: {s}", ex);
                    }
            }
        }
    }

    static SettingsFieldSaver()
    {

        if (typeof(TField) == typeof(String))
            Default = (SettingsFieldSaver<TField>)(Object)StringSaver.SingleLine;

        if (typeof(TField) == typeof(Boolean))
            Default = (SettingsFieldSaver<TField>)(Object)new SettingsFieldSaver<Boolean>.LambdaSaver(v => v ? "1" : "0", v => v != "0");

        if (typeof(TField).GetInterfaces().Any(intr => intr.IsGenericType && intr.GetGenericTypeDefinition() == typeof(System.Numerics.IBinaryNumber<>)))
            Default = (SettingsFieldSaver<TField>?)Activator.CreateInstance(typeof(NumberSaver<>).MakeGenericType(typeof(TField), typeof(TField))) ?? throw null!;

        if (typeof(TField).GetInterfaces().Any(intr => intr.IsGenericType && intr.GetGenericTypeDefinition() == typeof(ISettingsSaveable<>)))
            Default = (SettingsFieldSaver<TField>?)Activator.CreateInstance(typeof(SelfReporterSaver<>).MakeGenericType(typeof(TField), typeof(TField))) ?? throw null!;

        if (Nullable.GetUnderlyingType(typeof(TField)) is Type null_base_type)
        {
            var base_saver_type = typeof(SettingsFieldSaver<>).MakeGenericType(null_base_type);
            var base_default_prop = base_saver_type.GetProperty(nameof(Default), BindingFlags.Static | BindingFlags.Public) ?? throw null!;
            var base_default = base_default_prop.GetValue(null);
            if (base_default == null)
                return;
            Default = (SettingsFieldSaver<TField>?)Activator.CreateInstance(typeof(NullableSaver<>).MakeGenericType(null_base_type, null_base_type), args: [base_default]) ?? throw null!;
        }

    }

}
