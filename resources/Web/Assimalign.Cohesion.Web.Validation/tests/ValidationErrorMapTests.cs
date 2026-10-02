using System.Collections.Generic;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.Web.Validation.Internal;

namespace Assimalign.Cohesion.Web.Validation.Tests;

/// <summary>
/// How ObjectValidation errors become the <c>errors</c> map of a <c>400</c>: keys are member paths, and each
/// key's messages are reported in the order the rules ran.
/// </summary>
public class ValidationErrorMapTests
{
    [Theory(DisplayName = "Cohesion Test [Web.Validation] - Error map: a source is reported under the key a client names")]
    [InlineData("p => p.Name", "Name")]
    [InlineData("customer => customer.Address.City", "Address.City")]
    [InlineData("name", "name")]
    [InlineData("p => q.Name", "p => q.Name")]
    [InlineData("p => p", "p => p")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void GetKey_Source_ShouldReportTheMemberPath(string? source, string expected)
    {
        // Act
        string key = ValidationErrorMap.GetKey(source);

        // Assert
        key.ShouldBe(expected);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Error map: messages are grouped per key in the order the rules ran")]
    public void Create_Errors_ShouldGroupMessagesPerKeyInRuleOrder()
    {
        // Arrange — ObjectValidation enumerates its errors newest first.
        List<IValidationError> newestFirst =
        [
            new ValidationError { Source = "p => p.Age", Message = "too young" },
            new ValidationError { Source = "p => p.Name", Message = "too short" },
            new ValidationError { Source = "p => p.Name", Message = "required" },
        ];

        // Act
        Dictionary<string, object?> map = ValidationErrorMap.Create(newestFirst);

        // Assert
        map.Keys.ShouldBe(["Name", "Age"]);
        map["Name"].ShouldBe(new[] { "required", "too short" });
        map["Age"].ShouldBe(new[] { "too young" });
    }
}
