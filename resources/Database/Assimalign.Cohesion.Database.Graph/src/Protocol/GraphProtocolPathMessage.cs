using System;
using System.Collections.Generic;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph;

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
        Validate();
        var buffer = new List<byte>();
        ProtocolPayload.WriteInt32(buffer, Nodes.Count);
        foreach (var node in Nodes)
        {
            ProtocolPayload.WriteInt64(buffer, unchecked((long)node.Id.Value));
            ProtocolPayload.WriteInt32(buffer, node.Labels.Count);
            foreach (string label in node.Labels) { ProtocolPayload.WriteString(buffer, label); }
            GraphProtocolProperties.Write(buffer, node.Properties);
        }
        ProtocolPayload.WriteInt32(buffer, Relationships.Count);
        foreach (var relationship in Relationships)
        {
            ProtocolPayload.WriteInt64(buffer, unchecked((long)relationship.Id.Value));
            ProtocolPayload.WriteInt64(buffer, unchecked((long)relationship.From.Value));
            ProtocolPayload.WriteInt64(buffer, unchecked((long)relationship.To.Value));
            ProtocolPayload.WriteString(buffer, relationship.Type);
            GraphProtocolProperties.Write(buffer, relationship.Properties);
        }
        return buffer.ToArray();
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
        foreach (var node in Nodes)
        {
            if (node.Id.Value == 0) { throw new ProtocolException("Graph identities must be nonzero."); }
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
