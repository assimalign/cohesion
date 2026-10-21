using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Indexing;
using Assimalign.Cohesion.Database.Storage;

namespace Assimalign.Cohesion.Database.Graph.Tests;

public sealed class IndexFailureTranslationTests
{
    [Theory(DisplayName = "Cohesion Test [Database.Graph] - Index boundary: wrapped damage retains corruption precedence and model diagnostics")]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TranslateFailure_IndexCorruption_ShouldRetainCommonAndModelCodes(bool wrapped)
    {
        // Arrange
        await using var engine = GraphDatabaseEngine.Create("index-failures", new());
        var database = await engine.CreateDatabaseAsync("test", CancellationToken.None);
        var cause = new IndexCorruptionException("index", 42, 0);
        Exception error = wrapped ? new AggregateException(new InvalidOperationException("Other branch."), new Exception("Wrapped index.", cause)) : cause;

        // Act
        var translated = database.TranslateFailure(error).ShouldBeOfType<DatabaseCorruptionException>();

        // Assert
        translated.Diagnostics.Select(d => d.Code).ShouldBe(["COHDBS003", "COHDBI002", "COHDBG006"]);
        translated.InnerException.ShouldBeSameAs(cause);
        translated.PageId.ShouldBe((PageId)42);
        translated.Message.ShouldNotStartWith("COHDBS003:", Case.Sensitive);
        database.TranslateFailure(translated).ShouldBeSameAs(translated);
        var coded = new DatabaseException("Coded wrapper.", [new Types.Diagnostic
        { Code = "COHDBG006", Message = "Coded wrapper.", Severity = Types.DiagnosticSeverity.Error }], cause);
        database.TranslateFailure(coded).ShouldBeOfType<DatabaseCorruptionException>().InnerException.ShouldBeSameAs(cause);
        var aggregate = new DatabaseException("Aggregate wrapper.", coded.Diagnostics,
            new AggregateException(coded, new StorageCorruptionException((PageId)43, "Damaged storage page.")));
        database.TranslateFailure(aggregate).ShouldBeOfType<DatabaseCorruptionException>().PageId.ShouldBe((PageId)43);
        var ordinary = database.TranslateFailure(new Exception("Wrapper.", new IndexException("Index failed."))).ShouldBeOfType<DatabaseException>();
        ordinary.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG006");
        var storage = database.TranslateFailure(new StorageIOException("Device failed.")).ShouldBeOfType<DatabaseException>();
        storage.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("COHDBG006");
        storage.Message.ShouldBe("Device failed.");
    }
}
