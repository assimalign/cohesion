using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlTempSecondPassTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cohesion-sql-temp-second-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) { Directory.Delete(_root, true); } }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Temp options: empty RootPath remains an in-memory engine")]
    public async Task Create_EmptyRootPath_ShouldKeepMemorySemantics()
    {
        // Arrange and act
        await using var engine = SqlDatabaseEngine.Create("empty-root", new() { RootPath = "" });
        var database = await engine.CreateDatabaseAsync("online", CancellationToken.None);

        // Assert
        engine.TempSpace.ShouldBeNull(); database.ShouldNotBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - Temp options: RootPath and database-named directory refusals explain their own requirements")]
    public void Create_InvalidPaths_ShouldExplainEachRequirement()
    {
        // Arrange and act
        var rootError = Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("bad-root", new() { RootPath = default(FileSystemPath) }));
        var tempError = Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("bad-temp", new() { RootPath = _root, TempDirectory = Path.Combine(_root, "spill") }));

        // Assert
        rootError.ParamName.ShouldBe("RootPath"); rootError.Message.ShouldNotContain("below RootPath", Case.Sensitive);
        tempError.Message.ShouldContain("first segment", Case.Sensitive); tempError.Message.ShouldContain("leading dot", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Temp availability: an overlong optional path does not prevent engine creation")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Create_OverlongTempPath_ShouldKeepEngineOnline(bool fileEngine)
    {
        // Arrange and act
        var options = new SqlDatabaseEngineOptions { TempDirectory = Path.Combine(_root, ".spill", new string('x', 40000)) };
        if (fileEngine) { options.RootPath = _root; }
        await using var engine = SqlDatabaseEngine.Create("long-temp", options);
        var database = await engine.CreateDatabaseAsync("online", CancellationToken.None);

        // Assert
        engine.TempSpace.ShouldNotBeNull(); database.ShouldNotBeNull();
        using var statement = engine.TempSpace.CreateStatement(8192);
        Should.Throw<Assimalign.Cohesion.Database.Storage.StorageTempException>(() => statement.CreateRun()).Code.ShouldBe("CK05005");
    }

    [WindowsFact(DisplayName = "Cohesion Test [Database.Sql] - Temp links: unprivileged middle-component junction refuses before startup")]
    public void Create_JunctionTempComponent_ShouldPreserveTarget()
    {
        // Arrange
        string target = Path.Combine(_root, ".target"); Directory.CreateDirectory(target);
        string victim = Path.Combine(target, "data"); File.WriteAllBytes(victim, [2, 7, 1]);
        string link = Path.Combine(_root, ".link");
        var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using var process = Process.Start(start)!;
        string output = process.StandardOutput.ReadToEnd(), error = process.StandardError.ReadToEnd();
        if (!process.WaitForExit(30000)) { process.Kill(true); process.WaitForExit(); throw new TimeoutException("junction creation"); }
        process.ExitCode.ShouldBe(0, output + error);
        try
        {
            // Act
            var refusal = Should.Throw<ArgumentOutOfRangeException>(() => SqlDatabaseEngine.Create("junction", new()
            { RootPath = _root, TempDirectory = Path.Combine(link, "spill") }));

            // Assert
            refusal.ParamName.ShouldBe("TempDirectory"); File.ReadAllBytes(victim).ShouldBe([2, 7, 1]); Directory.GetFiles(target).Length.ShouldBe(1);
        }
        finally { Directory.Delete(link); }
    }
}
