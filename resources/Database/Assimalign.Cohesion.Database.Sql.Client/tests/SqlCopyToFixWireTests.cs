using System;
using System.Diagnostics.Tracing;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Protocol;
using Assimalign.Cohesion.Database.Sql.Protocol;
using Assimalign.Cohesion.Database.Transactions;
using Assimalign.Cohesion.Testing;

namespace Assimalign.Cohesion.Database.Sql.Client.Tests;

[Collection("SQL COPY wire")]
public sealed class SqlCopyToFixWireTests
{
    private readonly ITestOutputHelper _output;
    public SqlCopyToFixWireTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: frames and acknowledgement round trips scale with bytes")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToAsync_ShortRows_ShouldBatchFramesByBytes(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var seed = await harness.Client.ConnectAsync(timeout.Token);
        await using var probe = await SqlCopyWireProbe.OpenAsync(harness, tcp, timeout.Token);
        var measurements = new System.Collections.Generic.List<(int Frames, int Expected)>();
        foreach (int size in new[] { 1000, 10000, 16384 })
        {
            bool exactBuffers = size == 16384;
            string Value(int i) => i == 8191 ? "é" : i == 16383 ? "" : "x";
            await seed.ExecuteAsync("CREATE TABLE t" + size + (exactBuffers ? "(v TEXT)" : "(id INT)"), cancellationToken: timeout.Token);
            await seed.ExecuteAsync("INSERT INTO t" + size + " VALUES " + string.Join(",", Enumerable.Range(0, size).Select(i => exactBuffers ? "('" + Value(i) + "')" : "(" + i + ")")), cancellationToken: timeout.Token);
            byte[] expected = Encoding.UTF8.GetBytes(string.Concat(Enumerable.Range(0, size).Select(i => exactBuffers ? Value(i) + "\n" : i + "\n")));
            using var output = new MemoryStream();
            int frames = 0, acknowledgments = 0;
            // Act
            await probe.SendAsync((ProtocolMessageType)SqlProtocolMessageType.CopyTo, new ProtocolCopyToMessage("COPY t" + size + " TO STDOUT").Encode(), timeout.Token);
            (await probe.ReadAsync(timeout.Token)).Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.CopyStart);
            ProtocolCopyCompleteMessage complete;
            while (true)
            {
                var frame = await probe.ReadAsync(timeout.Token);
                if (frame.Type == (ProtocolMessageType)SqlProtocolMessageType.CopyComplete)
                {
                    complete = ProtocolCopyCompleteMessage.Decode(frame.Payload.Span);
                    break;
                }
                frame.Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.CopyData);
                frame.Payload.Length.ShouldBeInRange(1, 16 * 1024);
                frames++;
                await output.WriteAsync(frame.Payload, timeout.Token);
                await probe.SendAsync((ProtocolMessageType)SqlProtocolMessageType.CopyAcknowledged, ReadOnlyMemory<byte>.Empty, timeout.Token);
                acknowledgments++;
            }
            _output.WriteLine($"{(tcp ? "TCP" : "InMemory")} rows={size}, bytes={output.Length}, data frames={frames}, acknowledgement round trips={acknowledgments}, total server frames={frames + 2}.");
            // Assert
            output.ToArray().ShouldBe(expected);
            complete.RowCount.ShouldBe(size);
            complete.ByteCount.ShouldBe(expected.Length);
            acknowledgments.ShouldBe(frames);
            measurements.Add((frames, (expected.Length + 16383) / 16384));
        }
        foreach (var measurement in measurements) { measurement.Frames.ShouldBe(measurement.Expected); }
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: blank frame text is a reusable syntax refusal")]
    [InlineData(false, "")]
    [InlineData(true, " \r\n\t")]
    public async Task CopyToAsync_BlankFrame_ShouldReportSyntaxAndKeepSession(bool tcp, string statement)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var probe = await SqlCopyWireProbe.OpenAsync(harness, tcp, timeout.Token);
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Sql"), EventLevel.Verbose);
        // Act
        await probe.SendAsync((ProtocolMessageType)SqlProtocolMessageType.CopyTo, new ProtocolCopyToMessage(statement).Encode(), timeout.Token);
        var diagnostics = await probe.ReadAsync(timeout.Token);
        // Assert
        diagnostics.Type.ShouldBe(ProtocolMessageType.Diagnostics);
        ProtocolDiagnosticsMessage.Decode(diagnostics.Payload.Span).Entries.ShouldContain(entry => entry.Code == "CS00003");
        var frame = await probe.ReadAsync(timeout.Token);
        frame.Type.ShouldBe(ProtocolMessageType.Error);
        ProtocolErrorMessage.Decode(frame.Payload.Span).Code.ShouldBe(ProtocolErrorCode.ParseFailure);
        await probe.SendAsync(ProtocolMessageType.Ping, ReadOnlyMemory<byte>.Empty, timeout.Token);
        (await probe.ReadAsync(timeout.Token)).Type.ShouldBe(ProtocolMessageType.Pong);
        harness.Server.Sessions.Count.ShouldBe(1);
        events.Events.ShouldNotContain(entry => entry.EventName == "SessionFaulted");
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Client] - COPY: EOF awaiting acknowledgement is a peer close")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CopyToAsync_PeerClosesOutput_ShouldLogPeerClose(bool tcp)
    {
        // Arrange
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var harness = await SqlClientTestHarness.StartAsync(tcp: tcp);
        await using var probe = await SqlCopyWireProbe.OpenAsync(harness, tcp, timeout.Token);
        var session = harness.Server.Sessions.ShouldHaveSingleItem();
        using var events = new EventSourceRecorder(EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Database.Sql"), EventLevel.Verbose);
        await probe.SendAsync((ProtocolMessageType)SqlProtocolMessageType.CopyTo, new ProtocolCopyToMessage("COPY users TO STDOUT").Encode(), timeout.Token);
        (await probe.ReadAsync(timeout.Token)).Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.CopyStart);
        (await probe.ReadAsync(timeout.Token)).Type.ShouldBe((ProtocolMessageType)SqlProtocolMessageType.CopyData);
        // Act: half close at a frame boundary, without cancelling the server lifetime.
        await probe.CloseOutputAsync();
        while (!events.Events.Any(entry => entry.EventName == "SessionClosed" && Equals(entry.Payload![0], session.Id)))
        { await Task.Delay(1, timeout.Token); }
        // Assert
        events.Events.Single(entry => entry.EventName == "SessionClosed" && Equals(entry.Payload![0], session.Id)).Payload![1].ShouldBe("PeerClosed");
        events.Events.ShouldNotContain(entry => entry.EventName == "SessionFaulted" || entry.EventName == "SessionProtocolViolation");
        harness.Server.Sessions.ShouldBeEmpty();
        harness.Engine.TryGetDatabase("app", out var database).ShouldBeTrue();
        // The server retains its session reference after teardown; the owned session is closed.
        session.DatabaseSession.ShouldNotBeNull().State.ShouldBe(SessionState.Closed);
        Coordinator(database!).GetOpenContexts().ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Client] - COPY: XML contract preserves arbitrary destination exceptions")]
    public void CopyToAsync_XmlContract_ShouldDescribeOriginalException()
    {
        // Arrange
        string path = SourceFile();
        var lines = File.ReadLines(path).SkipWhile(line => !line.Contains("/// <summary>", StringComparison.Ordinal))
            .TakeWhile(line => !line.Contains("public async ValueTask", StringComparison.Ordinal))
            .Where(line => line.TrimStart().StartsWith("///", StringComparison.Ordinal)).Select(line => line[(line.IndexOf("///", StringComparison.Ordinal) + 3)..]);
        // Act
        var contract = System.Xml.Linq.XElement.Parse("<member>" + string.Join("\n", lines) + "</member>");
        // Assert
        string failure = contract.Elements("exception").Single(element => (string?)element.Attribute("cref") == "Exception").Value;
        failure.ShouldContain("original exception", Case.Sensitive);
        failure.ShouldContain("discarded", Case.Sensitive);
    }

    private static string SourceFile()
    {
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "src", "SqlConnection.Copy.cs");
            if (File.Exists(path)) { return path; }
        }
        throw new InvalidOperationException("COPY public contract source was not found.");
    }

    [UnsafeAccessor(UnsafeAccessorKind.Method, Name = "get_Coordinator")]
    private static extern TransactionCoordinator Coordinator(SqlDatabase database);
}
