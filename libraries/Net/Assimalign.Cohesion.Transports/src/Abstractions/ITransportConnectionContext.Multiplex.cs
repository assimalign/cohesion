namespace Assimalign.Cohesion.Transports;

public interface IMultiplexTransportConnectionContext : ITransportConnectionContext
{
    bool IsBidirectional { get; }
}
