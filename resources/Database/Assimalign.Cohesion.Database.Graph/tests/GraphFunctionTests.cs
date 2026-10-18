using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Graph.Internal;
using Assimalign.Cohesion.Database.Graph.Language;
using Assimalign.Cohesion.Database.Tests;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphFunctionTests
{
    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: scalar semantics preserve exact values")]
    [InlineData("upper(lower('AbC'))", "ABC")]
    [InlineData("substring('A😀BC',1,2)", "😀B")]
    [InlineData("substring('A😀BC',2)", "BC")]
    [InlineData("substring('A😀BC',99)", "")]
    [InlineData("substring('A😀BC',1,0)", "")]
    [InlineData("substring('xy',0.9,1.9)", "x")]
    [InlineData("substring('xy',-0.5)", "xy")]
    [InlineData("replace('abcabc','ab','X')", "XcXc")]
    [InlineData("replace('abc','','X')", "XaXbXcX")]
    [InlineData("replace('','','X')", "X")]
    [InlineData("replace('abc','','')", "abc")]
    [InlineData("trim('  x  ')", "x")]
    [InlineData("char_length('A😀B')", 3L)]
    [InlineData("size('A😀B')", 3L)]
    [InlineData("size([NULL,1])", 2L)]
    [InlineData("round(2.5)", 3d)]
    [InlineData("round(-2.5)", -3d)]
    [InlineData("round(1.25,1)", 1.3d)]
    [InlineData("round(0.12345678901234566,16)", 0.1234567890123457d)]
    [InlineData("round(1e-100,99)", 0d)]
    [InlineData("round(1e-100,100)", 1e-100)]
    [InlineData("round(1.25,9223372036854775807)", 1.25d)]
    [InlineData("floor(9007199254740993)=9007199254740993", true)]
    [InlineData("ceil(9007199254740993)=9007199254740993", true)]
    [InlineData("mod(-9223372036854775808,-1)", 0L)]
    [InlineData("toInteger(2.5)", 2L)]
    [InlineData("toInteger(-2.5)", -2L)]
    [InlineData("toInteger(1e30)", long.MaxValue)]
    [InlineData("toInteger(-1e30)", long.MinValue)]
    [InlineData("toInteger(TRUE)", 1L)]
    [InlineData("toInteger('2.5')", 2L)]
    [InlineData("toInteger('9223372036854775807.0')", long.MaxValue)]
    [InlineData("toInteger('9007199254740993.5')", 9007199254740993L)]
    [InlineData("toInteger('1e-100')", 0L)]
    [InlineData("toInteger('0xff')", 255L)]
    [InlineData("toInteger('0o11')", 9L)]
    [InlineData("toInteger('1_000')", 1000L)]
    [InlineData("toFloat('0xff')", 255d)]
    [InlineData("toInteger('bad')", null)]
    [InlineData("toFloat('bad')", null)]
    [InlineData("toBoolean('bad')", null)]
    [InlineData("toBoolean(0)", false)]
    [InlineData("toBoolean(-2)", true)]
    [InlineData("toString(FALSE)", "false")]
    [InlineData("toString(2.0)", "2.0")]
    [InlineData("toString(1e7)", "1.0E7")]
    [InlineData("toString(1e6)", "1000000.0")]
    [InlineData("toString(1e-3)", "0.001")]
    [InlineData("toString(1e-4)", "1.0E-4")]
    [InlineData("toString(2.5e20)", "2.5E20")]
    [InlineData("toString(5e-324)", "4.9E-324")]
    [InlineData("toString(CAST('2026-10-18T00:00:00Z' AS ZONED DATETIME))", "2026-10-18T00:00:00Z")]
    [InlineData("CAST(FALSE AS STRING)", "FALSE")]
    [InlineData("CAST('42.0' AS INTEGER)", 42)]
    [InlineData("CAST('42' AS BIGINT)", 42L)]
    [InlineData("CAST('2.5' AS DOUBLE)", 2.5d)]
    [InlineData("CAST('2.5' AS REAL)", 2.5f)]
    [InlineData("CAST(CAST('1.0000000596046447753906250001' AS DECIMAL) AS REAL)", 1.0000001f)]
    [InlineData("CAST('1.0000000596046447753906250001' AS REAL)", 1.0000001f)]
    [InlineData("CAST(CAST('2026-10-18T00:00:00Z' AS ZONED DATETIME) AS STRING)", "2026-10-18 00:00:00+00:00")]
    [InlineData("CAST(CAST('2026-10-18T01:02:03.1234000+02:00' AS ZONED DATETIME) AS STRING)", "2026-10-18 01:02:03.1234+02:00")]
    [InlineData("CAST('0e-1000' AS DOUBLE)", 0d)]
    [InlineData("CAST('-0e-1000' AS REAL)", -0f)]
    [InlineData("CAST('5e-324' AS DOUBLE)", double.Epsilon)]
    [InlineData("CAST('1.4e-45' AS REAL)", float.Epsilon)]
    [InlineData("CAST(' true ' AS BOOLEAN)", true)]
    [InlineData("CAST(NULL AS INTEGER)", null)]
    [InlineData("substring(NULL,'bad',-1)", null)]
    [InlineData("mod(NULL,'bad')", null)]
    [InlineData("upper(NULL)", null)]
    public async Task Execute_ScalarFunctions_ShouldReturnDeclaredValuesAsync(string expression, object? expected)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "RETURN " + expression)).ShouldHaveSingleItem();
        // Assert
        row.ShouldHaveSingleItem().ShouldBe(expected);
        if (expected is not null) { row[0]!.GetType().ShouldBe(expected.GetType()); }
    }

    public static IEnumerable<object[]> NumericInputs()
    {
        foreach (var value in new object[] { (sbyte)-2, (short)-2, -2, -2L, -2f, -2d, -2m, (byte)2, (ushort)2, 2U, 2UL })
        { foreach (string function in new[] { "abs", "ceil", "floor", "round" }) { yield return [function, value]; } }
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: numeric type is preserved and unsigned inputs become Decimal")]
    [MemberData(nameof(NumericInputs))]
    public async Task Execute_NumericFunctions_ShouldRetainInputTypeAsync(string function, object value)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("numeric-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        object actual = (await RowsAsync(session, $"RETURN {function}($v)", new Dictionary<string, object?> { ["v"] = value })).ShouldHaveSingleItem()[0]!;
        // Assert
        actual.GetType().ShouldBe(value is byte or ushort or uint or ulong ? typeof(decimal) : value.GetType());
        Convert.ToDecimal(actual, CultureInfo.InvariantCulture).ShouldBe(function == "abs" || value is byte or ushort or uint or ulong ? 2m : -2m);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: domain, conversion and dynamic kind errors carry COHDBG013")]
    [InlineData("RETURN sqrt(-1)")]
    [InlineData("RETURN log(0)")]
    [InlineData("RETURN log(-1)")]
    [InlineData("RETURN log(1,2)")]
    [InlineData("RETURN log(-1,2)")]
    [InlineData("RETURN log(2,-1)")]
    [InlineData("RETURN ln(0)")]
    [InlineData("RETURN abs(-9223372036854775808)")]
    [InlineData("RETURN mod(1,0)")]
    [InlineData("RETURN substring('x',-1)")]
    [InlineData("RETURN substring('x',0,-1)")]
    [InlineData("RETURN substring('x',2147483648)")]
    [InlineData("RETURN substring('x',0,2147483648)")]
    [InlineData("RETURN round(1,-1)")]
    [InlineData("RETURN CAST('bad' AS INTEGER)")]
    [InlineData("RETURN CAST(2.5 AS INTEGER)")]
    [InlineData("RETURN CAST('2.5' AS BIGINT)")]
    [InlineData("RETURN CAST('1.00000000000000000000000000001' AS BIGINT)")]
    [InlineData("RETURN CAST(2147483648 AS INTEGER)")]
    [InlineData("RETURN toInteger('9223372036854775807.1')")]
    [InlineData("RETURN toInteger('9223372036854775808')")]
    [InlineData("RETURN CAST(1e-100 AS DECIMAL)")]
    [InlineData("RETURN CAST('1e-1000' AS DOUBLE)")]
    [InlineData("RETURN CAST('-1e-1000' AS DOUBLE)")]
    [InlineData("RETURN CAST('1e-1000' AS REAL)")]
    [InlineData("RETURN CAST('1e-100' AS REAL)")]
    [InlineData("RETURN CAST('-1e-100' AS REAL)")]
    [InlineData("RETURN CAST(1e-100 AS REAL)")]
    [InlineData("RETURN CAST(-1e-100 AS REAL)")]
    [InlineData("RETURN CAST('NaN' AS FLOAT)")]
    [InlineData("RETURN CAST('2026-02-30' AS DATE)")]
    [InlineData("RETURN CAST('2026-10-18T12:00:00.' AS ZONED DATETIME)")]
    [InlineData("RETURN CAST('2026-10-18T12:00:00+15:00' AS ZONED DATETIME)")]
    [InlineData("MATCH (a:Person) RETURN upper(a.age)")]
    [InlineData("MATCH (a:Person) RETURN labels(a.age)")]
    [InlineData("FOR x IN abs(1) RETURN x")]
    [InlineData("UNWIND abs(1) AS x RETURN x")]
    public async Task Execute_InvalidValue_ShouldReportStructuredValueDiagnosticAsync(string source)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("function-errors", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person {age:42})", cancellationToken: CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(source, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: exact numeric casts and replacement expansion retain bounds")]
    public async Task Execute_ExactCastAndReplacementBounds_ShouldPreservePrecisionAndRejectExpansionAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("function-bounds", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        (await RowsAsync(session, "RETURN CAST($v AS DECIMAL)", new Dictionary<string, object?> { ["v"] = 0.10000000000000002d })).ShouldHaveSingleItem()[0].ShouldBe(0.10000000000000002m);
        var parameters = new Dictionary<string, object?> { ["s"] = new string('a', 1000), ["r"] = new string('\0', 10000) };
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (:Unwritten) RETURN replace($s,'a',$r)", parameters, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
        error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (:Unwritten) RETURN replace($s,'',$r)", parameters, CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG013");
        (await RowsAsync(session, "MATCH (n:Unwritten) RETURN n")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: proven wrong kinds fail planning before mutation and under EXPLAIN")]
    [InlineData("labels(r)", "labels", "COHDBG003")]
    [InlineData("type(a)", "type", "COHDBG003")]
    [InlineData("labels(properties(a))", "labels", "COHDBG003")]
    [InlineData("CAST(a AS STRING)", "CAST", "COHDBG003")]
    [InlineData("CAST(missing AS INTEGER)", "CAST", "COHDBG001")]
    [InlineData("nodes(a)", "nodes", "COHDBG003")]
    [InlineData("startNode(a)", "StartNode", "COHDBG003")]
    [InlineData("endNode(a)", "EndNode", "COHDBG003")]
    [InlineData("upper(1)", "Upper", "COHDBG003")]
    [InlineData("labels(missing)", "labels", "COHDBG001")]
    [InlineData("upper(toString(abs(missing)))", "Abs", "COHDBG001")]
    [InlineData("upper(CAST(missing AS STRING))", "CAST", "COHDBG001")]
    [InlineData("edges(a)", "edges", "COHDBG003")]
    [InlineData("toBoolean(1.5)", "toBoolean", "COHDBG003")]
    [InlineData("toBoolean(CAST(1 AS DECIMAL))", "toBoolean", "COHDBG003")]
    [InlineData("CAST(TRUE AS INTEGER)", "CAST", "COHDBG003")]
    public async Task Execute_ProvenSignatureError_ShouldPlanBeforeWritesAsync(string expression, string function, string code)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("plan-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Person)-[:T]->(:Person)", cancellationToken: CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + $"MATCH (a)-[r]->(b) INSERT (:Unwritten) RETURN {expression}", cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe(code); error.Message.ShouldContain(function);
        }
        (await RowsAsync(session, "MATCH (n:Unwritten) RETURN n")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: hand-built calls validate arity and render through EXPLAIN")]
    public async Task Execute_PublicFunctionAst_ShouldValidateAndDescribeAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("ast-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var invalid = new GqlFunctionCallExpression(GqlScalarFunction.Abs, []);
        var statement = new GqlQueryStatement(new GqlQueryExpression([], [], [], false, [new GqlProjection(invalid)]));
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(new GraphQueryRequest(statement), CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001"); error.Message.ShouldContain("Abs");
        GraphProjectionText.Describe(new GqlFunctionCallExpression(GqlScalarFunction.Relationships, [new GqlVariableExpression("p")])).ShouldBe("edges(p)");
        GraphProjectionText.Describe(new GqlCastExpression(new GqlFunctionCallExpression(GqlScalarFunction.CharLength, [new GqlLiteralExpression("abc")]), GqlCastType.Int64))
            .ShouldBe("CAST(char_length('abc') AS BIGINT)");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: graph accessors preserve IDs, path order, maps and carried entity kinds")]
    public async Task Execute_ElementAndPathFunctions_ShouldPreserveGraphValuesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("element-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (a:Person {z:2,name:'Alice',age:42})-[:KNOWS]->(b:Person {name:'Bob'})-[:LIKES]->(:Person {name:'Carol'})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, "MATCH p=(a {name:'Carol'})<-[r:LIKES]-(b)<-[:KNOWS]-(c) RETURN nodes(p), edges(p), path_length(p), labels(a), keys(c), properties(c), element_id(c)")).ShouldHaveSingleItem();
        // Assert
        row[0].ShouldBeOfType<object?[]>().Cast<GraphNode>().Select(n => n.Properties["name"]).ShouldBe(["Carol", "Bob", "Alice"]);
        row[1].ShouldBeOfType<object?[]>().Cast<GraphRelationship>().Select(r => r.Type).ShouldBe(["LIKES", "KNOWS"]);
        row[6].ShouldBeOfType<string>();
        row[2].ShouldBe(2L); row[3].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { "Person" });
        row[4].ShouldBeOfType<object?[]>().ShouldBe(new object?[] { "age", "name", "z" });
        row[5].ShouldBeAssignableTo<IReadOnlyDictionary<string, object?>>()!["age"].ShouldBe(42L);
        (await RowsAsync(session, "MATCH (a {name:'Alice'}) RETURN element_id(a)")).ShouldHaveSingleItem()[0].ShouldBe(row[6]);
        (await RowsAsync(session, "MATCH ()-[r:KNOWS]->() LET s=startNode(r), m=properties(s) RETURN m.name, s.name")).ShouldHaveSingleItem().ShouldBe(new object?[] { "Alice", "Alice" });
        var paths = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH ()-[r:KNOWS]->() LET s=startNode(r) RETURN s"), CancellationToken.None);
        paths.Paths.ShouldHaveSingleItem().Nodes.ShouldHaveSingleItem().Properties["name"].ShouldBe("Alice");
        (await RowsAsync(session, "OPTIONAL MATCH (a:Absent) RETURN labels(a), properties(a), element_id(a)")).ShouldHaveSingleItem().ShouldAllBe(v => v == null);
        foreach (string argument in new[] { "r", "properties({group:r}).group" })
        {
            var groupError = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"MATCH (a)-[r:KNOWS*1..2]->(b) RETURN labels({argument})", cancellationToken: CancellationToken.None));
            groupError.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
        }
        (await RowsAsync(session, "MATCH (a)-[r:KNOWS*1..2]->(b) RETURN size(r)")).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: nested map calls defer unbound inputs and reject INSERT forward references")]
    public async Task Execute_FunctionPropertyMaps_ShouldUseIncomingBindingsAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("map-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:P {v:42}), (:P {v:-42})", cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH (a:P {v:abs(b.v)}), (b:P {v:-42}) RETURN a.v");
        // Assert
        rows.ShouldHaveSingleItem()[0].ShouldBe(42L);
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("INSERT (a:Unwritten {v:abs(b.v)}), (b:P {v:1})", cancellationToken: CancellationToken.None));
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG001");
        (await RowsAsync(session, "MATCH (n:Unwritten) RETURN n")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: carried composite and group kinds retain signature proofs")]
    [InlineData("LET x=[1] RETURN labels(x)")]
    [InlineData("LET x={k:1} RETURN labels(x)")]
    [InlineData("LET x=properties(a) RETURN type(x)")]
    [InlineData("LET x=42 RETURN labels(x)")]
    public async Task Execute_CarriedWrongKind_ShouldFailPlanningAsync(string stage)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("kind-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("MATCH (a) " + stage, cancellationToken: CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: parameter list kinds validate before snapshot materialization")]
    public async Task Execute_ListParameterKind_ShouldFailDuringPlanningAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("parameter-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        var parameters = new Dictionary<string, object?> { ["x"] = new List<long> { 1 } };
        // Act
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("EXPLAIN RETURN labels($x)", parameters, CancellationToken.None));
        // Assert
        error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003"); error.Message.ShouldContain("Labels");
        (await RowsAsync(session, "RETURN size($x)", parameters)).ShouldHaveSingleItem()[0].ShouldBe(1L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: function and CAST grouping keys compare structurally")]
    [InlineData("upper(a.name)")]
    [InlineData("CAST(a.name AS STRING)")]
    public async Task Execute_FunctionGroupingKey_ShouldMatchEquivalentExpressionAsync(string expression)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("group-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:P {name:'a'}), (:P {name:'a'})", cancellationToken: CancellationToken.None);
        // Act
        var row = (await RowsAsync(session, $"MATCH (a:P) RETURN {expression} AS k, count(*) AS n GROUP BY {expression} ORDER BY k")).ShouldHaveSingleItem();
        // Assert
        row[1].ShouldBe(2L);
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: the clock is captured once per transaction including ReadCommitted")]
    [InlineData(IsolationLevel.Snapshot)]
    [InlineData(IsolationLevel.ReadCommitted)]
    public async Task Execute_TransactionClock_ShouldShareCapturedUtcInstantAsync(IsolationLevel isolation)
    {
        // Arrange
        var clock = new AdvancingTimeProvider();
        await using var engine = GraphDatabaseEngine.Create("clock-functions", new() { TimeProvider = clock });
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert: each read advances; every row and stage must reuse the capture.
        var first = clock.NextUtcNow;
        long reads = clock.Reads;
        var rows = await RowsAsync(session, "FOR x IN [1,2] LET t=CURRENT_TIMESTAMP WITH t RETURN t NEXT LET u=CURRENT_TIMESTAMP RETURN t,u,CURRENT_DATE");
        rows.Count.ShouldBe(2);
        rows.ShouldAllBe(row => Equals(row[0], first) && Equals(row[1], first) && Equals(row[2], DateOnly.FromDateTime(first.UtcDateTime)));
        clock.Reads.ShouldBe(reads + 1);
        var next = clock.NextUtcNow;
        (await RowsAsync(session, "INSERT (:Day {day:toString(CURRENT_DATE)}) RETURN CURRENT_TIMESTAMP")).ShouldHaveSingleItem()[0].ShouldBe(next);
        var captured = clock.NextUtcNow;
        reads = clock.Reads;
        await using (var transaction = await session.BeginTransactionAsync(isolation, CancellationToken.None))
        {
            clock.Reads.ShouldBe(reads + 1);
            (await RowsAsync(session, "FOR x IN [1,2] RETURN CURRENT_TIMESTAMP,CURRENT_DATE")).ShouldAllBe(row => Equals(row[0], captured) && Equals(row[1], DateOnly.FromDateTime(captured.UtcDateTime)));
            (await RowsAsync(session, "RETURN CURRENT_TIMESTAMP")).ShouldHaveSingleItem()[0].ShouldBe(captured);
            clock.Reads.ShouldBe(reads + 1);
            await transaction.CommitAsync(CancellationToken.None);
        }
        next = clock.NextUtcNow;
        (await RowsAsync(session, "RETURN CURRENT_TIMESTAMP")).ShouldHaveSingleItem()[0].ShouldBe(next);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: temporal values sort and compare while property writes fail with a coded error")]
    public async Task Execute_TemporalValues_ShouldOrderAndRejectPropertiesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("temporal-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act
        var dates = await RowsAsync(session, "FOR s IN ['2026-10-19','2026-10-18'] RETURN CAST(s AS DATE) AS d ORDER BY d");
        var instants = await RowsAsync(session, "FOR s IN ['2026-10-18T01:00:00Z','2026-10-18T02:00:00+02:00'] RETURN CAST(s AS ZONED DATETIME) AS t ORDER BY t");
        // Assert
        dates.Select(row => row[0]).ShouldBe(new object?[] { new DateOnly(2026,10,18), new DateOnly(2026,10,19) });
        instants[0][0].ShouldBeOfType<DateTimeOffset>().UtcDateTime.Hour.ShouldBe(0);
        (await RowsAsync(session, "RETURN CAST('2026-10-18' AS DATE)<CAST('2026-10-19' AS DATE), CAST('2026-10-18T00:00Z' AS ZONED DATETIME)=CAST('2026-10-18T02:00+02:00' AS ZONED DATETIME)")).ShouldHaveSingleItem().ShouldBe(new object?[] { true, true });
        foreach (string value in new[] { "CURRENT_TIMESTAMP", "CURRENT_DATE", "CAST('2026-10-18' AS DATE)" })
        {
            var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync($"INSERT (:Unwritten {{at:{value}}})", cancellationToken: CancellationToken.None));
            error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
        }
        (await RowsAsync(session, "MATCH (n:Unwritten) RETURN n")).ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: path list elements remain mandatory through aliases")]
    [InlineData("nodes", false)]
    [InlineData("relationships", false)]
    [InlineData("edges", false)]
    [InlineData("nodes", true)]
    [InlineData("relationships", true)]
    [InlineData("edges", true)]
    public async Task ExecutePaths_PathListElements_ShouldKeepNonNullProofAsync(string function, bool alias)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("path-list-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:A)-[:T]->(:B)", cancellationToken: CancellationToken.None);
        string stages = alias ? $"LET xs={function}(p) WITH xs FOR n IN xs LET value=n RETURN value" : $"FOR n IN {function}(p) RETURN n";
        // Act
        var result = (GraphPathsQueryResult)await session.ExecuteAsync(GraphPathsQueryRequest.FromGql("MATCH p=()-[:T]->() " + stages), CancellationToken.None);
        // Assert
        result.Paths.Count.ShouldBe(function == "nodes" ? 2 : 1);
        result.Paths.ShouldAllBe(path => path.Nodes.Count == (function == "nodes" ? 1 : 2));
    }

    [Theory(DisplayName = "Cohesion Test [Graph] - Functions: proven temporal property kinds reject INSERT and MATCH before writes")]
    [InlineData("CURRENT_DATE")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("CAST('2026-10-18' AS DATE)")]
    [InlineData("t")]
    public async Task Execute_TemporalPropertyKind_ShouldFailPlanningWithoutDataAsync(string value)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("temporal-property-functions", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        // Act / Assert
        foreach (string prefix in new[] { "", "EXPLAIN " })
        {
            foreach (string clause in new[] { $"INSERT (:Unwritten {{at:{value}}})", $"INSERT (:Unwritten)-[:T {{at:{value}}}]->()", $"INSERT (:Unwritten) MATCH (n {{at:{value}}}) RETURN n", $"INSERT (:Unwritten) MATCH ()-[r {{at:{value}}}]->() RETURN r" })
            {
                var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync(prefix + "LET t=CURRENT_DATE " + clause, cancellationToken: CancellationToken.None));
                error.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG003");
            }
        }
        (await RowsAsync(session, "MATCH (n:Unwritten) RETURN n")).ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: CAST identifiers and quoted temporal bindings retain values")]
    public async Task Execute_FunctionWordBindings_ShouldReferenceBoundValuesAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("function-word-bindings", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT (:Movie {title:'Film'})", cancellationToken: CancellationToken.None);
        // Act / Assert
        (await RowsAsync(session, "MATCH (cast:Movie) RETURN cast.title")).ShouldHaveSingleItem()[0].ShouldBe("Film");
        foreach (string name in new[] { "CURRENT_DATE", "CURRENT_TIMESTAMP", "CURRENT_TIME", "LOCAL_TIME", "LOCAL_TIMESTAMP" })
        {
            (await RowsAsync(session, $"LET \"{name}\"=42 RETURN \"{name}\"")).ShouldHaveSingleItem()[0].ShouldBe(42L);
            (await RowsAsync(session, $"FOR \"{name}\" IN [42] RETURN \"{name}\"")).ShouldHaveSingleItem()[0].ShouldBe(42L);
        }
        var error = await Should.ThrowAsync<DatabaseException>(async () => await session.ExecuteAsync("RETURN CAST('bad' AS INTEGER)", cancellationToken: CancellationToken.None));
        error.Message.ShouldContain("INTEGER", Case.Sensitive);
        error.Message.ShouldNotContain("Int32", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Graph] - Functions: temporal families sort between paths and strings in ValueGroup order")]
    public async Task Execute_TemporalFamilies_ShouldFollowNeo4jOrderAsync()
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("temporal-family-order", new());
        var database = await engine.CreateDatabaseAsync("graph", CancellationToken.None);
        await using var session = await database.CreateSessionAsync(CancellationToken.None);
        await session.ExecuteAsync("INSERT ()-[:T]->()", cancellationToken: CancellationToken.None);
        // Act
        var rows = await RowsAsync(session, "MATCH p=()-[:T]->() FOR v IN [NULL,1,TRUE,'a',CURRENT_DATE,CURRENT_TIMESTAMP,p,[]] RETURN v ORDER BY v");
        var nested = await RowsAsync(session, "FOR v IN [[CURRENT_DATE],[CURRENT_TIMESTAMP],['a']] RETURN v ORDER BY v");
        // Assert
        rows.Count.ShouldBe(8);
        rows[0][0].ShouldBeOfType<object?[]>(); rows[1][0].ShouldBeOfType<GraphPath>();
        rows[2][0].ShouldBeOfType<DateTimeOffset>(); rows[3][0].ShouldBeOfType<DateOnly>();
        rows[4][0].ShouldBe("a"); rows[5][0].ShouldBe(true); rows[6][0].ShouldBe(1L); rows[7][0].ShouldBeNull();
        nested[0][0].ShouldBeOfType<object?[]>()[0].ShouldBeOfType<DateTimeOffset>();
        nested[1][0].ShouldBeOfType<object?[]>()[0].ShouldBeOfType<DateOnly>();
    }

    private static async Task<List<object?[]>> RowsAsync(GraphDatabaseSession session, string source, IReadOnlyDictionary<string, object?>? parameters = null)
    {
        await using var result = (DatabaseResultSet)await session.ExecuteAsync(source, parameters, CancellationToken.None);
        var rows = new List<object?[]>();
        await foreach (var row in result.GetRowsAsync(CancellationToken.None))
        { rows.Add(Enumerable.Range(0, result.Columns.Count).Select(row.GetValue).ToArray()); }
        return rows;
    }
}
