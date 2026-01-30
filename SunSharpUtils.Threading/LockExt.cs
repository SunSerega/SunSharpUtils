using System;

using System.Threading;

namespace SunSharpUtils.Threading;

/// <summary>
/// Extensions for Lock class
/// </summary>
public static class LockExt
{

    /// <summary>
    /// Tries to lock, and returns null if already locked by another thread
    /// </summary>
    /// <param name="l"></param>
    /// <returns></returns>
    public static LockScopeObject? TryEnterScope(this Lock l)
    {
        if (!l.TryEnter())
            return null;
        return new(l);
    }

    /// <summary>
    /// IDisposable struct, which holds a lock on an object
    /// </summary>
    /// <param name="l"></param>
    public struct LockScopeObject(Lock l) : IDisposable
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