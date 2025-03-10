﻿using System;

namespace SunSharpUtils.Settings;

/// <summary>
/// Nullable value that can be saved to settings
/// </summary>
/// <typeparam name="T"></typeparam>
/// <param name="value"></param>
public readonly struct SettingsNullable<T>(T? value) : IEquatable<SettingsNullable<T>>, ISettingsSaveable<SettingsNullable<T>>
    where T : struct, IEquatable<T>
{
    /// <summary>
    /// </summary>
    public T? Value { get; init; } = value;
    /// <summary>
    /// </summary>
    public const String NullRepresentation = "-";

    /// <summary>
    /// </summary>
    public static implicit operator T?(SettingsNullable<T> sn) => sn.Value;
    /// <summary>
    /// </summary>
    public static implicit operator SettingsNullable<T>(T? value) => new(value);

    /// <summary>
    /// </summary>
    public static Boolean operator ==(SettingsNullable<T> a, SettingsNullable<T> b) =>
        a.Value is null ? b.Value is null : b.Value is not null && a.Value.Value.Equals(b.Value.Value);
    /// <summary>
    /// </summary>
    public static Boolean operator !=(SettingsNullable<T> a, SettingsNullable<T> b) => !(a == b);

    /// <summary>
    /// </summary>
    public Boolean Equals(SettingsNullable<T> other) => this == other;
    /// <summary>
    /// </summary>
    public override Boolean Equals(Object? obj) => obj is SettingsNullable<T> other && Equals(other);

    /// <summary>
    /// </summary>
    public override Int32 GetHashCode() => HashCode.Combine(Value);

    static String ISettingsSaveable<SettingsNullable<T>>.SerializeSetting(SettingsNullable<T> setting)
    {
        if (setting.Value is null)
            return NullRepresentation;
        var res = SettingsFieldSaver<T>.DefaultOrThrow.Serialize(setting.Value.Value);
        if (res == NullRepresentation)
            throw new FormatException($"Default serializer for type {typeof(T)} returned value conflicting null representation: {res}");
        return res;
    }

    static SettingsNullable<T> ISettingsSaveable<SettingsNullable<T>>.DeserializeSetting(String setting)
    {
        if (setting == NullRepresentation)
            return (T?)null;
        return SettingsFieldSaver<T>.DefaultOrThrow.Deserialize(setting);
    }

}
