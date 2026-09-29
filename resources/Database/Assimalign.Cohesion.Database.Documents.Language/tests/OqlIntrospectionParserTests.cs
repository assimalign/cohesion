using System.Linq;
using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlIntrospectionParserTests
{
    [Theory]
    [InlineData("COHESION_SCHEMA.INDEXES")]
    [InlineData("cohesion_schema.object_ownership")]
    [InlineData("\"COHESION_SCHEMA\".\"INDEXES\"")]
    [InlineData("\"COHESION_SCHEMA.INDEXES\"")]
    public void Reserved_system_sources_parse_in_queries_and_index_ddl(string source)
    {
        foreach (string text in new[] { $"SELECT * FROM {source}", $"CREATE INDEX ix ON {source} (PATH)", $"DROP INDEX ix ON {source}" })
        {
            var statement = new OqlQueryParser().Parse(text).ShouldBeOfType<OqlQueryStatement>();
            statement.Diagnostics.ShouldBeEmpty();
            string collection = statement.OqlExpression switch
            {
                OqlSelectExpression select => select.Collection,
                OqlCreateIndexExpression create => create.Collection,
                OqlDropIndexExpression drop => drop.Collection,
                _ => throw new Xunit.Sdk.XunitException("Expected a query or index DDL statement."),
            };
            collection.ShouldBe(source.Replace("\"", string.Empty));
        }
    }

    [Theory]
    [InlineData("SELECT * FROM other.items")]
    [InlineData("SELECT * FROM other.COHESION_SCHEMA.INDEXES")]
    [InlineData("SELECT * FROM COHESION_SCHEMA.INDEXES.other")]
    [InlineData("SELECT * FROM COHESION_SCHEMA.")]
    [InlineData("CREATE INDEX ix ON other.items (x)")]
    [InlineData("DROP INDEX ix ON other.items")]
    public void Reserved_namespace_does_not_enable_database_qualification(string text)
        => new OqlQueryParser().Parse(text).Diagnostics.Any(diagnostic => diagnostic.Code == "OQL0002").ShouldBeTrue();
}
