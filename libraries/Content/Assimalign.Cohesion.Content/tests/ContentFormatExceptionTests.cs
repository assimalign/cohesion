using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Content.Tests;

public class ContentFormatExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [Content] - Classified format errors preserve code, position and cause")]
    public void Constructor_ClassifiedError_PreservesDiagnostics()
    {
        // Arrange
        var cause = new InvalidOperationException();

        // Act
        var error = new ContentFormatException("test", ContentFormatErrorCode.Truncated, 42, cause);

        // Assert
        error.ErrorCode.ShouldBe(ContentFormatErrorCode.Truncated);
        error.Position.ShouldBe(42);
        error.InnerException.ShouldBeSameAs(cause);
    }

    [Fact(DisplayName = "Cohesion Test [Content] - Legacy format errors remain unspecified")]
    public void Constructor_LegacyOverloads_KeepUnspecifiedCode()
    {
        // Arrange
        var cause = new InvalidOperationException();

        // Act
        ContentFormatException[] errors = [new("test"), new("test", 12L), new("test", cause)];

        // Assert
        foreach (var error in errors)
        {
            error.ErrorCode.ShouldBe(ContentFormatErrorCode.Unspecified);
        }
        errors[0].Position.ShouldBeNull();
        errors[1].Position.ShouldBe(12);
        errors[2].InnerException.ShouldBeSameAs(cause);
    }
}
