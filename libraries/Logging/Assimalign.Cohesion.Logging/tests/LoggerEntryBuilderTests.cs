using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerEntryBuilderTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntryBuilder: builds with required pieces")]
    public void Build_MinimumValues()
    {
        var entry = new LoggerEntryBuilder(LogLevel.Warning, "Category").Build();

        Assert.Equal(LogLevel.Warning, entry.Level);
        Assert.Equal("Category", entry.Category);
        Assert.Equal(string.Empty, entry.Message);
        Assert.Empty(entry.Attributes);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntryBuilder: fluent setters propagate")]
    public void Build_FluentSettersPropagate()
    {
        var ts = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var parent = LogId.New();
        var id = LogId.New();
        var exception = new InvalidOperationException("boom");

        var entry = new LoggerEntryBuilder(LogLevel.Information, "A")
            .WithLevel(LogLevel.Critical)
            .WithCategory("B")
            .WithMessage("hello")
            .WithException(exception)
            .WithParentId(parent)
            .WithId(id)
            .WithTimestamp(ts)
            .WithAttribute("k", 1)
            .WithAttribute("k2", "v")
            .Build();

        Assert.Equal(LogLevel.Critical, entry.Level);
        Assert.Equal("B", entry.Category);
        Assert.Equal("hello", entry.Message);
        Assert.Same(exception, entry.Exception);
        Assert.Equal(parent, entry.ParentId);
        Assert.Equal(id, entry.Id);
        Assert.Equal(ts, entry.Timestamp);
        Assert.Equal(2, entry.Attributes.Count);
        Assert.Equal(1, entry.Attributes["k"]);
        Assert.Equal("v", entry.Attributes["k2"]);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntryBuilder: WithAttributes merges in batch")]
    public void Build_WithAttributesBatch()
    {
        var entry = new LoggerEntryBuilder(LogLevel.Information, "A")
            .WithAttribute("k", 1)
            .WithAttributes(new[]
            {
                new KeyValuePair<string, object?>("k", 2), // overwrite
                new KeyValuePair<string, object?>("k2", 3),
            })
            .Build();

        Assert.Equal(2, entry.Attributes["k"]);
        Assert.Equal(3, entry.Attributes["k2"]);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntryBuilder: rejects null/empty arguments")]
    public void Ctor_RejectsBadArguments()
    {
        Assert.Throws<ArgumentNullException>(() => new LoggerEntryBuilder(LogLevel.Information, null!));
        Assert.Throws<ArgumentException>(() => new LoggerEntryBuilder(LogLevel.Information, ""));
        Assert.Throws<ArgumentException>(() => new LoggerEntryBuilder(LogLevel.Information, "Cat").WithCategory(""));
        Assert.Throws<ArgumentException>(() => new LoggerEntryBuilder(LogLevel.Information, "Cat").WithAttribute("", 1));
        Assert.Throws<ArgumentNullException>(() => new LoggerEntryBuilder(LogLevel.Information, "Cat").WithAttributes(null!));
    }
}
