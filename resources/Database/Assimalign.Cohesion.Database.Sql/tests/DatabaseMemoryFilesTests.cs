using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Tests;

/// <summary>
/// The in-memory files an engine keeps for its in-memory databases (#1272; owner decision 33 of
/// 2026-10-06), compiled into the SQL assembly from the root's shared source: an open copies the
/// closed files' bytes into new streams, refuses files whose storage has not closed, and an
/// engine's disposal releases every file set. Since #1392 a file set is keyed by its database's
/// lower-case storage name and the set's name, as a database directory holds it on disk.
/// </summary>
public sealed class DatabaseMemoryFilesTests
{
    private const string Set = "sql";

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - In-memory files: an open copies the closed files' bytes into new streams")]
    public void TryOpen_AfterTheFilesClosed_ShouldCopyTheirBytesIntoNewStreams()
    {
        // Arrange: a file set written and closed, as a storage leaves it.
        var files = new DatabaseMemoryFiles();
        files.TryCreate("db", Set, out var data, out var journal, out var backup).ShouldBeTrue();
        data.Write([1, 2, 3]);
        journal.Write([4, 5]);
        data.Dispose();
        journal.Dispose();
        backup.Dispose();

        // Act
        bool opened = files.TryOpen("db", Set, out var reopenedData, out var reopenedJournal, out var reopenedBackup);
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
        files.TryCreate("db", Set, out var data, out var journal, out var backup).ShouldBeTrue();

        // Act
        var refused = Should.Throw<InvalidOperationException>(() => files.TryOpen("db", Set, out _, out _, out _));

        // Assert: the message names the file set as its path in the database's directory reads.
        refused.Message.ShouldContain("'db/sql' is still open", Case.Sensitive);
        backup.Dispose();
        journal.Dispose();
        data.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - In-memory files: Clear releases every file set")]
    public void Clear_WithFileSets_ShouldReleaseEveryFileSet()
    {
        // Arrange
        var files = new DatabaseMemoryFiles();
        files.TryCreate("a", Set, out var data, out var journal, out var backup).ShouldBeTrue();
        files.TryCreate("b", Set, out var otherData, out var otherJournal, out var otherBackup).ShouldBeTrue();
        foreach (var stream in new[] { data, journal, backup, otherData, otherJournal, otherBackup })
        {
            stream.Dispose();
        }

        // Act
        files.Clear();

        // Assert
        files.Names.ShouldBeEmpty();
        files.Exists("a", Set).ShouldBeFalse();
        files.TryOpen("b", Set, out _, out _, out _).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - In-memory files: every spelling of a name reaches the one file set, and the names are the storage names (#1392)")]
    public void TryCreate_CaseVariantOfAName_ShouldReachTheSameFileSet()
    {
        // Arrange
        var files = new DatabaseMemoryFiles();
        files.TryCreate("Sales", Set, out var data, out var journal, out var backup).ShouldBeTrue();
        data.Write([7]);
        foreach (var stream in new[] { data, journal, backup })
        {
            stream.Dispose();
        }

        // Act
        bool createdAgain = files.TryCreate("SALES", Set, out _, out _, out _);
        bool exists = files.Exists("sales", Set);
        bool opened = files.TryOpen("sAlEs", Set, out var reopenedData, out var reopenedJournal, out var reopenedBackup);

        // Assert
        createdAgain.ShouldBeFalse();
        exists.ShouldBeTrue();
        opened.ShouldBeTrue();
        reopenedData.Length.ShouldBe(1);
        files.Names.ShouldBe(["sales"]);
        reopenedBackup.Dispose();
        reopenedJournal.Dispose();
        reopenedData.Dispose();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - In-memory files: a database's catalog set is never a database named for it, and a drop takes every set of the database alone (#1392)")]
    public void Drop_DatabaseWithACatalogBesideADatabaseNamedForIt_ShouldDropOnlyThatDatabase()
    {
        // Arrange: 'sales' with a data and a catalog set, and a database literally named
        // 'sales.catalog', whose files were the catalog of 'sales' before #1392.
        var files = new DatabaseMemoryFiles();
        files.TryCreate("sales", Set, out var a, out var b, out var c).ShouldBeTrue();
        files.TryCreate("sales", DatabaseFileNames.CatalogFileSet, out var d, out var e, out var f).ShouldBeTrue();
        bool separate = files.TryCreate("sales.catalog", Set, out var g, out var h, out var i);
        foreach (var stream in new[] { a, b, c, d, e, f, g, h, i })
        {
            stream?.Dispose();
        }

        // Act
        bool dropped = files.Drop("SALES");

        // Assert
        separate.ShouldBeTrue();
        dropped.ShouldBeTrue();
        files.Exists("sales", Set).ShouldBeFalse();
        files.Exists("sales", DatabaseFileNames.CatalogFileSet).ShouldBeFalse();
        files.Exists("sales.catalog", Set).ShouldBeTrue();
        files.Names.ShouldBe(["sales.catalog"]);
        files.Drop("sales").ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Database.Sql] - In-memory files: a name that breaks the database-name rule is refused (#1392)")]
    public void TryCreate_InvalidName_ShouldBeRefused()
    {
        // Arrange
        var files = new DatabaseMemoryFiles();

        // Act
        var refusal = Should.Throw<ArgumentException>(() => files.TryCreate("a/b", Set, out _, out _, out _));

        // Assert
        refusal.Message.ShouldStartWith(DatabaseFileNames.Rule, Case.Sensitive);
        files.Names.ShouldBeEmpty();
    }
}
