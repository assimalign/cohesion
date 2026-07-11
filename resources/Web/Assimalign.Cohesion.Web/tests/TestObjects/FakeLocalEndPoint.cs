using System.Net;
using System.Net.Sockets;

namespace Assimalign.Cohesion.Web.Tests.TestObjects;

/// <summary>
/// A non-IP transport endpoint standing in for the Unix-domain-socket / named-pipe /
/// in-memory endpoints Cohesion's local drivers report — the category the
/// <c>TrustLocalTransports</c> option governs.
/// </summary>
internal sealed class FakeLocalEndPoint : EndPoint
{
    public override AddressFamily AddressFamily => AddressFamily.Unspecified;

    public override string ToString() => "fake-local";
}
