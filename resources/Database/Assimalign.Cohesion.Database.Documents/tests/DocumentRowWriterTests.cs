using System;
using System.Runtime.InteropServices;
using System.Text.Json;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Database.Documents.Internal;
using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Types;

namespace Assimalign.Cohesion.Database.Documents.Tests;

public sealed class DocumentRowWriterTests
{
    private readonly ITestOutputHelper _output;

    public DocumentRowWriterTests(ITestOutputHelper output) => _output = output;

    [Theory(DisplayName = "Cohesion Test [Documents] - Encoding 32 MiB of rows reuses bounded backing storage")]
    [InlineData(false)]
    [InlineData(true)]
    public void Write_LargeRepeatedRows_ShouldReuseBufferWithinMemoryBudget(bool wholeDocument)
    {
        // Arrange: measure the actual backing array, independently of GC and transport caches.
        const int rowCount = 128;
        string text = new('x', 256 * 1024);
        using var document = JsonDocument.Parse("{\"text\":\"" + text + "\"}");
        using var encoder = new DocumentRowWriter();
        var row = new DocumentQueryResult.DocumentQueryRow([wholeDocument ? document.RootElement : text], default);
        DatabaseColumn[] columns = [new() { Name = "text", Ordinal = 0, Type = wholeDocument ? DatabaseType.Json : DatabaseType.String }];
        byte[]? backing = null;
        long encodedBytes = 0;
        int peakBufferBytes = 0;
        int encodedRows = 0;

        // Act
        for (int index = 0; index < rowCount; index++)
        {
            ReadOnlyMemory<byte> payload = encoder.Write(row, columns, wholeDocument);
            MemoryMarshal.TryGetArray(payload, out var segment).ShouldBeTrue();
            var current = segment.Array.ShouldNotBeNull();
            if (backing is not null) { ReferenceEquals(current, backing).ShouldBeTrue(); }
            backing = current;
            peakBufferBytes = Math.Max(peakBufferBytes, current.Length);
            encodedBytes += payload.Length;
            encodedRows++;
        }

        // Assert
        _output.WriteLine($"Rows={encodedRows}; encoded bytes={encodedBytes}; peak backing buffer={peakBufferBytes} bytes.");
        encodedRows.ShouldBe(rowCount);
        encodedBytes.ShouldBeGreaterThan(32L * 1024 * 1024);
        peakBufferBytes.ShouldBeLessThan(8 * 1024 * 1024);
    }
}
