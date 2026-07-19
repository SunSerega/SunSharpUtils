using System;
using System.Diagnostics.CodeAnalysis;
using System.Threading;

namespace SunSharpUtils.Threading;

/// <summary>
/// Extensions for Lock class
/// </summary>
public static class LockExt
{

    /// <summary>
    /// Enters a lock and returns a nullable scope object
    /// </summary>
    /// <param name="l"></param>
    /// <returns></returns>
    [return: NotNull]
    public static LockScope? EnterNScope(this Lock l)
    {
        l.Enter();
        return new(l);
    }

    /// <summary>
    /// Tries to lock
    /// - If locked, returns a nullable scope object
    /// - Otherwise, returns null, meaning already locked by another thread
    /// </summary>
    /// <param name="l"></param>
    /// <returns></returns>
    public static LockScope? TryEnterNScope(this Lock l)
    {
        if (!l.TryEnter())
            return null;
        return new(l);
    }

    /// <summary>
    /// IDisposable struct, which holds a lock on an object
    /// </summary>
    /// <param name="l"></param>
    public struct LockScope(Lock l) : IDisposable
    {
        private Lock? l = l;

        /// <summary>
        /// Exits the lock
        /// </summary>
        public void Dispose()
        {
            this.l?.Exit();
            this.l = null;
        }

    }

}