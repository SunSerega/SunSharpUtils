using System;

namespace SunSharpUtils;

/// <summary>
/// Type of exception that should be handled by only displaying a message to the user
/// </summary>
public sealed class MessageException(string message) : Exception(message)
{
    /// <summary>
    /// </summary>
    public override string ToString() => Message;
}

/// <summary>
/// Centralized error handling
/// </summary>
public static class Err
{

    /// <summary>
    /// Handle this to define how errors are handled                    <br />
    ///                                                                 <br />
    /// Predefined handlers:                                            <br />
    /// - WPF: call SunSharpUtils.WPF.Common.Init                       <br />
    ///     (opens a MessageBox with the error message)                 <br />
    /// </summary>
    public static event Action<Exception>? OnError;

    /// <summary>
    /// Use defined error handler                   <br />
    /// Throws if no handler is defined             <br />
    /// </summary>
    /// <param name="e"></param>
    /// <exception cref="Exception"></exception>
    public static void Handle(Exception e)
    {
        (OnError??throw new Exception("No error handler", e))(e);
    }

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