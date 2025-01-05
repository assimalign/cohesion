namespace Assimalign.Cohesion.Net.Udt.Internal;


internal class UdtCongestionControlFactory<T> : UdtCongestionControlVirtualFactory where T : new()
{
    public override UdtCongestionControlBase create()
    {
        return new T() as UdtCongestionControlBase;
    }

    public override UdtCongestionControlVirtualFactory clone()
    {
        return new UdtCongestionControlFactory<T>();
    }
}