using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Client;

internal static class GraphRequest
{
    internal static GraphProtocolExecuteMessage Create(string statement, IReadOnlyDictionary<string, object?>? parameters)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(statement);
        var encoded = new Dictionary<string, byte[]>(parameters?.Count ?? 0);
        var writer = new DatabaseKeyWriter();
        if (parameters is not null)
        {
            foreach ((string name, object? value) in parameters)
            {
                writer.Reset();
                DatabaseValueCodec.Append(writer, value);
                encoded.Add(name, writer.ToArray());
            }
        }
        return new GraphProtocolExecuteMessage(statement, encoded);
    }
}

