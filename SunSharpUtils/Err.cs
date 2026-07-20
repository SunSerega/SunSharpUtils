using System;
using System.Diagnostics.CodeAnalysis;

namespace SunSharpUtils;

/// <summary>
/// Type of exception that should be handled by only displaying a message to the user
/// </summary>
/// <remarks>
/// </remarks>
public sealed class MessageException(String message) : Exception(message)
{
    /// <summary>
    /// </summary>
    public override String ToString() => this.Message;
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
        public required Action<Exception> Handle { get; init; }
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
    public static void HandleDuring(Action body)
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
    /// Tries to execute the body function and catches a specific exception type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="body"></param>
    /// <param name="result"></param>
    /// <param name="exception"></param>
    /// <param name="filter"></param>
    /// <returns></returns>
    public static Boolean TryCatch<T>(Func<T> body, [NotNullWhen(false)] out T? result, [NotNullWhen(true)] out Exception? exception, Predicate<Exception>? filter = null) where T : notnull =>
        Err<Exception>.TryCatch(body, out result, out exception, filter);

}

/// <summary>
/// Centralized error handling
/// </summary>
public static class Err<TException>
    where TException : Exception
{

    /// <summary>
    /// Tries to execute the body function and catches a specific exception type
    /// </summary>
    /// <typeparam name="T"></typeparam>
    /// <param name="body"></param>
    /// <param name="result"></param>
    /// <param name="exception"></param>
    /// <param name="filter"></param>
    /// <returns></returns>
    public static Boolean TryCatch<T>(Func<T> body, [NotNullWhen(false)] out T? result, [NotNullWhen(true)] out TException? exception, Predicate<TException>? filter = null)
        where T : notnull
    {
        try
        {
            result = body();
            exception = default;
            return false;
        }
        catch (TException e) when (filter?.Invoke(e) ?? true)
        {
            result = default;
            exception = e;
            return true;
        }
    }

}