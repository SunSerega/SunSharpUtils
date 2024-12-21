using System;

using System.Threading;

namespace SunSharpUtils.Threading;

/// <summary>
/// IDisposable struct, which holds a lock on an object
/// </summary>
public readonly struct ObjectLocker : IDisposable
{
    private readonly object o;

    /// <summary>
    /// Locks the object, and will only release it on Dispose
    /// </summary>
    /// <param name="o"></param>
    public ObjectLocker(object o)
    {
        if (o.GetType().IsValueType)
            throw new InvalidOperationException("Tried locking value type");
        this.o = o;
        Monitor.Enter(o);
    }

    /// <summary>
    /// Tries to lock the object, and returns null if the object is already locked by another thread
    /// </summary>
    /// <param name="o"></param>
    /// <returns></returns>
    public static ObjectLocker? TryLock(object o)
    {
        bool got_lock = false;
        Monitor.TryEnter(o, ref got_lock);
        if (!got_lock) return null;
        try
        {
            return new ObjectLocker(o);
        }
        finally
        {
            Monitor.Exit(o);
        }
    }

    /// <summary>
    /// Releases the lock on the object
    /// </summary>
    public void Dispose() => Monitor.Exit(o);

}