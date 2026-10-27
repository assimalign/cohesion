using System.Linq;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Protocol;

namespace Assimalign.Cohesion.Database.Graph.Client.Tests;

public sealed class GraphDiagnosticOverflowTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph.Client] - 4097 warnings preserve scalar and path success and session reuse")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task QueryAsync_OverflowWarnings_ShouldRetainPrefixNoticeAndSession(bool tcp)
    {
        // Arrange
        await using var harness = await GraphClientTestHarness.StartAsync(tcp);
        await using var connection = await harness.Client.ConnectAsync(harness.Token);
        var session = harness.Server.Sessions.Single();
        string match = "MATCH (n:" + string.Join(":", Enumerable.Range(0, 4097).Select(index => "Missing" + index)) + ") RETURN ";
        // Act
        var scalar = await connection.QueryAsync(match + "n.name", cancellationToken: harness.Token);
        var paths = connection.QueryPathsAsync(match + "n", cancellationToken: harness.Token);
        int count = 0;
        await foreach (var path in paths) { count++; }
        // Assert
        scalar.Count.ShouldBe(0);
        count.ShouldBe(0);
        foreach (var diagnostics in new[] { scalar.Diagnostics, paths.Diagnostics })
        {
            diagnostics.Count.ShouldBe(4096);
            diagnostics.Take(4095).ShouldAllBe(item => item.Code == "CG01010");
            diagnostics[^1].Code.ShouldBe("CK07001");
            diagnostics[^1].Message!.ShouldContain("first 4095 of 4097", Case.Sensitive);
            diagnostics[^1].Message!.ShouldContain("remaining 2", Case.Sensitive);
            new ProtocolDiagnosticsMessage(diagnostics).Encode().Length.ShouldBeLessThanOrEqualTo(ProtocolDiagnosticsMessage.MaxPayloadLength);
        }
        connection.IsOpen.ShouldBeTrue();
        await connection.QueryAsync("SHOW LABELS", cancellationToken: harness.Token);
        harness.Server.Sessions.ShouldHaveSingleItem().ShouldBeSameAs(session);
    }
}
