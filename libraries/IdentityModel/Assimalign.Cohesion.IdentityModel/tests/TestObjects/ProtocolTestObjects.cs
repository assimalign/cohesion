using Assimalign.Cohesion.IdentityModel;
using Assimalign.Cohesion.IdentityModel.Protocols;

namespace Assimalign.Cohesion.IdentityModel.Tests.TestObjects;

/// <summary>
/// Minimal concrete derivatives of the abstract protocol envelope types, standing in for
/// the protocol branches the later features add.
/// </summary>
internal sealed class TestMetadataDescriptor : ProtocolMetadataDescriptor
{
}

internal sealed class TestMetadata : ProtocolMetadata
{
    public TestMetadata(ProtocolMetadataDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
    }
}

internal sealed class TestRequestDescriptor : ProtocolRequestDescriptor
{
}

internal sealed class TestRequest : ProtocolRequest
{
    public TestRequest(ProtocolRequestDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
    }
}

internal sealed class TestResponseDescriptor : ProtocolResponseDescriptor
{
}

internal sealed class TestResponse : ProtocolResponse
{
    public TestResponse(ProtocolResponseDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
    }
}

internal sealed class TestLogoutRequestDescriptor : ProtocolLogoutRequestDescriptor
{
}

internal sealed class TestLogoutRequest : ProtocolLogoutRequest
{
    public TestLogoutRequest(ProtocolLogoutRequestDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
    }
}

internal sealed class TestLogoutResponseDescriptor : ProtocolLogoutResponseDescriptor
{
}

internal sealed class TestLogoutResponse : ProtocolLogoutResponse
{
    public TestLogoutResponse(ProtocolLogoutResponseDescriptor descriptor, AuthenticationProtocol protocol)
        : base(descriptor, protocol)
    {
    }
}
