using System;

namespace SunSharpUtils;

/// <summary>
/// Centralized user prompt handling
/// </summary>
public static class Prompt
{

    /// <summary>
    /// </summary>
    public readonly record struct DelegateStore
    {

        /// <summary>
        /// title, content
        /// </summary>
        public Action<string, string?> Notify { get; init; }

        /// <summary>
        /// title, content, return true if yes
        /// </summary>
        public Func<string, string?, bool> AskYesNo { get; init; }

        /// <summary>
        /// title, content, options, return selected option or null
        /// </summary>
        public Func<string, string?, string[], string?> AskAny { get; init; }

    }
    private static DelegateStore? delegate_store = null;
    private static DelegateStore D => delegate_store ?? throw new InvalidOperationException("Prompt.Init() not called");

    /// <summary>
    /// </summary>
    public static void Init(DelegateStore delegate_store)
    {
        if (Prompt.delegate_store is not null)
            throw new InvalidOperationException("Prompt.Init() called twice");
        Prompt.delegate_store = delegate_store;
    }

    /// <summary>
    /// </summary>
    public static void Notify(string title, string? content = null) => D.Notify(title, content);

    /// <summary>
    /// </summary>
    public static bool AskYesNo(string title, string? content = null) => D.AskYesNo(title, content);

    /// <summary>
    /// </summary>
    public static string? AskAny(string title, string? content, params string[] options)
    {
        if (options.Length == 0)
            throw new InvalidOperationException("No options provided. Use Notify to prompt user without options");
        return D.AskAny(title, content, options);
    }

    /// <summary>
    /// </summary>
    public static TEnum? AskAny<TEnum>(string title, string? content, params TEnum[] options)
        where TEnum: struct, Enum
    {
        var res = AskAny(title, content, Array.ConvertAll(options, e => e.ToString()));
        if (res is null) return null;
        return (TEnum)Enum.Parse(typeof(TEnum), res);
    }

}