namespace System.Threading;

/// <summary>
/// 
/// </summary>
public interface IChangeToken
{
    /// <summary>
    /// Registers for a callback that will be invoked when the entry has changed.
    /// </summary>
    /// <param name="callback">The <see cref="Action{Object}"/> to invoke.</param>
    /// <returns>An <see cref="IDisposable"/> that is used to unregister the callback.</returns>
    IDisposable OnChange(Action<object> callback);
}
