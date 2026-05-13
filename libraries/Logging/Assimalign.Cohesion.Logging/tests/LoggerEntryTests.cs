using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LoggerEntryTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntry: required arguments are stored")]
    public void Ctor_StoresRequiredArguments()
    {
        var entry = new LoggerEntry(LogLevel.Information, "Test.Category", "hello");

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Test.Category", entry.Category);
        Assert.Equal("hello", entry.Message);
        Assert.Null(entry.Exception);
        Assert.Null(entry.ParentId);
        Assert.NotEqual(LogId.Empty, entry.Id);
        Assert.Empty(entry.Attributes);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntry: timestamp defaults to UtcNow when omitted")]
    public void Ctor_TimestampDefaultsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var entry = new LoggerEntry(LogLevel.Information, "Test.Category", "hello");
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(entry.Timestamp, before, after);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntry: timestamp override is honored")]
    public void Ctor_TimestampOverrideHonored()
    {
        var ts = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero);
        var entry = new LoggerEntry(LogLevel.Information, "Test", "m", timestamp: ts);

        Assert.Equal(ts, entry.Timestamp);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntry: null message becomes empty string")]
    public void Ctor_NullMessageBecomesEmpty()
    {
        var entry = new LoggerEntry(LogLevel.Information, "Test", message: null);

        Assert.Equal(string.Empty, entry.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntry: empty category throws")]
    public void Ctor_EmptyCategory_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LoggerEntry(LogLevel.Information, "", "msg"));
        Assert.Throws<ArgumentNullException>(() => new LoggerEntry(LogLevel.Information, null!, "msg"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntry: parent id is stored")]
    public void Ctor_ParentId_Stored()
    {
        var parent = LogId.New();
        var entry = new LoggerEntry(LogLevel.Information, "Test", "m", parentId: parent);

        Assert.Equal(parent, entry.ParentId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntry: attributes round-trip")]
    public void Ctor_AttributesRoundTrip()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["userId"] = 42,
            ["status"] = "ok",
            ["nullKey"] = null,
        };

        var entry = new LoggerEntry(LogLevel.Warning, "Test", "m", attributes: attributes);

        Assert.Equal(3, entry.Attributes.Count);
        Assert.Equal(42, entry.Attributes["userId"]);
        Assert.Equal("ok", entry.Attributes["status"]);
        Assert.Null(entry.Attributes["nullKey"]);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LoggerEntry: exception is stored")]
    public void Ctor_Exception_Stored()
    {
        var exception = new InvalidOperationException("boom");
        var entry = new LoggerEntry(LogLevel.Error, "Test", "m", exception: exception);

        Assert.Same(exception, entry.Exception);
    }
}
