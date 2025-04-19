using System;

namespace SunSharpUtils;

/// <summary>
/// Type of exception that should be handled by only displaying a message to the user
/// </summary>
public sealed class MessageException : Exception
{
    internal MessageException(String message) : base(message) { }

    /// <summary>
    /// </summary>
    public override String ToString() => Message;
}

/// <summary>
/// Centralized error handling
/// </summary>
public static class Err
{
    /// <summary>
    /// </summary>
    public readonly record struct DelegateStore
    {
        /// <summary>
        /// Error handler
        /// </summary>
        public Action<Exception> Handle { get; init; }
    }
    private static DelegateStore? delegate_store = null;
    private static DelegateStore D => delegate_store ?? throw new InvalidOperationException("Err.Init() not called");

    /// <summary>
    /// </summary>
    public static void Init(DelegateStore delegate_store)
    {
        if (Err.delegate_store is not null)
            throw new InvalidOperationException("Err.Init() called twice");
        Err.delegate_store = delegate_store;
    }

    /// <summary>
    /// </summary>
    /// <param name="e"></param>
    /// <exception cref="Exception"></exception>
    public static void Handle(Exception e) => D.Handle(e);

    /// <summary>
    /// Passes MessageException to handler
    /// </summary>
    /// <param name="message"></param>
    public static void Handle(String message) =>
        Handle(new MessageException(message));

    /// <summary>
    /// Executes body action, using <see cref="Handle(Exception)"/> to handle any exception
    /// </summary>
    /// <param name="body"></param>
    public static void Handle(Action body)
    {
        try
        {
            body();
        }
        catch (Exception e)
        {
            Handle(e);
        }
    }

    /// <summary>
    /// Executes body func, using <see cref="Handle(Exception)"/> to handle any exception
    /// </summary>
    /// <typeparam name="T1"></typeparam>
    /// <typeparam name="T2"></typeparam>
    /// <param name="body"></param>
    /// <param name="fallback"></param>
    /// <returns>Either the return value of body func, or, in case of exception, value of <paramref name="fallback"/></returns>
    public static T2 Handle<T1, T2>(Func<T1> body, T2 fallback)
        where T1 : T2
    {
        try
        {
            return body();
        }
        catch (Exception e)
        {
            Handle(e);
            return fallback;
        }
    }
    /// <summary>
    /// Executes body func, using <see cref="Handle(Exception)"/> to handle any exception
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="body"></param>
    /// <returns>Either the return value of body func, or, in case of exception, default(<typeparamref name="T"/>)</returns>
    public static T? Handle<T>(Func<T> body) =>
        Handle(body, default(T));

}