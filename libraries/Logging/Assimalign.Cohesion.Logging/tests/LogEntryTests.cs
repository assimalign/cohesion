using System;
using System.Collections.Generic;
using Assimalign.Cohesion.Logging;

namespace Assimalign.Cohesion.Logging.Tests;

public class LogEntryTests
{
    [Fact(DisplayName = "Cohesion Test [Logging] - LogEntry: required arguments are stored")]
    public void Ctor_StoresRequiredArguments()
    {
        var entry = new LogEntry(LogLevel.Information, "Test.Category", "hello");

        Assert.Equal(LogLevel.Information, entry.Level);
        Assert.Equal("Test.Category", entry.Category);
        Assert.Equal("hello", entry.Message);
        Assert.Null(entry.Exception);
        Assert.Null(entry.ParentId);
        Assert.NotEqual(LogId.Empty, entry.Id);
        Assert.Empty(entry.Attributes);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LogEntry: timestamp defaults to UtcNow when omitted")]
    public void Ctor_TimestampDefaultsToUtcNow()
    {
        var before = DateTimeOffset.UtcNow;
        var entry = new LogEntry(LogLevel.Information, "Test.Category", "hello");
        var after = DateTimeOffset.UtcNow;

        Assert.InRange(entry.Timestamp, before, after);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LogEntry: timestamp override is honored")]
    public void Ctor_TimestampOverrideHonored()
    {
        var ts = new DateTimeOffset(2026, 5, 13, 12, 0, 0, TimeSpan.Zero);
        var entry = new LogEntry(LogLevel.Information, "Test", "m", timestamp: ts);

        Assert.Equal(ts, entry.Timestamp);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LogEntry: null message becomes empty string")]
    public void Ctor_NullMessageBecomesEmpty()
    {
        var entry = new LogEntry(LogLevel.Information, "Test", message: null);

        Assert.Equal(string.Empty, entry.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LogEntry: empty category throws")]
    public void Ctor_EmptyCategory_Throws()
    {
        Assert.Throws<ArgumentException>(() => new LogEntry(LogLevel.Information, "", "msg"));
        Assert.Throws<ArgumentNullException>(() => new LogEntry(LogLevel.Information, null!, "msg"));
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LogEntry: parent id is stored")]
    public void Ctor_ParentId_Stored()
    {
        var parent = LogId.New();
        var entry = new LogEntry(LogLevel.Information, "Test", "m", parentId: parent);

        Assert.Equal(parent, entry.ParentId);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LogEntry: attributes round-trip")]
    public void Ctor_AttributesRoundTrip()
    {
        var attributes = new Dictionary<string, object?>
        {
            ["userId"] = 42,
            ["status"] = "ok",
            ["nullKey"] = null,
        };

        var entry = new LogEntry(LogLevel.Warning, "Test", "m", attributes: attributes);

        Assert.Equal(3, entry.Attributes.Count);
        Assert.Equal(42, entry.Attributes["userId"]);
        Assert.Equal("ok", entry.Attributes["status"]);
        Assert.Null(entry.Attributes["nullKey"]);
    }

    [Fact(DisplayName = "Cohesion Test [Logging] - LogEntry: exception is stored")]
    public void Ctor_Exception_Stored()
    {
        var exception = new InvalidOperationException("boom");
        var entry = new LogEntry(LogLevel.Error, "Test", "m", exception: exception);

        Assert.Same(exception, entry.Exception);
    }
}
