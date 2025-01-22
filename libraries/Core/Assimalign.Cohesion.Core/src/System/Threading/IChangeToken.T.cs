namespace System.Threading;

/// <summary>
/// 
/// </summary>
/// <typeparam name="T"></typeparam>
public interface IChangeToken<T> : IChangeToken
{
    /// <summary>
    /// 
    /// </summary>
    /// <param name="callback"></param>
    /// <returns></returns>
    IDisposable OnChange(Action<T> callback);
}