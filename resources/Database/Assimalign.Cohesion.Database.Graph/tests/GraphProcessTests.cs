using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Transactions;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class GraphProcessTests
{
    [Fact(DisplayName = "Cohesion Test [Graph] - Recovery: committed graph and postings survive process exit with uncommitted creates, relabels and type postings (#1534, #1633)")]
    public async Task Recover_UncommittedGraphAndLabelPostings_ShouldRestoreCommittedStateAsync()
    {
        // Arrange
        string root = Path.Combine(Path.GetTempPath(), "cohesion-graph-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            // Act / Assert: the fixture checks label scans before the crash and after recovery.
            await Run("seed", root);
            await Run("verify", root);
            await AssertRecoveredTypesAsync(root);
            // The recovery checkpoint must itself leave a clean reusable file set.
            await Run("verify", root);
            await AssertRecoveredTypesAsync(root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task AssertRecoveredTypesAsync(string root)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await using var engine = GraphDatabaseEngine.Create("type-posting-recovery", new() { RootPath = root });
        var database = await engine.OpenDatabaseAsync("crash", timeout.Token);
        var reader = await database.Coordinator.BeginAsync(IsolationLevel.Snapshot, timeout.Token);
        try
        {
            var found = database.Store.ScanRelationshipTypes(["LINK", "PARTIAL"], reader.Snapshot, timeout.Token);
            var edge = found.ShouldHaveSingleItem();
            edge.Type.ShouldBe("LINK");
            database.Store.FindRelationship(edge.Id, reader.Snapshot).ShouldNotBeNull();
            database.Store.ScanRelationshipTypes(["PARTIAL"], reader.Snapshot, timeout.Token).ShouldBeEmpty();
            (await database.Store.GetIncidentAsync(edge.SourceId, reader.Snapshot, timeout.Token)).Select(item => item.Id).ShouldBe([edge.Id]);
        }
        finally { await database.Coordinator.RollbackAsync(reader, timeout.Token); }
    }

    private static async Task Run(string mode, string root)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "GraphRecoveryFixture", "Assimalign.Cohesion.Database.Graph.RecoveryFixture.dll");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(fixture);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(root);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start graph recovery fixture.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch
        {
            if (!process.HasExited) { process.Kill(entireProcessTree: true); }
            using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(cleanup.Token);
            throw;
        }
        process.ExitCode.ShouldBe(0, await output + await error);
    }
}
