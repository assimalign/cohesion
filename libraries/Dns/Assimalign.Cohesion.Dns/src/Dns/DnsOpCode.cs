namespace Assimalign.Cohesion.Dns;

public enum DnsOpCode : byte
{
    StandardQuery = 0,
    InverseQuery = 1,
    ServerStatusRequest = 2,
    Notify = 4,
    Update = 5,
    DnsStatefulOperations = 6
}
