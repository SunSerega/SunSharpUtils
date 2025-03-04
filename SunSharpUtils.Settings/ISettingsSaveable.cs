using System;

namespace SunSharpUtils.Settings;

/// <summary>
/// Implement this to define default saver for this type
/// </summary>
/// <typeparam name="TSelf"></typeparam>
public interface ISettingsSaveable<TSelf>
    where TSelf : ISettingsSaveable<TSelf>
{

    /// <summary>
    /// </summary>
    static abstract String SerializeSetting(TSelf setting);
    /// <summary>
    /// </summary>
    static abstract TSelf DeserializeSetting(String setting);

}