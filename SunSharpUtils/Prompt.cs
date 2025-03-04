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
        public Action<String, String?> Notify { get; init; }

        /// <summary>
        /// title, content, return true if yes
        /// </summary>
        public Func<String, String?, Boolean> AskYesNo { get; init; }

        /// <summary>
        /// title, content, options, return selected option or null
        /// </summary>
        public Func<String, String?, String[], String?> AskAny { get; init; }

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
    public static void Notify(String title, String? content = null) => D.Notify(title, content);

    /// <summary>
    /// </summary>
    public static Boolean AskYesNo(String title, String? content = null) => D.AskYesNo(title, content);

    /// <summary>
    /// </summary>
    public static String? AskAny(String title, String? content, params String[] options)
    {
        if (options.Length == 0)
            throw new InvalidOperationException("No options provided. Use Notify to prompt user without options");
        return D.AskAny(title, content, options);
    }

    /// <summary>
    /// </summary>
    public static TEnum? AskAny<TEnum>(String title, String? content, params TEnum[] options)
        where TEnum: struct, Enum
    {
        var res = AskAny(title, content, Array.ConvertAll(options, e => e.ToString()));
        if (res is null) return null;
        return (TEnum)Enum.Parse(typeof(TEnum), res);
    }

}