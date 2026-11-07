using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public sealed class SqlDialectDocumentationTests
{
    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Batch diagnostics distinguish target/deadline refusals, paused health and retired schema codes (#1792, #1283, #1778)")]
    public void Documentation_BatchDiagnostics_ShouldDistinguishActiveAndRetiredCodes()
    {
        // Arrange
        string dialect = ReadDialect();
        // Act / Assert
        dialect.ShouldContain("| `CS05097` | Error |", Case.Sensitive);
        dialect.ShouldContain("| `CK04014` | Error |", Case.Sensitive);
        dialect.ShouldContain("Degraded health with CK06006", Case.Sensitive);
        dialect.ShouldContain("CS03104 is retired", Case.Sensitive);
        dialect.ShouldNotContain("| `CS03104` | Error |", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Joint deferred docs: every job publishes once, failures retain the old schema and default-only and inline paths have joint parity")]
    public void Documentation_DeferredPublication_ShouldDescribeJointBoundary()
    {
        // Arrange
        string dialect = ReadDialect();
        string design = ReadDocumentation("Sql", "DESIGN.md");

        // Act / Assert
        dialect.ShouldContain("Every deferred plan publishes its", Case.Sensitive);
        // #1778: only a declared different target replans; a no-target open has
        // no declaration to report against, and successful Verify clears the report.
        dialect.ShouldContain("different-target Build discards it and replans", Case.Sensitive);
        dialect.ShouldContain("A no-target open discards without a provisioning report or trace", Case.Sensitive);
        dialect.ShouldContain("successful Verify clears a declared discard observation", Case.Sensitive);
        dialect.ShouldContain("Statements naming a new table in the plan also wait before binding", Case.Sensitive);
        dialect.ShouldContain("Default-only changes publish jointly", Case.Sensitive);
        dialect.ShouldContain("ApplySchemaAsync use the same joint executor inline", Case.Sensitive);
        design.ShouldContain("SQL record-space format 37 requires this joint kind-9 meaning", Case.Sensitive);
        design.ShouldContain("prepared target plus untouched objects", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Joint Build documentation: scripts run inline and untouched availability means DML")]
    public void Documentation_JointBuildClaims_ShouldDescribeScriptsAndDml()
    {
        // Arrange
        string design = ReadDocumentation("Sql", "DESIGN.md");

        // Act / Assert
        design.ShouldNotContain("Neither Build nor open scans data.", Case.Sensitive);
        design.ShouldContain("any script force the typed migration inline", Case.Sensitive);
        design.ShouldContain("DML on untouched tables remains available", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Recursive docs: bounded-state consumers run until cancellation (#1719)")]
    public void Documentation_RecursiveConsumers_ShouldDescribeCancellation()
    {
        // Arrange
        string client = ReadDocumentation("Sql.Client", "DESIGN.md");

        // Act / Assert
        client.ShouldNotContain("unbounded blocking input refuses with CS05067", Case.Sensitive);
        client.ShouldContain("bounded-state consumers run until cancellation", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Recursive docs: RECURSIVE is a profile keyword refused before MERGE (#1719)")]
    public void Documentation_RecursiveKeyword_ShouldDescribeMergeRestriction()
    {
        // Arrange
        string disposition = ReadDialect().Split("**Keyword disposition (#1101).**")[1].Split("A recognized construct is skipped")[0];

        // Act / Assert
        disposition.ShouldNotContain("`CK00001`: `RECURSIVE`", Case.Sensitive);
        disposition.ShouldContain("RECURSIVE is a profile keyword", Case.Sensitive);
        disposition.ShouldContain("only before MERGE", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Recursive scope: documentation describes executed fixpoints consistently (#1719)")]
    [InlineData("Sql.Language", "DESIGN.md", "and recursive execution")]
    [InlineData("Sql", "DESIGN.md", "nested CTEs, recursive execution")]
    [InlineData("Sql", "OVERVIEW.md", "CS05089")]
    [InlineData("Sql.Language", "DIALECT.md", "CS05089")]
    public void Documentation_RecursiveScope_ShouldDescribeExecution(string project, string file, string obsolete)
    {
        // Arrange
        string documentation = ReadDocumentation(project, file);

        // Act / Assert
        documentation.ShouldNotContain(obsolete, Case.Sensitive);
        documentation.ShouldContain("fixpoint", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Dialect: ON UPDATE describes executed actions and direct RETURNING (#1723)")]
    public void Dialect_UpdateActions_ShouldDescribeExecutedBehavior()
    {
        // Arrange
        string opening = ReadDialect().Split("## Window stage")[0];

        // Act / Assert
        opening.ShouldNotContain("does not execute update actions", Case.Sensitive);
        opening.ShouldContain("execute CASCADE, SET NULL", Case.Sensitive);
        opening.ShouldContain("RETURNING reports the statement's own row image", Case.Sensitive);
        opening.ShouldContain("same parent row and constraint replaces", Case.Sensitive);
    }

    [Theory(DisplayName = "Cohesion Test [Sql.Language] - Dialect: format history retains the version that introduced each record (#1723)")]
    [InlineData("Sql.Language", "DIALECT.md", "Compiled-schema v11 was unchanged by SQ-A3.")]
    [InlineData("Sql.Language", "DIALECT.md", "schema v11 and JSONB storage bytes were unchanged.")]
    [InlineData("Sql.Language", "DESIGN.md", "grammar, AST and compiled-schema v11 were unchanged.")]
    [InlineData("Sql", "DESIGN.md", "Compiled-schema v11, dense row encoding,")]
    [InlineData("Sql", "DESIGN.md", "compiled documents advanced to **cohesion/database-schema/v6** in #1437")]
    [InlineData("Sql", "DESIGN.md", "cohesion/database-schema/v7` in #1108, matching Sdk.Database.")]
    [InlineData("Sql.Language", "DIALECT.md", "(TRUNCATE restart). SQL format 29 refuses format 28 and earlier")]
    [InlineData("Sql.Language", "DIALECT.md", "Format 29 added owned-sequence metadata to format 28's shared sequence records")]
    [InlineData("Sql.Language", "DIALECT.md", "abandoned owners. SQL remains format 29; existing kind-14 reclamation records suffice.")]
    [InlineData("Sql.Language", "DIALECT.md", "Compiled-schema v8 was unchanged by SQ-A.")]
    [InlineData("Sql", "DESIGN.md", "SQL remains format 29. Failed preparation and crash recovery use kind 14 unchanged.")]
    [InlineData("Sql", "DESIGN.md", "SQL stays format 29: ordinary table records and kind 14")]
    [InlineData("Sql.Language", "DIALECT.md", "Format 36 introduced ASC NULLS FIRST as the default key order (#1787). Current")]
    [InlineData("Sql.Language", "DIALECT.md", "Format 37 requires joint publication for every deferred")]
    [InlineData("Sql", "DESIGN.md", "current format 37 and mandatory index")]
    [InlineData("Sql", "DESIGN.md", "Current format 37 also requires extension 8's OnUpdate byte")]
    [InlineData("Indexing", "DESIGN.md", "B-tree page format 2, but this engine supports only format 3")]
    [InlineData("Graph", "DESIGN.md", "catalog v3 and B-tree page format 2 therefore reports only CK06101")]
    [InlineData("Graph", "DESIGN.md", "and roots, and catalog v4 records including")]
    [InlineData("Sql.Language", "DIALECT.md", "additive indexes and CHECK, NOT NULL, UNIQUE and foreign-key constraint")]
    [InlineData("Sql", "DESIGN.md", "SQL remains format 29: ordinary dense table records")]
    public void Documentation_FormatHistory_ShouldDistinguishHistoricalAndCurrentVersions(string project, string file, string expected)
    {
        // Arrange
        string documentation = ReadDocumentation(project, file);

        // Act / Assert
        documentation.ShouldContain(expected, Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Sql.Language] - Dialect: clause totals and count explanation include FILTER (#1720)")]
    public void Dialect_DeclaredClauseTotals_ShouldIncludeFilter()
    {
        // Arrange
        string dialect = ReadDialect();
        int supported = SqlLanguageProfile.Instance.Clauses.Count();
        const int exclusions = 1;

        // Act
        string matrix = dialect.Split("## Statement matrix")[1].Split("\n## ")[0];

        // Assert
        matrix.ShouldContain($"**{supported} of {supported + exclusions} declared clauses**", Case.Sensitive);
        matrix.Split("Phase 22")[0].ShouldContain("FILTER (#1720)", Case.Sensitive);
        dialect.ShouldContain($"These contextual exclusions do not change the {supported}/{supported + exclusions} declared-clause count.", Case.Sensitive);
    }

    private static string ReadDialect([CallerFilePath] string source = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "docs", "DIALECT.md"));

    private static string ReadDocumentation(string project, string file, [CallerFilePath] string source = "")
        => File.ReadAllText(Path.Combine(Path.GetDirectoryName(source)!, "..", "..", $"Assimalign.Cohesion.Database.{project}", "docs", file));
}
