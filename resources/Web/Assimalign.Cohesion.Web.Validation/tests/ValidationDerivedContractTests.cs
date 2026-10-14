using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization.Metadata;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Validation.Internal;
using Assimalign.Cohesion.Web.Validation.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Validation.Tests;

/// <summary>Integration coverage for derived contracts emitted by System.Text.Json's generator.</summary>
public sealed class ValidationDerivedContractTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Validation] - Error map: Should discover attributed derived contracts from a base-only JSON registration")]
    [InlineData(typeof(ValidationDerivedModel), "model => model.DisplayName", "display_name")]
    [InlineData(typeof(ValidationDerivedModel), "model => model.Next.DisplayName", "next.display_name")]
    [InlineData(typeof(ValidationLeafModel), "model => model.DisplayName", "display_name")]
    [InlineData(typeof(ValidationLeafModel), "model => model.TrackingCode", "tracking_code")]
    public void Create_AttributedDerivedContract_ShouldUseRegisteredJsonNames(Type modelType, string source, string expected)
    {
        // Arrange — only the base type is explicitly registered; System.Text.Json discovers both descendants.
        IHttpContentSerializationFeature serialization = new ContentSerializationBuilder()
            .AddJson(ValidationDerivedJsonContext.Default).Build();
        serialization.TryGetJsonTypeInfo(modelType, out JsonTypeInfo? contract).ShouldBeTrue();
        contract.ShouldNotBeNull();
        contract.Properties.Select(property => property.Name).ShouldContain("display_name");
        IValidationError[] errors = [new ValidationError { Source = source, Message = "invalid" }];

        // Act
        Dictionary<string, object?> map = ValidationErrorMap.Create(errors, modelType, serialization);

        // Assert
        map.Keys.ShouldBe([expected]);
        map[expected].ShouldBe(new[] { "invalid" });
    }
}
