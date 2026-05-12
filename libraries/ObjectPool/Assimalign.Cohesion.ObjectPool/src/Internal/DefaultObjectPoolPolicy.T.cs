namespace Assimalign.Cohesion.ObjectPool.Internal;

internal class DefaultObjectPoolPolicy<T1> : ObjectPoolPolicy<T1>
{
    /// <inheritdoc />
    public override bool CanReturn(T1 instance)
    {
        return true;
    }
}
