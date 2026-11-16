using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Language.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlWindowCompatibilityMatrixTests
{
    private readonly ITestOutputHelper _output;
    public OqlWindowCompatibilityMatrixTests(ITestOutputHelper output) { _output = output; }

    [Fact(DisplayName = "Cohesion Test [OQL] - New words at every name site and clause follower preserve base acceptance (#1805)")]
    public void Parse_NameSiteFollowerMatrix_ShouldPreserveBaseAcceptance()
    {
        // Arrange: generated order and acceptance were recorded in the b5602544d copy.
        byte[] acceptance = OqlWindowFreeDifferentialTests.ReadBitmap("Matrix.bin.gz");
        using var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(OqlWindowFreeDifferentialTests.DataDirectory(), "Matrix.json")));
        var parser = new OqlQueryParser(true);
        var differences = new List<string>();
        int statements = 0, accepted = 0;
        // Act: inspect every difference, rather than stopping at the first regression.
        foreach (string text in OqlWindowCompatibilityMatrix.Statements())
        {
            bool required = (acceptance[statements / 8] & (1 << (statements % 8))) != 0;
            statements++;
            if (!required) { continue; }
            accepted++;
            var result = parser.Parse(text);
            if (result.Diagnostics.Any())
            { differences.Add(text + " => " + string.Join("; ", result.Diagnostics.Select(d => $"{d.Code}@{d.Start}: {d.Message}"))); }
        }
        // Assert
        _output.WriteLine($"Matrix statements={statements}; base-accepted={accepted}; differences={differences.Count}");
        foreach (string difference in differences) { _output.WriteLine(difference); }
        statements.ShouldBe(manifest.RootElement.GetProperty("Statements").GetInt32());
        accepted.ShouldBe(manifest.RootElement.GetProperty("Accepted").GetInt32());
        differences.ShouldBeEmpty();
    }
}
