using System;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>
/// A visibility probe: returns whether a key has a visible, live entry, as a
/// one-row result set with the column <c>exists</c> (boolean).
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("KeyValueExistsRequest Operation={Operation}")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueExistsRequest.DebugView))]
public sealed class KeyValueExistsRequest : KeyValueRequest
{
    /// <summary>
    /// Initializes a new <see cref="KeyValueExistsRequest"/>.
    /// </summary>
    /// <param name="key">The key to probe.</param>
    /// <exception cref="ArgumentException">Thrown when <paramref name="key"/> is empty.</exception>
    public KeyValueExistsRequest(ReadOnlyMemory<byte> key)
        : base(KeyValueOperation.Exists)
    {
        if (key.IsEmpty)
        {
            throw new ArgumentException(ErrorMessages.KeyValueDeleteRequestArgument, nameof(key));
        }

        Key = key;
    }

    /// <summary>
    /// Gets the key to probe.
    /// </summary>
    public ReadOnlyMemory<byte> Key { get; }

    private sealed class DebugView
    {
        private readonly global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueExistsRequest _target;

        public DebugView(global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueExistsRequest value)
        {
            _target = value;
        }

        public global::System.ReadOnlyMemory<byte> Key => _target.Key;
        public global::Assimalign.Cohesion.Database.KeyValuePair.KeyValueOperation Operation => _target.Operation;
    }
}
