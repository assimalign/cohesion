using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Database.Graph.Language.Tests;

public sealed class GqlCollectionPropertyParserTests
{
    [Theory(DisplayName = "Cohesion Test [GQL] - Property values: full scalar expressions pass admission")]
    [InlineData("INSERT (n:N {k:[x IN [1] | x+10][0]}) RETURN n.k")]
    [InlineData("MATCH (n:N {k:[x IN [1] | x][0]}) RETURN n.k")]
    [InlineData("MATCH (n:N) SET n.k=[x IN [n.k] | x+10][0] RETURN n.k")]
    [InlineData("MATCH (n:N) SET n={k:[1,2][0..1][0]} RETURN n.k")]
    [InlineData("MATCH (n:N) SET n += {k:{k:11}.k} RETURN n.k")]
    [InlineData("MERGE (n:N {k:[x IN [1] | x*2][0]}) ON CREATE SET n.c=[x IN [1] | x][0] ON MATCH SET n.m=[x IN [2] | x][0] RETURN n.k")]
    public void Parse_ScalarProperty_ShouldAcceptCompletedExpression(string query)
    {
        // Arrange / Act
        var statement = new GqlQueryParser().Parse(query);
        // Assert
        statement.Diagnostics.ShouldBeEmpty();
    }

    [Theory(DisplayName = "Cohesion Test [GQL] - Property values: static containers retain the capability refusal in every position")]
    [InlineData("[1]")]
    [InlineData("{k:1}")]
    [InlineData("[x IN [1] | x]")]
    [InlineData("[1,2][0..1]")]
    public void Parse_ContainerProperty_ShouldRefuseCapability(string value)
    {
        // Arrange / Act
        foreach (string query in new[]
        {
            $"MATCH (n:N {{k:({value})}}) RETURN n.k",
            $"INSERT (n:N {{k:({value})}}) RETURN n.k",
            $"MERGE (n:N {{k:({value})}}) RETURN n.k",
            $"MATCH (n:N) SET n.k=({value}) RETURN n.k",
            $"MATCH (n:N) SET n={{k:({value})}} RETURN n.k",
            $"MATCH (n:N) SET n += {{k:({value})}} RETURN n.k",
        })
        {
            var statement = new GqlQueryParser().Parse(query);
            // Assert
            statement.Diagnostics.ShouldHaveSingleItem().Code.ShouldBe("CK00001");
        }
    }
}
