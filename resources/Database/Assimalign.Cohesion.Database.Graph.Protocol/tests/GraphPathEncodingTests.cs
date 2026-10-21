using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Protocol.Internal;
using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Protocol.Tests;

public sealed class GraphPathEncodingTests
{
    [Fact(DisplayName = "Cohesion Test [Graph.Protocol] - Span path encoding preserves every existing property tag and wire byte")]
    public void Encode_AllScalarProperties_ShouldPreserveExistingPayloadBytes()
    {
        // Arrange: the original List-based primitives independently pin the existing wire format.
        var properties = new Dictionary<string, object?>
        {
            ["null"] = null, ["false"] = false, ["true"] = true, ["utf8"] = "Café 😀",
            ["byte"] = byte.MaxValue, ["sbyte"] = sbyte.MinValue, ["short"] = short.MinValue,
            ["ushort"] = ushort.MaxValue, ["int"] = int.MinValue, ["uint"] = uint.MaxValue,
            ["long"] = long.MinValue, ["ulong"] = ulong.MaxValue, ["float"] = -1.25f,
            ["double"] = 1.5e100, ["decimal"] = -12.50m,
        };
        GraphNode[] nodes = [new(new(1), ["Person", "Employé"], properties), new(new(2), [], new Dictionary<string, object?>())];
        GraphRelationship[] edges = [new(new(3), "KNOWS", new(2), new(1), properties)];
        var message = new GraphProtocolPathMessage(nodes, edges);
        var legacy = new List<byte>();
        ProtocolPayload.WriteInt32(legacy, nodes.Length);
        foreach (var node in nodes)
        {
            ProtocolPayload.WriteInt64(legacy, (long)node.Id.Value);
            ProtocolPayload.WriteInt32(legacy, node.Labels.Count);
            foreach (string label in node.Labels) { ProtocolPayload.WriteString(legacy, label); }
            GraphProtocolProperties.Write(legacy, node.Properties);
        }
        ProtocolPayload.WriteInt32(legacy, edges.Length);
        foreach (var edge in edges)
        {
            ProtocolPayload.WriteInt64(legacy, (long)edge.Id.Value);
            ProtocolPayload.WriteInt64(legacy, (long)edge.From.Value);
            ProtocolPayload.WriteInt64(legacy, (long)edge.To.Value);
            ProtocolPayload.WriteString(legacy, edge.Type);
            GraphProtocolProperties.Write(legacy, edge.Properties);
        }

        // Act
        byte[] destination = new byte[message.GetEncodedLength()];
        int written = message.Encode(destination);
        var decoded = GraphProtocolPathMessage.Decode(destination);

        // Assert
        written.ShouldBe(legacy.Count);
        destination.ShouldBe(legacy.ToArray());
        message.Encode().ShouldBe(destination);
        decoded.Relationships[0].From.ShouldBe(nodes[1].Id);
        foreach (var (key, value) in properties)
        {
            decoded.Nodes[0].Properties[key].ShouldBe(value);
            decoded.Nodes[0].Properties[key]?.GetType().ShouldBe(value?.GetType());
        }
        Should.Throw<ArgumentException>(() => message.Encode(destination.AsSpan(0, written - 1)));
    }

    [Fact(DisplayName = "Cohesion Test [Graph.Protocol] - Path measurement refuses a single oversized frame before allocating its payload")]
    public void GetEncodedLength_OversizedPath_ShouldRefusePerFrameLimit()
    {
        // Arrange
        var node = new GraphNode(new(1), [], new Dictionary<string, object?>
        { ["text"] = new string('x', (int)ProtocolFrameHeader.MaxPayloadLength) });
        var message = new GraphProtocolPathMessage([node], []);

        // Act / Assert
        Should.Throw<ProtocolException>(() => message.GetEncodedLength());
    }
}
