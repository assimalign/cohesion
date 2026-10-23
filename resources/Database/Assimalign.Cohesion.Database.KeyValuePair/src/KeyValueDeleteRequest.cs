using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// A delete, optionally conditional. The result is a plain
/// <see cref="Assimalign.Cohesion.Database.Execution.DatabaseResult"/> whose
/// <c>AffectedCount</c> is 1 when an entry was deleted and 0 otherwise (no
/// visible entry, or the compare-and-swap condition did not hold — a first-class
/// outcome, never an exception).
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("KeyValueDeleteRequest Operation={Operation}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueDeleteRequest.DebugView))]
public sealed class KeyValueDeleteRequest : KeyValueRequest
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueDeleteRequest"/>.
    /// </summary>
    /// <param name="key">The key to delete.</param>
    /// <param name="expectedETag">The etag the key's current entry must carry for the delete to apply, or null for an unconditional delete.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public KeyValueDeleteRequest(ReadOnlyMemory<byte> key, long? expectedETag = null)
        : base(KeyValueOperation.Delete)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException(ErrorMessages.KeyValueDeleteRequestArgument, nameof(key));
        }

        Key = key;
        ExpectedETag = expectedETag;
    }

    /// <summary>
    /// Gets the key to delete.
    /// </summary>
    public ReadOnlyMemory<byte> Key { get; }

    /// <summary>
    /// Gets the etag the key's current entry must carry for the delete to apply
    /// (compare-and-swap), or null for an unconditional delete.
    /// </summary>
    public long? ExpectedETag { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueDeleteRequest _target;

        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueDeleteRequest value)
        {
            _target = value;
        }

        public global::System.ReadOnlyMemory<byte> Key => _target.Key;
        public long? ExpectedETag => _target.ExpectedETag;
        public global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueOperation Operation => _target.Operation;
    }
}
