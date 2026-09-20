using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Database.Sql.Storage;
using Assimalign.Cohesion.Database.Types;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Catalog.Tests;

/// <summary>Verifies durable collation metadata and the safe database-default boundary.</summary>
public sealed class SqlCatalogCollationTests
{
    /// <summary>Explicit and inherited collations survive column alteration and recovery.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Collation: defaults and column overrides survive restart")]
    public async Task Collation_AfterColumnChangesAndRestart_ShouldPreserveDefaultAndOverrides()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal),
            new MemoryStream(), "collation");
        SqlCatalog.Open(storage).DefaultCollation.ShouldBe(Collation.Binary);
        var catalog = SqlCatalog.Open(storage, Collation.CaseInsensitive);
        catalog.DefaultCollation.ShouldBe(Collation.CaseInsensitive);

        // Act
        await catalog.CreateTableAsync("dbo", "people",
            [new("name", new(DatabaseType.String)),
             new("exact", new(DatabaseType.String), collation: Collation.Binary),
             new("legacy", new(DatabaseType.String), collation: Collation.Invariant)],
            cancellationToken: CancellationToken.None);
        await catalog.AddColumnAsync("dbo", "people",
            new("accent", new(DatabaseType.String), collation: Collation.CaseAccentInsensitive), CancellationToken.None);
        await catalog.DropColumnAsync("dbo", "people", "legacy", CancellationToken.None);
        using var reopenedStorage = SqlStorage.Open(new MemoryStream(data.ToArray()),
            new MemoryStream(journal.ToArray()), new MemoryStream());
        var reopened = SqlCatalog.Open(reopenedStorage);

        // Assert
        reopened.DefaultCollation.ShouldBe(Collation.CaseInsensitive);
        reopened.TryGetTable("dbo", "people", out var table).ShouldBeTrue();
        table.FindColumn("name")!.Collation.ShouldBeNull();
        table.FindColumn("exact")!.Collation.ShouldBe(Collation.Binary);
        table.FindColumn("accent")!.Collation.ShouldBe(Collation.CaseAccentInsensitive);
        table.FindColumn("legacy").ShouldBeNull();
        SqlCatalog.CaptureSnapshot(reopened).DefaultCollation.ShouldBe(Collation.CaseInsensitive);
    }

    /// <summary>Existing indexes cannot silently acquire a different inherited collation.</summary>
    /// <returns>The asynchronous test operation.</returns>
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Collation: reopening a populated database under a different default is rejected")]
    public async Task Open_WithDifferentDefaultAfterTableCreation_ShouldRejectChangeAndAllowSameDefault()
    {
        // Arrange
        using var data = new MemoryStream();
        using var journal = new MemoryStream();
        using var storage = SqlStorage.Create(new NonClosingStream(data), new NonClosingStream(journal),
            new MemoryStream(), "collation");
        var catalog = SqlCatalog.Open(storage);
        await catalog.CreateTableAsync("dbo", "people", [new("name", new(DatabaseType.String))],
            cancellationToken: CancellationToken.None);

        // Act / Assert
        using var sameStorage = SqlStorage.Open(new MemoryStream(data.ToArray()),
            new MemoryStream(journal.ToArray()), new MemoryStream());
        SqlCatalog.Open(sameStorage, Collation.Binary).DefaultCollation.ShouldBe(Collation.Binary);

        using var changedStorage = SqlStorage.Open(new MemoryStream(data.ToArray()),
            new MemoryStream(journal.ToArray()), new MemoryStream());
        var exception = Should.Throw<SqlCatalogException>(() =>
            SqlCatalog.Open(changedStorage, Collation.CaseInsensitive));
        exception.Message.ShouldContain("existing index keys");
    }

    /// <summary>Pre-collation catalog records retain binary behavior.</summary>
    [Theory(DisplayName = "Cohesion Test [Sql.Catalog] - Collation: legacy table formats inherit Binary")]
    [InlineData(false)]
    [InlineData(true)]
    public void Open_LegacyTableFormats_ShouldInheritBinary(bool hasConstraintExtension)
    {
        // Arrange
        using var storage = SqlStorage.Create(new MemoryStream(), new MemoryStream(), new MemoryStream(), "legacy");
        var writer = new DatabaseKeyWriter();
        writer.AppendInt32(1).AppendInt64(1).AppendString("dbo", Collation.Binary)
            .AppendString("people", Collation.Binary).AppendInt32(1)
            .AppendString("name", Collation.Binary).AppendInt8((sbyte)DatabaseType.String)
            .AppendInt32(-1).AppendInt32(-1).AppendInt32(-1).AppendBoolean(true)
            .AppendNull().AppendInt32(0);
        if (hasConstraintExtension)
        {
            writer.AppendInt8((sbyte)DatabaseObjectOwner.Adhoc).AppendNull().AppendInt32(1).AppendInt32(0);
        }
        using (var transaction = storage.BeginTransaction())
        {
            storage.InsertRow(transaction, writer.ToArray());
            transaction.Commit();
        }

        // Act
        var catalog = SqlCatalog.Open(storage);

        // Assert
        catalog.DefaultCollation.ShouldBe(Collation.Binary);
        catalog.TryGetTable("dbo", "people", out var table).ShouldBeTrue();
        table.Columns[0].Collation.ShouldBeNull();
    }

    /// <summary>A collation is meaningful only on a string column.</summary>
    [Fact(DisplayName = "Cohesion Test [Sql.Catalog] - Collation: non-string overrides are rejected")]
    public void Column_WithNonStringCollation_ShouldReject()
    {
        Should.Throw<ArgumentException>(() =>
            new SqlCatalogColumn("id", new(DatabaseType.Int32), collation: Collation.CaseInsensitive));
    }
}
