namespace SunAssistant.Core;

/// <summary>
/// </summary>
public interface ISingleton
{
    /// <summary>
    /// </summary>
    public abstract static ISingleton Instance { get; }
}

/// <summary>
/// </summary>
public abstract class Singleton<TSelf> : ISingleton
    where TSelf : Singleton<TSelf>, new()
{
    /// <summary>
    /// </summary>
    public static TSelf Instance { get; } = new();
    static ISingleton ISingleton.Instance => Instance;
}
