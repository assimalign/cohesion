using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Graph.Catalog;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Graph.Protocol;
using Assimalign.Cohesion.Database.Graph.Storage;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Storage.Units;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphHygieneTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Projection: source-free property names render embedded quotes reversibly")]
    public void Describe_QuotedProperty_ShouldRoundTripWithoutSourceText()
    {
        // Arrange
        var expression = new GqlPropertyExpression("a\"b", "p\"q");

        // Act
        string text = GraphProjectionText.Describe(expression);
        var statement = (GqlQueryStatement)new GqlQueryParser().Parse("MATCH (\"a\"\"b\") RETURN " + text);

        // Assert
        text.ShouldBe("\"a\"\"b\".\"p\"\"q\"");
        statement.Diagnostics.ShouldBeEmpty();
        var property = statement.GqlExpression.Projections.Single().Expression.ShouldBeOfType<GqlPropertyExpression>();
        property.Variable.ShouldBe(expression.Variable);
        property.Property.ShouldBe(expression.Property);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Catalog: record size bounds count encoded UTF-8 bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Encode_CatalogNameAtRecordLimit_ShouldAcceptExactFitAndRefuseOverflow(bool multibyte)
    {
        // Arrange: a label's fixed header and null schema occupy 43 bytes.
        int capacity = SlottedPage.MaxRecordSize - 43;
        string name = multibyte ? new string('é', capacity / 2) + new string('x', capacity % 2) : new string('x', capacity);
        var label = new GraphLabelMetadata(Guid.NewGuid(), name);

        // Act
        byte[] exact = GraphCatalogCodec.Encode(new CatalogRecord(label: label), new TransactionSequence(1));
        var overflow = new CatalogRecord(label: label with { Name = name + "x" });

        // Assert
        exact.Length.ShouldBe(SlottedPage.MaxRecordSize);
        GraphCatalogCodec.Decode(exact).Label.ShouldBe(label);
        Should.Throw<GraphElementTooLargeException>(() => GraphCatalogCodec.Encode(overflow, new TransactionSequence(1)));
        await using var engine = GraphDatabaseEngine.Create("catalog-exact-fit", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await GraphSchema.Open(database, session).SaveLabelAsync(label, CancellationToken.None);
        (await GraphSchema.Open(database, session).GetLabelsAsync(CancellationToken.None)).ShouldHaveSingleItem().ShouldBe(label);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Wire: oversized catalog names fail with CG02009 and preserve the session")]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Execute_OversizedCatalogName_ShouldReportElementTooLarge(int kind)
    {
        // Arrange
        await using var harness = await GraphServerHarness.StartAsync();
        await using var client = await harness.DialAsync();
        await client.HandshakeAsync();
        string name = new('x', 9000);
        string text = kind switch
        {
            0 => $"INSERT (:{name})",
            1 => $"INSERT ()-[:{name}]->()",
            _ => $"INSERT (:Label {{{name}: 1}})",
        };

        // Act
        await client.SendAsync(GraphProtocolMessageType.Execute, GraphProtocolExecuteMessage.Create(text).Encode());
        var frame = await client.ExpectAsync(ProtocolMessageType.Error);
        var error = ProtocolErrorMessage.Decode(frame.Payload.Span);

        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ExecutionFailure);
        error.Message.ShouldStartWith("CG02009:", Case.Sensitive);
        error.Message.ShouldContain("Graph catalog metadata encodes to", Case.Sensitive);
        await client.SendAsync(ProtocolMessageType.Ping);
        await client.ExpectAsync(ProtocolMessageType.Pong);
        var session = (GraphDatabaseSession)harness.Server.Sessions.Single().DatabaseSession!;
        (await GraphSchema.Open(session.Database, session).GetLabelsAsync(CancellationToken.None)).ShouldBeEmpty();
        (await GraphSchema.Open(session.Database, session).GetRelationshipTypesAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Catalog: metadata-only overflow carries structured CG02009")]
    public async Task SaveLabelAsync_CatalogOnlyOverflow_ShouldReportStructuredSizeFailure()
    {
        // Arrange: the label name fits a node record; only the catalog stores its owner name.
        await using var engine = GraphDatabaseEngine.Create("catalog-only-size", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        string name = "Fits";
        var label = new GraphLabelMetadata(Guid.NewGuid(), name, DatabaseObjectOwner.Schema, new string('s', 9000));
        var record = new StoredGraphNode(1, [name], new Dictionary<string, object?>());
        GraphRecordCodec.Encode(new GraphRecord(1, 1, Node: record, LabelIds: [label.Id]), new TransactionSequence(1)).Length
            .ShouldBeLessThan(SlottedPage.MaxRecordSize);

        // Act
        var failure = await Should.ThrowAsync<DatabaseException>(async () =>
            await GraphSchema.Open(database, session).SaveLabelAsync(label, CancellationToken.None));

        // Assert
        failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG02009");
        failure.Message.ShouldNotStartWith("CG02009:", Case.Sensitive);
        failure.InnerException.ShouldBeOfType<GraphElementTooLargeException>().Message.ShouldContain("Graph catalog metadata encodes to", Case.Sensitive);
        (await GraphSchema.Open(database, session).GetLabelsAsync(CancellationToken.None)).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Schema: an unknown relationship type error names the type")]
    public async Task DropRelationshipType_UnknownName_ShouldIncludeName()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("hygiene", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);

        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () =>
            await GraphSchema.Open(database, session).DropRelationshipTypeAsync("MissingType", CancellationToken.None));

        // Assert
        error.Message.ShouldContain("MissingType", Case.Sensitive);
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CG01002");
    }

    [Fact(DisplayName = "Cohesion Test [Database.Graph] - Properties: unsigned runtime types retain their declared signed/decimal classification")]
    public async Task CreateNode_UnsignedProperties_ShouldPreserveRuntimeTypesAndClassifyWithoutLoss()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("unsigned", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        object[] values = [(byte)255, ushort.MaxValue, uint.MaxValue, ulong.MaxValue];
        DatabaseType[] types = [DatabaseType.Int16, DatabaseType.Int32, DatabaseType.Int64, DatabaseType.Decimal];

        // Act
        for (int index = 0; index < values.Length; index++)
        {
            object value = values[index];
            var schema = GraphSchema.Open(database, session);
            var label = new GraphLabelMetadata(Guid.NewGuid(), "Unsigned" + index);
            await schema.SaveLabelAsync(label, CancellationToken.None);
            await schema.SavePropertyKeyAsync(new(label.Id, "v", types[index], Required: true), CancellationToken.None);
            var node = await database.CreateNodeAsync(session, [label.Name], new Dictionary<string, object?> { ["v"] = value }, CancellationToken.None);
            var stored = (await database.GetNodeAsync(session, node.Id, CancellationToken.None))!.Value.Properties["v"];

            // Assert
            GraphDatabase.TypeOf(stored).ShouldBe(types[index]);
            stored.ShouldBe(value);
            stored!.GetType().ShouldBe(value.GetType());
            var component = GraphValueCodec.Decode(GraphValueCodec.Encode(stored));
            component.ShouldBe(stored);
            component!.GetType().ShouldBe(value.GetType());
        }
    }
}
