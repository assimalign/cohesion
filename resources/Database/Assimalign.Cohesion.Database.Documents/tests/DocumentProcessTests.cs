using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentProcessTests
{
    [Fact]
    public async Task Committed_document_and_secondary_index_survive_process_exit_without_disposal()
    {
        string root = Path.Combine(Path.GetTempPath(), "cohesion-document-crash-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await Run("seed", root);
            await Run("verify", root);
            // The recovery checkpoint must itself leave a clean reusable file set.
            await Run("verify", root);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static async Task Run(string mode, string root)
    {
        string fixture = Path.Combine(AppContext.BaseDirectory, "DocumentRecoveryFixture", "Assimalign.Cohesion.Database.Documents.RecoveryFixture.dll");
        var info = new ProcessStartInfo("dotnet") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        info.ArgumentList.Add(fixture);
        info.ArgumentList.Add(mode);
        info.ArgumentList.Add(root);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start document recovery fixture.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        try { await process.WaitForExitAsync(timeout.Token); }
        catch { process.Kill(entireProcessTree: true); throw; }
        process.ExitCode.ShouldBe(0, await output + await error);
    }
}
