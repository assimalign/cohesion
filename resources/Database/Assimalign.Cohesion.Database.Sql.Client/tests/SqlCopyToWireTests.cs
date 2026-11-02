using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Connections;
using Assimalign.Cohesion.Connections.InMemory;
using Assimalign.Cohesion.Database.Client;
using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection("SQL COPY wire")]
public sealed class SqlCopyToWireTests
{
    private readonly ITestOutputHelper _output;
    public SqlCopyToWireTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: text and CSV complete and leave the connection reusable")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToAsync_BothFormats_ShouldStreamAndReuse(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        using var csv = new MemoryStream();
        using var text = new MemoryStream();
        // Act
        (await connection.CopyToAsync("COPY users(name,id) TO STDOUT (FORMAT csv, HEADER, FORCE_QUOTE *)", csv, timeout.Token)).ShouldBe(2);
        (await connection.CopyToAsync("COPY users TO STDOUT", text, timeout.Token)).ShouldBe(2);
        // Assert
        Encoding.UTF8.GetString(csv.ToArray()).ShouldBe("name,id\n\"ada\",\"1\"\n\"grace\",\"2\"\n");
        Encoding.UTF8.GetString(text.ToArray()).ShouldBe("1\tada\t100\n2\tgrace\t200\n");
        (await connection.ExecuteScalarAsync<long>(new SqlCommand("SELECT COUNT(*) FROM users"), cancellationToken: timeout.Token)).ShouldBe(2);
        connection.IsOpen.ShouldBeTrue();
        csv.CanWrite.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: coded refusals cross both transports and preserve connection and transaction")]
    [InlineData(false, "COPY users TO 'file'", "CK00001")]
    [InlineData(true, "COPY users TO PROGRAM 'cat'", "CK00001")]
    [InlineData(false, "COPY users TO STDOUT (FORMAT binary)", "CK00001")]
    [InlineData(true, "COPY (SELECT * FROM users) TO STDOUT", "CK00001")]
    [InlineData(false, "COPY users(id,id) TO STDOUT", "CS01071")]
    [InlineData(false, "SELECT 1", "CS05095")]
    [InlineData(true, "COPY users TO STDOUT", "CS04075")]
    public async Task CopyToAsync_Refusals_ShouldCarryDiagnostics(bool tcp, string sql, string code)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        if (code == "CS04075")
        {
            await connection.ExecuteAsync("BEGIN", cancellationToken: timeout.Token);
            await connection.ExecuteAsync("INSERT INTO users VALUES(3,'retained',300)", cancellationToken: timeout.Token);
        }
        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.CopyToAsync(sql, Stream.Null, timeout.Token));
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == code);
        error.ConnectionUsable.ShouldBeTrue();
        connection.IsOpen.ShouldBeTrue();
        (await connection.ExecuteScalarAsync<long>(new SqlCommand("SELECT COUNT(*) FROM users"), cancellationToken: timeout.Token)).ShouldBe(code == "CS04075" ? 3 : 2);
        if (code == "CS04075") { await connection.ExecuteAsync("ROLLBACK", cancellationToken: timeout.Token); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: large export uses a snapshot while concurrent writes complete")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToAsync_Backpressure_ShouldHideConcurrentWrites(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await using var writer = await harness.Client.ConnectAsync(timeout.Token);
        await SeedAsync(connection, 12000, timeout.Token);
        using var output = new SqlCopyWireGate();
        // Act
        var copying = connection.CopyToAsync("COPY large_export TO STDOUT (FORMAT csv)", output, timeout.Token).AsTask();
        try
        {
            await output.Entered.Task.WaitAsync(timeout.Token);
            harness.Engine.TryGetDatabase("app", out var database).ShouldBeTrue();
            Coordinator(database!).GetOpenContexts().Count.ShouldBe(1);
            (await writer.ExecuteAsync("UPDATE large_export SET id=id+20000", cancellationToken: timeout.Token)).ShouldBe(12000);
            Coordinator(database!).GetOpenContexts().Count.ShouldBe(1);
            await writer.ExecuteAsync("INSERT INTO large_export VALUES(99999,'new')", cancellationToken: timeout.Token);
        }
        finally { output.Release.TrySetResult(); }
        // Assert
        (await copying).ShouldBe(12000);
        string[] lines = Encoding.UTF8.GetString(output.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Length.ShouldBe(12000);
        lines.Select(line => int.Parse(line.AsSpan(0, line.IndexOf(',')))).Order().ShouldBe(Enumerable.Range(0, 12000));
        (await writer.ExecuteScalarAsync<long>(new SqlCommand("SELECT COUNT(*) FROM large_export"), cancellationToken: timeout.Token)).ShouldBe(12001);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: cancellation disconnect and destination failure join stream teardown")]
    [InlineData(false, "cancel")]
    [InlineData(true, "cancel")]
    [InlineData(false, "disconnect")]
    [InlineData(true, "disconnect")]
    [InlineData(false, "destination")]
    [InlineData(true, "destination")]
    public async Task CopyToAsync_EarlyExit_ShouldReleaseSession(bool tcp, string outcome)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(timeout.Token);
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        await SeedAsync(connection, 4000, timeout.Token);
        using var output = new SqlCopyWireGate();
        var copying = connection.CopyToAsync("COPY large_export TO STDOUT", output, cancellation.Token).AsTask();
        // Act
        try
        {
            await output.Entered.Task.WaitAsync(timeout.Token);
            if (outcome == "cancel") { cancellation.Cancel(); }
            else if (outcome == "disconnect") { await connection.AbortAsync(); }
            else { output.Fail = true; }
        }
        finally { output.Release.TrySetResult(); }
        // Assert
        if (outcome == "cancel") { await Should.ThrowAsync<OperationCanceledException>(async () => await copying); }
        else if (outcome == "destination") { await Should.ThrowAsync<IOException>(async () => await copying); }
        else { await Should.ThrowAsync<Exception>(async () => await copying); }
        connection.IsOpen.ShouldBeFalse();
        while (harness.Server.Sessions.Count != 0) { await Task.Delay(1, timeout.Token); }
        harness.Engine.TryGetDatabase("app", out var database).ShouldBeTrue();
        Coordinator(database!).GetOpenContexts().ShouldBeEmpty();
        await using var replacement = await harness.Client.ConnectAsync(timeout.Token);
        (await replacement.ExecuteScalarAsync<long>(new SqlCommand("SELECT COUNT(*) FROM large_export"), cancellationToken: timeout.Token)).ShouldBe(4000);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: allocation per row is constant at two sizes over three rounds")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToAsync_TwoSizes_ShouldKeepBoundedAllocation(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var connection = await harness.Client.ConnectAsync(timeout.Token);
        var measured = new List<double>();
        foreach (int size in new[] { 1000, 4000 })
        {
            await SeedAsync(connection, size, timeout.Token);
            await connection.CopyToAsync("COPY large_export TO STDOUT", Stream.Null, timeout.Token);
            long minimum = long.MaxValue;
            // Act
            for (int round = 0; round < 3; round++)
            {
                long before = GC.GetTotalAllocatedBytes(true);
                (await connection.CopyToAsync("COPY large_export TO STDOUT", Stream.Null, timeout.Token)).ShouldBe(size);
                minimum = Math.Min(minimum, GC.GetTotalAllocatedBytes(true) - before);
            }
            measured.Add((double)minimum / size);
            _output.WriteLine($"COPY {(tcp ? "TCP" : "InMemory")} {size}: {minimum} bytes, {measured[^1]:F3} bytes/row.");
            await connection.ExecuteAsync("DROP TABLE large_export", cancellationToken: timeout.Token);
        }
        // Assert
        measured[1].ShouldBeLessThan(measured[0] * 1.6);
        measured[1].ShouldBeLessThan(10000);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: an older client negotiates no stream and keeps ordinary execution working")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExecuteAsync_OlderClient_ShouldRefuseCopyAndKeepQueries(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        IDuplexConnectionFactory factory = tcp ? new Assimalign.Cohesion.Connections.Tcp.TcpConnectionFactory()
            : ((InMemoryConnectionListener)harness.Listener).CreateFactory();
        var legacy = new ProtocolMessageFamily("Sql", [ProtocolCapability.Diagnostics, ProtocolCapability.TransactionStatus], "DISCARD ALL", 5, 6, 7, 8, 9);
        await using var client = DatabaseClient.Create(new DatabaseClientOptions { Family = legacy, ConnectionFactory = factory,
            Settings = new DatabaseConnectionSettings { Database = "app", EndPoint = harness.Listener.EndPoint, Principal = "old" } });
        await using var core = await client.RentAsync(timeout.Token);
        await using var connection = new SqlConnection(core, null);
        // Act
        var local = await Should.ThrowAsync<SqlClientException>(async () => await connection.CopyToAsync("COPY users TO STDOUT", Stream.Null, timeout.Token));
        // Assert: no capability means no new frame is sent, preserving the older rental.
        local.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05096");
        connection.IsOpen.ShouldBeTrue();
        var error = await core.ExecuteAsync(new LegacyExecute(legacy, "COPY users TO STDOUT"), timeout.Token);
        error.ShouldContain("CS05096", Case.Sensitive);
        (await core.ExecuteAsync(new LegacyExecute(legacy, "SELECT 1"), timeout.Token)).ShouldBe("ok");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(SqlDatabase database);

    private static async Task SeedAsync(SqlConnection connection, int count, CancellationToken cancellationToken)
    {
        await connection.ExecuteAsync("CREATE TABLE large_export(id INT, v TEXT)", cancellationToken: cancellationToken);
        string text = new('x', 300);
        for (int start = 0; start < count; start += 500)
        {
            await connection.ExecuteAsync("INSERT INTO large_export VALUES " + string.Join(",", Enumerable.Range(start, Math.Min(500, count - start))
                .Select(i => "(" + i + ",'" + text + "')")), cancellationToken: cancellationToken);
        }
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Client] - COPY: an older server keeps ordinary commands working and refuses export locally")]
    public async Task CopyToAsync_OlderServer_ShouldRefuseBeforeSending()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var peer = new SqlCopyPeer("legacy");
        await using var connection = await peer.Client.ConnectAsync(timeout.Token);
        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.CopyToAsync("COPY t TO STDOUT", Stream.Null, timeout.Token));
        await connection.ExecuteAsync("SELECT 1", cancellationToken: timeout.Token);
        // Assert
        error.Diagnostics.ShouldContain(diagnostic => diagnostic.Code == "CS05096");
        connection.IsOpen.ShouldBeTrue();
        peer.Requests.ShouldBe(1);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: malformed or incomplete streams never certify success")]
    [InlineData("bad-start")]
    [InlineData("unexpected-ack")]
    [InlineData("oversize")]
    [InlineData("empty")]
    [InlineData("truncated")]
    [InlineData("bad-complete")]
    [InlineData("wrong-bytes")]
    public async Task CopyToAsync_MalformedPeer_ShouldDiscardConnection(string response)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var peer = new SqlCopyPeer(response);
        await using var connection = await peer.Client.ConnectAsync(timeout.Token);
        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.CopyToAsync("COPY t TO STDOUT", Stream.Null, timeout.Token));
        // Assert
        error.ConnectionUsable.ShouldBeFalse();
        connection.IsOpen.ShouldBeFalse();
        error.Message.ShouldContain("CK07005", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: a late statement error preserves its code and partial destination and discards pending acknowledgments")]
    [InlineData("error")]
    [InlineData("error-before-ack")]
    public async Task CopyToAsync_LateStatementFailure_ShouldPreserveTerminalError(string response)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var peer = new SqlCopyPeer(response);
        await using var connection = await peer.Client.ConnectAsync(timeout.Token);
        using var output = new MemoryStream();
        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.CopyToAsync("COPY t TO STDOUT", output, timeout.Token));
        // Assert
        error.Message.ShouldContain("CS05007", Case.Sensitive);
        Encoding.UTF8.GetString(output.ToArray()).ShouldBe("1\n");
        error.ConnectionUsable.ShouldBeFalse();
        connection.IsOpen.ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Client] - COPY: a broken error postamble preserves the original diagnostic and discards the rental")]
    public async Task CopyToAsync_BrokenErrorStatus_ShouldDiscardConnection()
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var peer = new SqlCopyPeer("bad-ready");
        await using var connection = await peer.Client.ConnectAsync(timeout.Token);
        // Act
        var error = await Should.ThrowAsync<SqlClientException>(async () => await connection.CopyToAsync("COPY t TO STDOUT", Stream.Null, timeout.Token));
        // Assert
        error.Message.ShouldContain("CS05007", Case.Sensitive);
        error.ConnectionUsable.ShouldBeFalse();
        connection.IsOpen.ShouldBeFalse();
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: invalid delivery acknowledgments release the server snapshot")]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task CopyToAsync_InvalidAcknowledgment_ShouldReleaseSnapshot(bool tcp, bool wrongType)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        IDuplexConnectionFactory factory = tcp ? new Assimalign.Cohesion.Connections.Tcp.TcpConnectionFactory()
            : ((InMemoryConnectionListener)harness.Listener).CreateFactory();
        await using var client = DatabaseClient.Create(new DatabaseClientOptions { Family = SqlProtocol.Family,
            ConnectionFactory = factory, Settings = new DatabaseConnectionSettings { Database = "app",
                EndPoint = harness.Listener.EndPoint, Principal = "bad-ack" } });
        await using var core = await client.RentAsync(timeout.Token);
        // Act
        var error = await Should.ThrowAsync<DatabaseClientException>(async () =>
            await core.ExecuteAsync(new BadAcknowledgment(wrongType), timeout.Token));
        // Assert
        error.Code.ShouldBe(ProtocolErrorCode.ProtocolViolation);
        error.Message.ShouldContain("CK07005", Case.Sensitive);
        while (harness.Server.Sessions.Count != 0) { await Task.Delay(1, timeout.Token); }
        harness.Engine.TryGetDatabase("app", out var database).ShouldBeTrue();
        Coordinator(database!).GetOpenContexts().ShouldBeEmpty();
    }

    private sealed class BadAcknowledgment : DatabaseProtocolExchange<string>
    {
        private readonly bool _wrongType;
        internal BadAcknowledgment(bool wrongType) : base(SqlProtocol.Family) { _wrongType = wrongType; }
        protected override async ValueTask<string> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)SqlProtocolMessageType.CopyTo,
                new ProtocolCopyToMessage("COPY users TO STDOUT").Encode()), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            (await reader.ReadFrameAsync(cancellationToken))!.Value.Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.CopyStart);
            (await reader.ReadFrameAsync(cancellationToken))!.Value.Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.CopyData);
            await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)(_wrongType ? SqlProtocolMessageType.CopyStart : SqlProtocolMessageType.CopyAcknowledged),
                _wrongType ? ReadOnlyMemory<byte>.Empty : new byte[] { 1 }), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            var frame = await reader.ReadFrameAsync(cancellationToken) ?? throw new IOException("Missing protocol refusal.");
            frame.Type.ShouldBe(ProtocolMessageType.Error);
            var error = ProtocolErrorMessage.Decode(frame.Payload.Span);
            throw new DatabaseClientException(error.Code, error.Message);
        }
    }
    private sealed class LegacyExecute : DatabaseProtocolExchange<string>
    {
        private readonly string _statement;
        internal LegacyExecute(ProtocolMessageFamily family, string statement) : base(family) { _statement = statement; }
        protected override async ValueTask<string> ExecuteCoreAsync(ProtocolFrameReader reader, ProtocolFrameWriter writer, CancellationToken cancellationToken)
        {
            await writer.WriteFrameAsync(new ProtocolFrame((ProtocolMessageType)5, ProtocolExecuteMessage.Create(_statement).Encode()), cancellationToken);
            await writer.FlushAsync(cancellationToken);
            while (await reader.ReadFrameAsync(cancellationToken) is { } frame)
            {
                if (frame.Type == ProtocolMessageType.Error) { return ProtocolErrorMessage.Decode(frame.Payload.Span).Message; }
                if ((byte)frame.Type == 8) { return "ok"; }
            }
            throw new IOException("Missing completion.");
        }
    }
}
