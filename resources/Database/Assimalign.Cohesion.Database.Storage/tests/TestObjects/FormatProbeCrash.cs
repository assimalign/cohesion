using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Assimalign.Cohesion.Database.Blob.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

// Linked into each engine test assembly. The seeder exits without unwinding its live brackets.
internal static class FormatProbeCrash
{
    internal static async Task SeedAsync(string model, string root, bool legacy, string? refusal = null)
    {
        var start = new ProcessStartInfo("dotnet")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add(Path.Combine(AppContext.BaseDirectory, "FormatProbeFixture", "Assimalign.Cohesion.Database.Storage.FormatProbeFixture.dll"));
        start.ArgumentList.Add(model); start.ArgumentList.Add(root); start.ArgumentList.Add(refusal ?? (legacy ? "legacy" : "current"));
        using var process = Process.Start(start) ?? throw new InvalidOperationException("Could not start format-probe fixture.");
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try { await process.WaitForExitAsync(timeout.Token); }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                using var cleanup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                await process.WaitForExitAsync(cleanup.Token);
            }
        }
        process.ExitCode.ShouldBe(0, await output + await error);
        (await output).ShouldContain("SEEDED", Case.Sensitive);
        foreach (string path in Directory.GetFiles(root, "*.dat", SearchOption.AllDirectories))
        {
            await CrashReopen.OpenAsync(path, _ =>
            {
                using var ready = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                return new ValueTask<bool>(true);
            }, timeout.Token);
        }
        foreach (string path in Directory.GetFiles(root, "*.log", SearchOption.AllDirectories))
        { new FileInfo(path).Length.ShouldBeGreaterThan(0); }
    }

    internal static void AssertUncommittedImages(string root)
    {
        foreach (string marker in Directory.GetFiles(root, "*.page", SearchOption.AllDirectories))
        {
            long pageId = long.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture);
            using var journal = StorageJournal.Create(new MemoryStream(File.ReadAllBytes(Path.ChangeExtension(marker, ".log"))));
            var records = journal.ReadAll().ToArray();
            var commits = records.Where(record => record.Type == JournalRecordType.CommitTransaction).Select(record => record.TransactionSequence).ToHashSet();
            records.ShouldContain(record => record.Type == JournalRecordType.FullPageImage && (long)record.PageId == pageId
                && !commits.Contains(record.TransactionSequence), marker);
            using var data = File.OpenRead(Path.ChangeExtension(marker, ".dat"));
            data.Position = pageId * Units.Page.Size + 512;
            data.ReadByte().ShouldBe(0x55);
        }
    }

    internal static Dictionary<string, byte[]> Snapshot(string root)
        => Directory.GetFiles(root, "*", SearchOption.AllDirectories).ToDictionary(path => path, File.ReadAllBytes);

    internal static void AssertUnchanged(string root, Dictionary<string, byte[]> before)
    {
        Directory.GetFiles(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ShouldBe(before.Keys.Order(StringComparer.Ordinal));
        foreach (var (path, bytes) in before) { File.ReadAllBytes(path).ShouldBe(bytes, path); }
    }

    internal static void TearPages(string root)
    {
        TearIndexRoots(root);
        foreach (string marker in Directory.GetFiles(root, "*.page", SearchOption.AllDirectories))
        {
            long page = long.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture);
            using var data = new FileStream(Path.ChangeExtension(marker, ".dat"), FileMode.Open, FileAccess.Write, FileShare.None);
            data.Position = page * Units.Page.Size + 512;
            data.Write(new byte[512]); data.Flush(flushToDisk: true);
        }
    }

    private static void TearIndexRoots(string root)
    {
        foreach (string path in Directory.GetFiles(root, "*.dat", SearchOption.AllDirectories))
        {
            using var data = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var page = new byte[Units.Page.Size];
            for (long offset = Units.Page.Size; offset + page.Length <= data.Length; offset += page.Length)
            {
                data.Position = offset; data.ReadExactly(page);
                if (page[21] != (byte)PageType.Index) { continue; }
                data.Position = offset; data.Write(new byte[512]);
            }
            data.Flush(flushToDisk: true);
        }
    }
    internal static void AssertConverged(string root)
    {
        foreach (string marker in Directory.GetFiles(root, "*.page", SearchOption.AllDirectories))
        {
            long page = long.Parse(File.ReadAllText(marker), CultureInfo.InvariantCulture);
            using var data = new FileStream(Path.ChangeExtension(marker, ".dat"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            data.Position = page * Units.Page.Size + 512;
            data.ReadByte().ShouldBe(0x43, marker);
        }
    }
}
