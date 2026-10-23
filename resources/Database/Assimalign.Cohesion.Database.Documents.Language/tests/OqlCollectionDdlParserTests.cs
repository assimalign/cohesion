using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlCollectionDdlParserTests
{
    [Theory(DisplayName = "Cohesion Test [Documents.Language] - CREATE COLLECTION: names and positional IF NOT EXISTS parse")]
    [InlineData("CREATE COLLECTION c", "c", false)]
    [InlineData("create collection \"order lines\";", "order lines", false)]
    [InlineData("CREATE COLLECTION IF NOT EXISTS c", "c", true)]
    [InlineData("CREATE COLLECTION \"if\"", "if", false)]
    [InlineData("CREATE COLLECTION if", "if", false)]
    [InlineData("CREATE COLLECTION IF NOT EXISTS if", "if", true)]
    public void Parse_CreateCollection_ShouldRetainNameAndModifier(string text, string name, bool conditional)
    {
        // Arrange / Act
        var statement = Parse(text);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var create = statement.OqlExpression.ShouldBeOfType<OqlCreateCollectionExpression>();
        create.Name.ShouldBe(name);
        create.IfNotExists.ShouldBe(conditional);
        create.Text.ShouldBe(text);
        create.Location!.Start.ShouldBe(0);
        create.Location.End.ShouldBe(text.TrimEnd(';').Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - DROP COLLECTION: names and positional IF EXISTS parse")]
    [InlineData("DROP COLLECTION people", "people", false)]
    [InlineData("DROP COLLECTION IF EXISTS c", "c", true)]
    [InlineData("drop collection \"order lines\";", "order lines", false)]
    [InlineData("DROP COLLECTION if", "if", false)]
    [InlineData("DROP COLLECTION IF EXISTS if", "if", true)]
    public void Parse_DropCollection_ShouldRetainNameAndModifier(string text, string name, bool conditional)
    {
        // Arrange / Act
        var statement = Parse(text);

        // Assert
        statement.Diagnostics.ShouldBeEmpty();
        var drop = statement.OqlExpression.ShouldBeOfType<OqlDropCollectionExpression>();
        drop.Name.ShouldBe(name);
        drop.IfExists.ShouldBe(conditional);
        drop.Text.ShouldBe(text);
        drop.Location!.Start.ShouldBe(0);
        drop.Location.End.ShouldBe(text.TrimEnd(';').Length);
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Collection DDL: invalid syntax retains exact code and offset")]
    [InlineData("CREATE COLLECTION other.items", "OQL0002", 23)]
    [InlineData("CREATE COLLECTION", "OQL0002", 17)]
    [InlineData("CREATE COLLECTION b.s.c", "OQL0002", 19)]
    [InlineData("CREATE COLLECTION IF EXISTS c", "OQL0002", 21)]
    [InlineData("DROP COLLECTION", "OQL0002", 15)]
    [InlineData("DROP COLLECTION IF NOT EXISTS c", "OQL0002", 19)]
    [InlineData("CREATE COLLECTION c extra", "OQL0002", 20)]
    [InlineData("CREATE COLLECTION c; SELECT * FROM c", "OQL0002", 21)]
    [InlineData("DROP COLLECTION c extra", "OQL0002", 18)]
    [InlineData("DROP COLLECTION c; SELECT * FROM c", "OQL0002", 19)]
    public void Parse_InvalidCollectionDdl_ShouldReportSyntax(string text, string code, int offset)
    {
        // Arrange / Act
        var errors = Parse(text).Diagnostics.ToArray();

        // Assert
        errors.ShouldNotBeEmpty();
        errors[0].Code.ShouldBe(code);
        errors[0].Start.ShouldBe(offset);
        errors.ShouldAllBe(error => error.Code != "CK00001");
    }

    [Theory(DisplayName = "Cohesion Test [Documents.Language] - Collection DDL: unsupported constructs stay positional")]
    [InlineData("CREATE TABLE t", "CREATE", 0)]
    [InlineData("CREATE DATASET c", "CREATE", 0)]
    [InlineData("ALTER COLLECTION c", "ALTER", 0)]
    [InlineData("CREATE COLLECTION c WITH {}", "WITH", 20)]
    [InlineData("SELECT * FROM c WHERE EXISTS c.tags", "EXISTS", 22)]
    [InlineData("CREATE COLLECTION c EXISTS", "EXISTS", 20)]
    [InlineData("CREATE COLLECTION EXISTS", "EXISTS", 18)]
    [InlineData("SELECT COLLECTION(1, 2) FROM c", "COLLECTION", 7)]
    [InlineData("SELECT * FROM c WHERE EXISTS x IN c.items: x > 1", "EXISTS", 22)]
    public void Parse_UnsupportedConstruct_ShouldReportCapability(string text, string construct, int offset)
    {
        // Arrange / Act
        var error = Parse(text).Diagnostics.ShouldHaveSingleItem();

        // Assert
        error.Code.ShouldBe("CK00001");
        error.Start.ShouldBe(offset);
        error.End.ShouldBe(offset + construct.Length);
        error.Message!.ShouldContain($"The {construct} clause", Case.Sensitive);
    }

    private static OqlQueryStatement Parse(string text) =>
        new OqlQueryParser().Parse(text).ShouldBeOfType<OqlQueryStatement>();
}
