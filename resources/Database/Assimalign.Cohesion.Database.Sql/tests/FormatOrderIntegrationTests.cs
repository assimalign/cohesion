using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Storage.Tests.TestObjects;

namespace Assimalign.Cohesion.Database.Sql.Tests;

[Collection(nameof(FormatProbeFixTests))]
public sealed class FormatOrderIntegrationTests
{
    [Theory(DisplayName = "Cohesion Test [Sql] - Older model and B-tree formats have one stable refusal without any file write (#1744, #1809, #1644)")]
    [InlineData(false, 33)]
    [InlineData(true, 33)]
    [InlineData(false, 37)]
    [InlineData(true, 37)]
    [InlineData(false, 38)]
    [InlineData(true, 38)]
    public async Task Open_OlderModelAndPageFormats_ShouldKeepRefusalOrderAndEveryByte(bool clean, int format)
    {
        // Arrange: build the image through the existing clean/crashed refusal fixture.
        string root = Path.Combine(Path.GetTempPath(), "cohesion-Sql-format-order-" + Guid.NewGuid().ToString("N"));
        try
        {
            await FormatProbeCrash.SeedAsync("Sql", root, legacy: true,
                (clean ? "clean-" : "") + (format == 33 ? "legacy-index2" : $"format{format}-index2"));
            if (!clean) { FormatProbeCrash.AssertUncommittedImages(root); }
            var before = FormatProbeCrash.Snapshot(root);
            // Act / Assert: repeat after disposal so a refusal cannot silently scrub/checkpoint.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                await using (var engine = SqlDatabaseEngine.Create("format-order", new() { RootPath = root }))
                {
                    var failure = await Should.ThrowAsync<DatabaseException>(async () =>
                        await engine.OpenDatabaseAsync("probe", CancellationToken.None));
                    failure.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CS06041");
                    failure.Message.ShouldContain($"uses data-storage format {format}", Case.Sensitive);
                    failure.Message.ShouldNotContain("CK06101", Case.Sensitive);
                }
                FormatProbeCrash.AssertUnchanged(root, before);
            }
        }
        finally { if (Directory.Exists(root)) { Directory.Delete(root, recursive: true); } }
    }
}
