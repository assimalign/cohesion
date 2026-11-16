using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Storage.Tests;

public sealed class StorageCheckpointAttributionDocumentationTests
{
    [Fact(DisplayName = "Cohesion Test [Storage] - Checkpoint attribution retains final per-engine holder and peak measurements (#1283)")]
    public void Design_CheckpointAttribution_ShouldRecordEveryEngineAndFourTriggerBound()
    {
        // Arrange
        string[] design = File.ReadAllLines(DesignPath());
        string[] engines = ["SQL", "Documents", "Graph", "KeyValuePair", "Blob"];
        const string header = "| Engine | Deferred holders (count) | Busy refusals | Journal peak (bytes) | Peak / 65,536 | 4× bound (bytes) |";
        // Act / Assert: each measured row retains attribution, refusals and the enforced byte bound.
        int table = Array.IndexOf(design, header);
        table.ShouldBeGreaterThanOrEqualTo(0);
        var rows = design.Skip(table + 2).TakeWhile(line => line.StartsWith("| ", StringComparison.Ordinal)).ToArray();
        rows.Length.ShouldBe(engines.Length);
        foreach (string engine in engines)
        {
            string row = rows.Where(line => line.StartsWith($"| {engine} |", StringComparison.Ordinal)).ShouldHaveSingleItem();
            string[] cells = row.Split('|', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
            cells.Length.ShouldBe(6);
            cells[1].ShouldNotBeNullOrWhiteSpace();
            long.Parse(cells[2], CultureInfo.InvariantCulture).ShouldBeGreaterThanOrEqualTo(0);
            long peak = long.Parse(cells[3], NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            long bound = long.Parse(cells[5], NumberStyles.AllowThousands, CultureInfo.InvariantCulture);
            bound.ShouldBe(4 * 65536);
            peak.ShouldBeGreaterThan(0);
            peak.ShouldBeLessThanOrEqualTo(bound);
        }
    }

    private static string DesignPath([CallerFilePath] string sourcePath = "")
        => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(sourcePath)!, "..", "docs", "DESIGN.md"));
}
