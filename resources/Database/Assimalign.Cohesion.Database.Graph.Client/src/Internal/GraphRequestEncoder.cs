using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Internal;

internal static class GraphRequestEncoder
{
    internal static GraphProtocolExecuteMessage Create(string statement, IReadOnlyDictionary<string, object?>? parameters, bool compositeValues = false)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        var encoded = new Dictionary<string, byte[]>(parameters?.Count ?? 0);
        var writer = new DatabaseKeyWriter();
        if (parameters is not null)
        {
            foreach ((string name, object? value) in parameters)
            {
                if (compositeValues) { encoded.Add(name, GraphValueCodec.Encode(value)); continue; }
                if (value is System.Collections.IList and not byte[] or IReadOnlyDictionary<string, object?>)
                { throw new GraphClientException(ProtocolErrorCode.ExecutionFailure, ErrorMessages.CompositeCapabilityRequired); }
                writer.Reset();
                DatabaseValueCodec.Append(writer, value);
                encoded.Add(name, writer.ToArray());
            }
        }
        return new GraphProtocolExecuteMessage(statement, encoded);
    }
}

