namespace Assimalign.Cohesion.Web.ForwardedHeaders.Internal;

internal enum ForwardingStopReason
{
    None,
    UntrustedPeer,
    ForwardLimit,
    MalformedFor,
    InvalidProto,
    ImplausibleHost,
    UnusableForwardedHeader,
    AddresslessHop,
}
