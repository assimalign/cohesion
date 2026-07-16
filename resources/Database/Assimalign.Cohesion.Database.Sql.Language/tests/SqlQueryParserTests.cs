using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Sql.Language.Tests;

public class SqlQueryParserTests
{
    [Theory]
    [InlineData("SELECT * FROM dbo.Users;", SqlQueryCommandType.Select)]
    [InlineData("INSERT INTO dbo.Users(Id) VALUES(1);", SqlQueryCommandType.Insert)]
    [InlineData("UPDATE dbo.Users SET Name = 'A';", SqlQueryCommandType.Update)]
    [InlineData("DELETE FROM dbo.Users WHERE Id = 1;", SqlQueryCommandType.Delete)]
    [InlineData("CREATE TABLE dbo.Users (Id INT);", SqlQueryCommandType.Create)]
    [InlineData("ALTER TABLE dbo.Users ADD Name NVARCHAR(100);", SqlQueryCommandType.Alter)]
    [InlineData("DROP TABLE dbo.Users;", SqlQueryCommandType.Drop)]
    public void Parse_KnownStandardSql_InfersCommandType(string sql, SqlQueryCommandType commandType)
    {
        var parser = new SqlQueryParser();

        var statement = (SqlQueryStatement)parser.Parse(sql);

        statement.SqlExpression.CommandType.ShouldBe(commandType);
        statement.Diagnostics.ShouldNotContain(x => x.Code == "SQL0002");
    }

    [Fact]
    public void Parse_EmptySql_EmitsErrorDiagnostic()
    {
        var parser = new SqlQueryParser();

        var statement = (SqlQueryStatement)parser.Parse("   \r\n  ");

        statement.SqlExpression.CommandType.ShouldBe(SqlQueryCommandType.Unknown);
        statement.Diagnostics.ShouldContain(x => x.Code == "SQL0001");
    }

    [Fact]
    public void Parse_UnknownCommand_EmitsUnknownCommandDiagnostic()
    {
        var parser = new SqlQueryParser();

        var statement = (SqlQueryStatement)parser.Parse("MERGE INTO dbo.Users u USING dbo.Users2 u2 ON u.Id = u2.Id;");

        statement.SqlExpression.CommandType.ShouldBe(SqlQueryCommandType.Unknown);
        statement.Diagnostics.ShouldContain(x => x.Code == "SQL0002");
    }

    [Fact]
    public void Parse_MissingTerminator_EmitsInformationDiagnostic()
    {
        var parser = new SqlQueryParser();

        var statement = (SqlQueryStatement)parser.Parse("SELECT Id FROM dbo.Users");

        statement.SqlExpression.CommandType.ShouldBe(SqlQueryCommandType.Select);
        statement.Diagnostics.ShouldContain(x => x.Code == "SQL0100");
    }
}
