using System;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language.Tests;

public sealed class OqlFunctionApiReviewTests
{
    [Fact(DisplayName = "Cohesion Test [Documents.Language] - Functions: the original parser constructor remains available")]
    public void Constructor_ExistingSignature_ShouldRemainAvailable()
    {
        // Arrange / Act / Assert
        typeof(OqlQueryParser).GetConstructor([typeof(QueryParserOptions)]).ShouldNotBeNull();
    }
}
