using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database;
using Assimalign.Cohesion.Database.Sql;

namespace Assimalign.Cohesion.Sdk.Database.Tests;

public sealed class MigrationAlterColumnGoldenTests
{
    [Fact(DisplayName = "Cohesion Test [Sdk.Database] - Migration SQL: engine statements and refusals match the golden corpus")]
    public void Generate_MigrationCorpus_ShouldMatchExpectedText()
    {
        // Arrange: framework ArgumentException suffixes must not depend on the runner's locale.
        var previousCulture = CultureInfo.CurrentCulture;
        var previousUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;
            CultureInfo.CurrentUICulture = CultureInfo.InvariantCulture;

            // Act / Assert
            CompareCorpus();
        }
        finally
        {
            CultureInfo.CurrentCulture = previousCulture;
            CultureInfo.CurrentUICulture = previousUiCulture;
        }
    }

    private static void CompareCorpus()
    {
        // Expected text is public engine output. Generate new shapes from a build without the
        // change under test, then run this test against that build and the changed build.
        var actual = new StringBuilder();
        var plans = MigrationAlterColumnCases.All().Select(item => (item.Name, Plan: MigrationAlterColumnCases.Plan(item.Previous, item.Desired)))
            .Concat(MigrationAlterColumnCases.RawPlans()).Concat(MigrationAlterColumnCases.DdlPlans());
        foreach (var item in plans)
        {
            actual.Append(item.Name).Append('\n');
            try
            {
                // Act: script generation is metadata-only; the same output serves empty/populated,
                // inline/deferred typed application. Execution routes are covered separately.
                var script = SqlMigrationScriptGenerator.Generate(item.Plan);
                foreach (var step in script.Steps)
                {
                    actual.Append(step.StatementText).Append('\n').Append(step.RollbackStatementText ?? "<irreversible>").Append('\n');
                }
                actual.Append("Reversible: ").Append(script.IsReversible).Append('\n');
            }
            catch (DatabaseException refusal)
            {
                actual.Append("Refused: ").Append(refusal.Message).Append('\n');
            }
        }

        // Assert
        actual.ToString().ShouldBe(File.ReadAllText(GoldenPath()).Replace("\r\n", "\n"));
    }

    private static string GoldenPath([CallerFilePath] string source = "")
        => Path.Combine(Path.GetDirectoryName(source)!, "TestObjects", "MigrationAlterColumn.expected.txt");
}
