using System;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// A <see cref="TestGrant"/> codec a test can bend: it names any record set, and encodes or
/// decodes through a delegate when one is set and through <see cref="TestGrantCodec"/> otherwise.
/// </summary>
internal sealed class StubGrantCodec : IIdentityHubRecordCodec<TestGrant>
{
    private readonly TestGrantCodec _codec = new();

    public IdentityHubRecordSetName Set { get; set; } = "test.grants";

    public Func<TestGrant, IdentityHubRecord?>? OnEncode { get; set; }

    public Func<IdentityHubRecord, TestGrant?>? OnDecode { get; set; }

    public IdentityHubRecord Encode(TestGrant value)
    {
        return OnEncode is null ? _codec.Encode(value) : OnEncode(value)!;
    }

    public TestGrant Decode(IdentityHubRecord record)
    {
        return OnDecode is null ? _codec.Decode(record) : OnDecode(record)!;
    }
}
