using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Graph.Protocol.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Protocol;

/// <summary>An ordered path retaining node identities, labels, properties, and directed relationships.</summary>
/// <param name="Nodes">The nodes in traversal order, including both endpoints.</param>
/// <param name="Relationships">The relationships connecting consecutive nodes, in traversal order.</param>
public sealed record GraphProtocolPathMessage(IReadOnlyList<GraphNode> Nodes, IReadOnlyList<GraphRelationship> Relationships)
{
    /// <summary>Encodes a path into one bounded protocol frame payload.</summary>
    /// <returns>The path payload.</returns>
    /// <exception cref="ProtocolException">The path is disconnected, has zero identities, or contains unsupported property values.</exception>
    public byte[] Encode()
    {
        byte[] payload = new byte[GetEncodedLength()];
        EncodeCore(payload, measure: false);
        return payload;
    }

    /// <summary>Measures the unchanged Path payload without allocating an encoded copy.</summary>
    /// <returns>The bounded payload length in bytes.</returns>
    /// <exception cref="ProtocolException">The path is invalid or exceeds the frame limit.</exception>
    public int GetEncodedLength()
    {
        Validate();
        return EncodeCore(default, measure: true);
    }

    /// <summary>Encodes the unchanged Path payload directly into caller-owned storage.</summary>
    /// <param name="destination">Storage for the complete payload.</param>
    /// <returns>The number of bytes written.</returns>
    /// <exception cref="ProtocolException">The path or a property is invalid, or exceeds the frame limit.</exception>
    /// <exception cref="ArgumentException">The destination is too small.</exception>
    public int Encode(Span<byte> destination)
    {
        Validate();
        return EncodeCore(destination, measure: false);
    }

    private int EncodeCore(Span<byte> destination, bool measure)
    {
        var writer = new GraphProtocolPathWriter(destination, measure);
        writer.WriteInt32(Nodes.Count);
        for (int i = 0; i < Nodes.Count; i++)
        {
            GraphNode node = Nodes[i];
            writer.WriteInt64(unchecked((long)node.Id.Value));
            writer.WriteInt32(node.Labels.Count);
            for (int j = 0; j < node.Labels.Count; j++) { writer.WriteString(node.Labels[j]); }
            writer.WriteProperties(node.Properties);
        }
        writer.WriteInt32(Relationships.Count);
        for (int i = 0; i < Relationships.Count; i++)
        {
            GraphRelationship relationship = Relationships[i];
            writer.WriteInt64(unchecked((long)relationship.Id.Value));
            writer.WriteInt64(unchecked((long)relationship.From.Value));
            writer.WriteInt64(unchecked((long)relationship.To.Value));
            writer.WriteString(relationship.Type);
            writer.WriteProperties(relationship.Properties);
        }
        return writer.Length;
    }

    /// <summary>Decodes and validates a complete path payload.</summary>
    /// <param name="payload">The frame payload.</param>
    /// <returns>The path, retaining traversal order and edge direction.</returns>
    /// <exception cref="ProtocolException">The payload is malformed, disconnected, or has trailing bytes.</exception>
    public static GraphProtocolPathMessage Decode(ReadOnlySpan<byte> payload)
    {
        int position = 0;
        int count = ReadCount(payload, ref position, 16);
        var nodes = new GraphNode[count];
        for (int i = 0; i < count; i++)
        {
            var id = new GraphNodeId(unchecked((ulong)ProtocolPayload.ReadInt64(payload, ref position)));
            int labelCount = ReadCount(payload, ref position, 4);
            var labels = new string[labelCount];
            for (int j = 0; j < labelCount; j++) { labels[j] = ProtocolPayload.ReadString(payload, ref position); }
            nodes[i] = new(id, labels, GraphProtocolProperties.Read(payload, ref position));
        }
        count = ReadCount(payload, ref position, 32);
        var relationships = new GraphRelationship[count];
        for (int i = 0; i < count; i++)
        {
            var id = new GraphRelationshipId(unchecked((ulong)ProtocolPayload.ReadInt64(payload, ref position)));
            var from = new GraphNodeId(unchecked((ulong)ProtocolPayload.ReadInt64(payload, ref position)));
            var to = new GraphNodeId(unchecked((ulong)ProtocolPayload.ReadInt64(payload, ref position)));
            string type = ProtocolPayload.ReadString(payload, ref position);
            relationships[i] = new(id, type, from, to, GraphProtocolProperties.Read(payload, ref position));
        }
        if (position != payload.Length) { throw new ProtocolException("Trailing bytes in graph path payload."); }
        var result = new GraphProtocolPathMessage(nodes, relationships);
        result.Validate();
        return result;
    }

    internal static int ReadCount(ReadOnlySpan<byte> payload, ref int position, int minimumSize)
    {
        int count = ProtocolPayload.ReadInt32(payload, ref position);
        if (count < 0 || count > (payload.Length - position) / minimumSize)
        {
            throw new ProtocolException("Malformed graph element count.");
        }
        return count;
    }

    private void Validate()
    {
        if (Nodes.Count == 0 || Relationships.Count != Nodes.Count - 1)
        {
            throw new ProtocolException("A path requires one more node than relationships.");
        }
        for (int i = 0; i < Nodes.Count; i++)
        {
            if (Nodes[i].Id.Value == 0) { throw new ProtocolException("Graph identities must be nonzero."); }
        }
        for (int i = 0; i < Relationships.Count; i++)
        {
            var edge = Relationships[i];
            if (edge.Id.Value == 0 ||
                !((edge.From == Nodes[i].Id && edge.To == Nodes[i + 1].Id) ||
                  (edge.To == Nodes[i].Id && edge.From == Nodes[i + 1].Id)))
            {
                throw new ProtocolException("A path relationship must connect its consecutive nodes.");
            }
        }
    }
}
