using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Tests;

/// <summary>Exercises admission and execution of engine-registered document functions.</summary>
public sealed class DocumentFunctionExtensibilityTests
{
    [Fact(DisplayName = "Cohesion Test [Documents] - Functions: custom call requests defer engine binding")]
    public void FromOql_CustomFunctionCall_ShouldRemainAvailableForEngineBinding()
    {
        // Arrange / Act: parsing a request precedes choosing the engine whose catalog binds it.
        var request = DocumentQueryRequest.FromOql("SELECT developer_slug(name) AS slug FROM items");

        // Assert: the unbound call must survive intact for the executing engine to resolve.
        request.Statement.Diagnostics.ShouldBeEmpty();
        var select = request.Statement.OqlExpression.ShouldBeOfType<OqlSelectExpression>();
        var projection = select.Projections.ShouldHaveSingleItem();
        projection.Alias.ShouldBe("slug");
        var call = projection.Expression.ShouldBeOfType<OqlCallExpression>();
        call.Name.ShouldBe("DEVELOPER_SLUG");
        var argument = call.Arguments.ShouldHaveSingleItem().ShouldBeOfType<OqlPathExpression>();
        argument.Segments.ShouldHaveSingleItem().Name.ShouldBe("name");
    }
}
