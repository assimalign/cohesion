#if !DEBUG
using System;
using System.IO;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlWindowFreeAllocationTests
{
    [Fact(DisplayName = "Cohesion Test [OQL] - Function lookup scans its immutable backing array without allocation (#1805)")]
    public void Find_NativeAndNullNames_ShouldAllocateNothing()
    {
        // Arrange
        OqlFunctions.Find(null!).ShouldBeNull();
        for (int i = 0; i < 1000; i++) { GC.KeepAlive(OqlFunctions.Find("upper")); }
        // Act
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { GC.KeepAlive(OqlFunctions.Find("upper")); }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        // Assert
        allocated.ShouldBe(0);
        OqlFunctions.Find("upper").ShouldNotBeNull().Name.ShouldBe("UPPER");
    }

    private readonly ITestOutputHelper _output;
    public OqlWindowFreeAllocationTests(ITestOutputHelper output) { _output = output; }

    [Theory(DisplayName = "Cohesion Test [OQL] - Window-free MERGE parsing keeps baseline allocation bytes (#1805)")]
    [InlineData("id")] [InlineData("over")]
    public void Parse_WindowFreeMerge_ShouldKeepExactAllocationBytes(string field)
    {
        // Arrange
        string text = "MERGE INTO orders o USING customers c ON KEY c." + field + " WHEN MATCHED THEN DELETE";
        var parser = new OqlQueryParser();
        parser.Parse(text).Diagnostics.ShouldBeEmpty();
        for (int i = 0; i < 100; i++) { GC.KeepAlive(parser.Parse(text)); }
        long minimum = long.MaxValue;
        // Act
        for (int round = 0; round < 5; round++)
        {
            long before = GC.GetAllocatedBytesForCurrentThread();
            for (int i = 0; i < 100; i++) { GC.KeepAlive(parser.Parse(text)); }
            minimum = Math.Min(minimum, GC.GetAllocatedBytesForCurrentThread() - before);
        }
        // Assert
        long bytes = minimum / 100;
        _output.WriteLine($"MERGE bytes={bytes}; architecture={System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture}");
        bytes.ShouldBe(2880L);
    }
    [Fact(DisplayName = "Cohesion Test [Database] - Window-free Children iterator retains base instance fields (#1805)")]
    public void Inspect_SharedIterator_ShouldKeepBaseFieldCount()
    {
        // Arrange
        using var stream = File.OpenRead(Path.Combine(AppContext.BaseDirectory, "Assimalign.Cohesion.Database.Documents.Language.dll"));
        using var pe = new PEReader(stream);
        var metadata = pe.GetMetadataReader();
        int matches = 0;
        // Act / Assert
        foreach (var handle in metadata.TypeDefinitions)
        {
            var type = metadata.GetTypeDefinition(handle);
            if (!metadata.GetString(type.Name).StartsWith("<Children>", StringComparison.Ordinal)) { continue; }
            _output.WriteLine($"Children fields={type.GetFields().Count}");
            type.GetFields().Count.ShouldBe(28);
            matches++;
        }
        matches.ShouldBe(1);
    }
}
#endif
