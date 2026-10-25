using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;

using Assimalign.Cohesion.Database.KeyValuePair.Internal;
using Assimalign.Cohesion.Database.KeyValuePair.Protocol;

namespace Assimalign.Cohesion.Database.KeyValuePair;

/// <summary>Invokes a registered scalar, returning one nullable column named <c>value</c> and one row.</summary>
/// <remarks>Arguments and binary buffers are copied at construction. Repeated references to the same binary backing range share one detached snapshot. The request can be reused concurrently.</remarks>
[DebuggerDisplay("KeyValueFunctionRequest {Name}")]
[DebuggerTypeProxy(typeof(KeyValueFunctionRequest.DebugView))]
public sealed class KeyValueFunctionRequest : KeyValueRequest
{
    private readonly object?[] _arguments;

    /// <summary>Initializes a scalar call with detached primitive arguments.</summary>
    /// <param name="name">The case-insensitive function identifier; default is refused.</param>
    /// <param name="arguments">Native primitive values in signature order; NULL is permitted.</param>
    /// <exception cref="ArgumentNullException">The argument list is null.</exception>
    /// <exception cref="ArgumentException">The name is default, or an argument is not a supported primitive.</exception>
    public KeyValueFunctionRequest(KeyValueFunctionName name, IReadOnlyList<object?> arguments)
        : base(KeyValueOperation.Function)
    {
        if (name.Value is null) { throw new ArgumentException(ErrorMessages.FunctionNameRequired, nameof(name)); }
        ArgumentNullException.ThrowIfNull(arguments);
        Name = name;
        _arguments = new object?[arguments.Count];
        var visible = new object?[arguments.Count];
        Dictionary<ReadOnlyMemory<byte>, byte[]>? binarySnapshots = null;
        for (int index = 0; index < arguments.Count; index++)
        {
            object? source = arguments[index];
            ReadOnlyMemory<byte>? binary = source switch
            {
                byte[] bytes => bytes,
                ReadOnlyMemory<byte> bytes => bytes,
                Memory<byte> bytes => bytes,
                // Keep NULL nullable: a bare null can bind the byte[] -> memory conversion.
                _ => (ReadOnlyMemory<byte>?)null,
            };
            object? value;
            if (binary is { } memory)
            {
                // Equality is backing-object and range identity, not byte-content equality.
                // A wire parameter repeated many times must not multiply its owned payload.
                binarySnapshots ??= [];
                if (!binarySnapshots.TryGetValue(memory, out byte[]? snapshot))
                {
                    snapshot = memory.ToArray();
                    binarySnapshots.Add(memory, snapshot);
                }
                value = snapshot;
            }
            else
            {
                value = KeyValueFunctionValues.Snapshot(source, nameof(arguments));
            }
            _arguments[index] = value;
            visible[index] = value is byte[] ownedBytes ? (ReadOnlyMemory<byte>)ownedBytes : value;
        }
        Arguments = new ReadOnlyCollection<object?>(visible);
    }

    /// <summary>Gets the immutable function identifier.</summary>
    public KeyValueFunctionName Name { get; }

    /// <summary>Gets the detached primitive arguments, with binary values exposed as read-only memory.</summary>
    public IReadOnlyList<object?> Arguments { get; }

    internal ReadOnlySpan<object?> ArgumentValues => _arguments;

    private sealed class DebugView
    {
        private readonly KeyValueFunctionRequest _target;

        public DebugView(KeyValueFunctionRequest value) { _target = value; }

        public KeyValueFunctionName Name => _target.Name;
        public KeyValueOperation Operation => _target.Operation;
    }
}
