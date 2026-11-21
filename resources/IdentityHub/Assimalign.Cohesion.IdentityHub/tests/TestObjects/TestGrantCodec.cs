using System;
using System.Text.Json;

namespace Assimalign.Cohesion.IdentityHub.Tests;

/// <summary>
/// The codec a feature package would write for <see cref="TestGrant"/>: it names the record set,
/// picks the key, the expiry, and the index entries, and lays the payload out as JSON.
/// </summary>
internal sealed class TestGrantCodec : IIdentityHubRecordCodec<TestGrant>
{
    public static readonly IdentityHubRecordIndexName UserCode = "user-code";

    public static readonly IdentityHubRecordIndexName Client = "client";

    public IdentityHubRecordSetName Set => "test.grants";

    public IdentityHubRecord Encode(TestGrant value)
    {
        ArgumentNullException.ThrowIfNull(value);
        return new IdentityHubRecord(
            value.Code,
            JsonSerializer.SerializeToUtf8Bytes(value, TestGrantJsonContext.Default.TestGrant),
            value.ExpiresAt,
            [
                new IdentityHubRecordIndex(UserCode, value.UserCode, isUnique: true),
                new IdentityHubRecordIndex(Client, value.ClientId),
            ]);
    }

    public TestGrant Decode(IdentityHubRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        return JsonSerializer.Deserialize(record.Payload.Span, TestGrantJsonContext.Default.TestGrant)!;
    }
}
