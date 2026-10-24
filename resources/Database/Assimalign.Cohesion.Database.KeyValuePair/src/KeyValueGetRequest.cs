using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// A point read: returns the visible, live entry for a key, as a one-row result
/// set with the columns <c>key</c> (binary), <c>value</c> (binary), and
/// <c>etag</c> (int64) — or zero rows when the key has no visible entry.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("KeyValueGetRequest Operation={Operation}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueGetRequest.DebugView))]
public sealed class KeyValueGetRequest : KeyValueRequest
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueGetRequest"/>.
    /// </summary>
    /// <param name="key">The key to read.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public KeyValueGetRequest(ReadOnlyMemory<byte> key)
        : base(KeyValueOperation.Get)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException(ErrorMessages.KeyValueDeleteRequestArgument, nameof(key));
        }

        Key = key;
    }

    /// <summary>
    /// Gets the key to read.
    /// </summary>
    public ReadOnlyMemory<byte> Key { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueGetRequest _target;

        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueGetRequest value)
        {
            _target = value;
        }

        public global::System.ReadOnlyMemory<byte> Key => _target.Key;
        public global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueOperation Operation => _target.Operation;
    }
}
