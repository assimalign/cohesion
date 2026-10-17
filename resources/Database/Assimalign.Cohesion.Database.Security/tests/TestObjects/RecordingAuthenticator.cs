using System;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database.Security.Tests;

/// <summary>
/// An authenticator, derived in another assembly as an application's would be, that records
/// what its core received and returns a fixed result.
/// </summary>
internal sealed class RecordingAuthenticator : DatabaseAuthenticator
{
    private readonly bool _result;

    /// <summary>
    /// Initializes a new instance of the <see cref="RecordingAuthenticator"/> class.
    /// </summary>
    /// <param name="result">The result every call of the core returns.</param>
    public RecordingAuthenticator(bool result)
    {
        _result = result;
    }

    /// <summary>Gets the number of times the core ran.</summary>
    public int Calls { get; private set; }

    /// <summary>Gets the database the core last received.</summary>
    public string? Database { get; private set; }

    /// <summary>Gets the principal the core last received.</summary>
    public string? Principal { get; private set; }

    /// <summary>Gets the evidence the core last received.</summary>
    public byte[]? Evidence { get; private set; }

    /// <summary>Gets the cancellation token the core last received.</summary>
    public CancellationToken Token { get; private set; }

    /// <inheritdoc />
    protected override ValueTask<bool> AuthenticateCoreAsync(string database, string principal, ReadOnlyMemory<byte> evidence, CancellationToken cancellationToken)
    {
        Calls++;
        Database = database;
        Principal = principal;
        Evidence = evidence.ToArray();
        Token = cancellationToken;
        return ValueTask.FromResult(_result);
    }
}
