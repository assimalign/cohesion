namespace Assimalign.Cohesion.ObjectPool.Internal;

internal class DefaultObjectPoolFactory<T1> : ObjectPoolFactory<T1> where T1 : class, new()
{
    public override T1 Create()
    {
        return new T1();
    }
}
