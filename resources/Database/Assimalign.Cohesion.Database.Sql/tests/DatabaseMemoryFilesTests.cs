using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The in-memory files an engine keeps for its in-memory databases (#1272; owner decision 33 of
/// 2026-10-06), compiled into the SQL assembly from the root's shared source: an open copies the
/// closed files' bytes into new streams, refuses files whose storage has not closed, and an
/// engine's disposal releases every file set.
/// </summary>
public sealed class DatabaseMemoryFilesTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - In-memory files: an open copies the closed files' bytes into new streams")]
    public void TryOpen_AfterTheFilesClosed_ShouldCopyTheirBytesIntoNewStreams()
    {
        // Arrange: a file set written and closed, as a storage leaves it.
        var files = new DatabaseMemoryFiles();
        files.TryCreate("db", out var data, out var journal, out var backup).ShouldBeTrue();
        data.Write([1, 2, 3]);
        journal.Write([4, 5]);
        data.Dispose();
        journal.Dispose();
        backup.Dispose();

        // Act
        bool opened = files.TryOpen("db", out var reopenedData, out var reopenedJournal, out var reopenedBackup);
        var dataBytes = new byte[(int)reopenedData.Length];
        reopenedData.ReadExactly(dataBytes);
        var journalBytes = new byte[(int)reopenedJournal.Length];
        reopenedJournal.ReadExactly(journalBytes);
        reopenedData.Write([6]);

        // Assert: the bytes, at the start of new, writable streams, and the closed ones untouched.
        opened.ShouldBeTrue();
        dataBytes.ShouldBe(new byte[] { 1, 2, 3 });
        journalBytes.ShouldBe(new byte[] { 4, 5 });
        reopenedBackup.Length.ShouldBe(0);
        reopenedData.Length.ShouldBe(4);
        data.CanRead.ShouldBeFalse();
        reopenedBackup.Dispose();
        reopenedJournal.Dispose();
        reopenedData.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - In-memory files: an open refuses files whose storage has not closed")]
    public void TryOpen_WhileTheFilesAreOpen_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var files = new DatabaseMemoryFiles();
        files.TryCreate("db", out var data, out var journal, out var backup).ShouldBeTrue();

        // Act
        var refused = Should.Throw<InvalidOperationException>(() => files.TryOpen("db", out _, out _, out _));

        // Assert
        refused.Message.ShouldContain("'db' is still open");
        backup.Dispose();
        journal.Dispose();
        data.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - In-memory files: Clear releases every file set")]
    public void Clear_WithFileSets_ShouldReleaseEveryFileSet()
    {
        // Arrange
        var files = new DatabaseMemoryFiles();
        files.TryCreate("a", out var data, out var journal, out var backup).ShouldBeTrue();
        files.TryCreate("b", out var otherData, out var otherJournal, out var otherBackup).ShouldBeTrue();
        foreach (var stream in new[] { data, journal, backup, otherData, otherJournal, otherBackup })
        {
            stream.Dispose();
        }

        // Act
        files.Clear();

        // Assert
        files.Names.ShouldBeEmpty();
        files.Exists("a").ShouldBeFalse();
        files.TryOpen("b", out _, out _, out _).ShouldBeFalse();
    }
}
