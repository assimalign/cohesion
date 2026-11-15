using System;
using System.Linq;
using System.Threading;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Sql.Internal;
using Assimalign.Cohesion.Database.Sql.Language;
using Assimalign.Cohesion.Database.Sql.Schema.Internal;
using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Database.Types;
using Assimalign.Cohesion.Sdk.Database.Tests;

namespace Assimalign.Cohesion.Database.Sql.Tests;

public sealed class SqlMigrationLoweringTests
{
    [Fact(DisplayName = "Cohesion Test [Database.Sql] - ALTER types: all built-ins and corpus declarations match catalog resolution")]
    public void ResolveAlterColumnType_DeclaredSpellings_ShouldMatchCatalogRules()
    {
        // Arrange: use the golden corpus's declarations so new spellings enter this check too.
        var columns = SqlType.BuiltIn.Select(type => new CompiledSchemaColumn("Value", type.Storage.Type, true, CustomType: type.Name))
            .Concat(MigrationAlterColumnCases.All().SelectMany(item => new[] { item.Previous, item.Desired }))
            .Concat(MigrationAlterColumnCases.InvalidDeclarations().Select(item => item.Column)).Distinct();
        foreach (var column in columns)
        {
            var catalog = SqlTypeCatalog.Standard;
            if (column.CustomType is { } declared && catalog.Resolve(declared, out _) is null)
            {
                if (!SqlTypeRules.TrySplit(declared, out string name, out int? first, out _) || first is not null)
                {
                    // Catalog resolution also refuses modifiers on an unknown domain. Script generation
                    // has no registration and supplies its own invalid-domain explanation, pinned by the golden.
                    Should.Throw<DatabaseException>(() => SqlMigrationLowering.ResolveAlterColumnType(column, out _));
                    continue;
                }
                Exception? refusal = null;
                try { SqlTypeRules.ValidateDomainName(name); }
                catch (ArgumentException failure) { refusal = failure; }
                if (refusal is not null)
                {
                    Should.Throw<ArgumentException>(() => SqlMigrationLowering.ResolveAlterColumnType(column, out _)).Message.ShouldBe(refusal.Message);
                    continue;
                }
                var storage = new DatabaseTypeInfo(column.Type, column.MaxLength, column.Precision, column.Scale);
                catalog = new SqlTypeCatalog(SqlType.BuiltIn.Append(SqlType.CreateDomain(name, SqlType.ForStorage(storage))), []);
            }

            // Act / Assert: include the member-storage mismatch refusal, not just resolved values.
            DatabaseTypeInfo? expected = null;
            string? expectedDomain = null;
            DatabaseException? error = null;
            try { expected = SqlSchemaProvisioner.ResolveColumnType(column, catalog, out expectedDomain); }
            catch (DatabaseException failure) { error = failure; }
            if (error is not null)
            {
                Should.Throw<DatabaseException>(() => SqlMigrationLowering.ResolveAlterColumnType(column, out _)).Message.ShouldBe(error.Message);
            }
            else
            {
                var actual = SqlMigrationLowering.ResolveAlterColumnType(column, out string? actualDomain);
                (actual.Type, actual.MaxLength, actual.Precision, actual.Scale).ShouldBe(
                    (expected!.Type, expected.MaxLength, expected.Precision, expected.Scale), column.CustomType ?? column.Type.ToString());
                actualDomain.ShouldBe(expectedDomain);
            }
        }
        foreach (DatabaseType type in Enum.GetValues<DatabaseType>().Append((DatabaseType)byte.MaxValue))
        {
            SqlMigrationLowering.DiagnosticTypeName(type).ShouldBe(SqlType.NameOf(type));
        }
    }

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Migration DEFAULT reader: shared admission preserves engine values and diagnostics")]
    [InlineData(null)]
    [InlineData("NULL")]
    [InlineData("(+1)")]
    [InlineData("-1.25")]
    [InlineData("'it''s ready'")]
    [InlineData("true")]
    [InlineData("DATE '2026-01-02'")]
    [InlineData("TIME '12:34:56'")]
    [InlineData("TIMESTAMP '2026-01-02 12:34:56'")]
    [InlineData("TIMESTAMPTZ '2026-01-02 12:34:56+00:00'")]
    [InlineData("INTERVAL 'PT1S'")]
    [InlineData("X'0102'")]
    [InlineData("1 + 2")]
    [InlineData("CURRENT_TIMESTAMP")]
    [InlineData("'unterminated")]
    [InlineData("1); DROP TABLE Orders; --")]
    [InlineData("1 ORDER BY Id")]
    [InlineData("1; --")]
    [InlineData("-(1 + 2)")]
    [InlineData("-'text'")]
    [InlineData("nextval('s')")]
    [InlineData("1 + nextval('s')")]
    [InlineData("CAST('s' AS REGCLASS)")]
    [InlineData("NEXT VALUE FOR s")]
    [InlineData("(SELECT nextval('s') FROM Orders)")]
    public void CanonicalDefault_SharedReader_ShouldMatchProvisionerReader(string? literal)
        => CompareReaders(literal);

    [Theory(DisplayName = "Cohesion Test [Database.Sql] - Migration DEFAULT reader: grouped text retains declaration and guard limits")]
    [InlineData(255, "1")]
    [InlineData(256, "1")]
    [InlineData(257, "1")]
    [InlineData(4095, "1")]
    [InlineData(4096, "1")]
    [InlineData(4097, "1")]
    [InlineData(255, "nextval('s')")]
    [InlineData(256, "nextval('s')")]
    [InlineData(257, "nextval('s')")]
    [InlineData(4095, "nextval('s')")]
    [InlineData(4096, "nextval('s')")]
    [InlineData(4097, "nextval('s')")]
    public void CanonicalDefault_NestingBoundary_ShouldMatchProvisionerReader(int depth, string expression)
    {
        // Arrange: a fixed stack makes the parser ceiling independent of the runner's stack.
        string literal = new string('(', depth) + expression + new string(')', depth);
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { CompareReaders(literal); }
            catch (Exception exception) { failure = exception; }
        }, 16 * 1024 * 1024) { IsBackground = true };

        // Act
        thread.Start();

        // Assert: only a hang guard, not a wall-clock performance requirement.
        thread.Join(TimeSpan.FromSeconds(30)).ShouldBeTrue();
        failure.ShouldBeNull();
    }

    private static void CompareReaders(string? literal)
    {
        // Arrange: provisioning and ALTER lowering must admit identical defaults and refusals.
        string? expected = null;
        Exception? refusal = null;
        try { expected = SqlSchemaProvisioner.CanonicalDefault(literal); }
        catch (DatabaseException exception) { refusal = exception; }

        // Act / Assert
        if (refusal is null)
        {
            SqlMigrationDefaultLiteral.CanonicalDefault(literal).ShouldBe(expected);
        }
        else
        {
            var actual = Should.Throw<DatabaseException>(() => SqlMigrationDefaultLiteral.CanonicalDefault(literal));
            actual.GetType().ShouldBe(refusal.GetType());
            actual.Message.ShouldBe(refusal.Message);
            actual.Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Severity,
                diagnostic.Location, diagnostic.Start, diagnostic.End, diagnostic.Line)).ShouldBe(
                ((DatabaseException)refusal).Diagnostics.Select(diagnostic => (diagnostic.Code, diagnostic.Message, diagnostic.Severity,
                    diagnostic.Location, diagnostic.Start, diagnostic.End, diagnostic.Line)));
        }
    }
}
