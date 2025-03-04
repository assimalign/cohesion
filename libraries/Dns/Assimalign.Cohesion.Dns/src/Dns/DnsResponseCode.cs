namespace Assimalign.Cohesion.Dns;

public enum DnsResponseCode : ushort
{
    NoError = 0,
    FormatError = 1,
    ServerFailure = 2,
    NxDomain = 3,
    NotImplemented = 4,
    Refused = 5,
    YXDomain = 6,
    YXRRSet = 7,
    NXRRSet = 8,
    NotAuth = 9,
    NotZone = 10,
    BADVERS = 16,
    BADCOOKIE = 23
}
