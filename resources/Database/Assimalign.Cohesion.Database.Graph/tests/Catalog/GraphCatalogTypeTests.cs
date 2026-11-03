using System;
using System.Linq;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Catalog.Tests;

public sealed class GraphCatalogTypeTests
{
    [Theory(DisplayName = "Cohesion Test [Graph.Catalog] - Graph types: every new kind round-trips only at v3")]
    [InlineData(6)]
    [InlineData(7)]
    [InlineData(8)]
    public void Codec_GraphTypeKind_ShouldRoundTripAndRefuseV2(byte kind)
    {
        // Arrange
        var property = GraphPropertyType.Create("p", DatabaseType.Int64, true);
        CatalogRecord record = kind switch
        {
            6 => new(graphType: GraphType.Create()),
            7 => new(nodeType: GraphNodeType.Create("N", ["I"], [property])),
            _ => new(edgeType: GraphEdgeType.Create("T", "N", "M", [property])),
        };

        // Act
        byte[] bytes = GraphCatalogCodec.Encode(record, new TransactionSequence(7));
        var decoded = GraphCatalogCodec.Decode(bytes);

        // Assert
        bytes[17].ShouldBe((byte)3);
        decoded.Kind.ShouldBe(kind);
        decoded.Id.ShouldBe(GraphCatalog.GraphTypeId);
        decoded.Name.ShouldBe(record.Name);
        if (kind == 6) { decoded.GraphType.ShouldNotBeNull().NodeTypes.ShouldBeEmpty(); }
        else if (kind == 7)
        {
            decoded.NodeType!.ImpliedLabels.ShouldBe(["I"]);
            decoded.NodeType.Properties.Select(item => (item.Name, item.Type, item.Required)).ShouldBe([("p", DatabaseType.Int64, true)]);
        }
        else
        {
            (decoded.EdgeType!.SourceNodeType, decoded.EdgeType.TargetNodeType).ShouldBe(("N", "M"));
            decoded.EdgeType.Properties.ShouldHaveSingleItem().Required.ShouldBeTrue();
        }
        bytes[17] = 2;
        Should.Throw<GraphCatalogException>(() => GraphCatalogCodec.Decode(bytes)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06019");
    }

    [Theory(DisplayName = "Cohesion Test [Graph.Catalog] - Graph types: malformed counts flags types identity and trailing bytes are corruption")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    public void Decode_MalformedGraphType_ShouldRefuse(int damage)
    {
        // Arrange
        byte[] bytes = GraphCatalogCodec.Encode(new CatalogRecord(nodeType: GraphNodeType.Create("N", ["I"],
            [GraphPropertyType.Create("p", DatabaseType.Int64, true)])), new TransactionSequence(7));
        if (damage == 0) { bytes = bytes[..^1]; }
        else if (damage == 1) { bytes[^1] = 2; }
        else if (damage == 2) { bytes[^2] = 254; }
        else if (damage == 3) { bytes[18] ^= 1; }
        else if (damage == 4) { bytes[39] = 255; bytes[40] = 255; bytes[41] = 255; bytes[42] = 127; }
        else { bytes = [.. bytes, 0]; }

        // Act / Assert
        Should.Throw<GraphCatalogException>(() => GraphCatalogCodec.Decode(bytes)).Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG06006");
    }
}
