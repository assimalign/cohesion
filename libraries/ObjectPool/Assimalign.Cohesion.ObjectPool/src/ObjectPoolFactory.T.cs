namespace Assimalign.Cohesion.ObjectPool;

/// <summary>
/// 
/// </summary>
/// <typeparam name="T"></typeparam>
public abstract class ObjectPoolFactory<T> where T : class
{
    /// <summary>
    /// 
    /// </summary>
    /// <returns></returns>
    public abstract T Create();
}
