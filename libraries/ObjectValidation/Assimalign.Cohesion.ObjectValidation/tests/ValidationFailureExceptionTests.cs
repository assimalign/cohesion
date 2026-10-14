using System.Collections.Generic;
using System.Linq;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

public sealed class ValidationFailureExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Constructor: Should retain context errors")]
    public void Constructor_WithContext_ShouldRetainErrors()
    {
        // Arrange
        var context = new ValidationContext<object>(new object());
        context.AddFailure("Name", "Name is required.");
        context.AddFailure("Age", "Age is out of range.");
        var errors = context.Errors.ToArray();

        // Act
        var exception = new ValidationFailureException(context);
        context.AddFailure("Later", "Added after construction.");

        // Assert
        exception.Errors.Count.ShouldBe(2);
        exception.Errors[0].ShouldBeSameAs(errors[0]);
        exception.Errors[1].ShouldBeSameAs(errors[1]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Constructor: Should retain enumerable errors")]
    public void Constructor_WithEnumerable_ShouldRetainErrors()
    {
        // Arrange
        var first = new ValidationError { Code = "required", Source = "Name", Message = "Name is required." };
        var second = new ValidationError { Code = "range", Source = "Age", Message = "Age is out of range." };
        var errors = new List<IValidationError> { first, second };

        // Act
        var exception = new ValidationFailureException(errors);
        errors.Clear();

        // Assert
        exception.Errors.Count.ShouldBe(2);
        exception.Errors[0].ShouldBeSameAs(first);
        exception.Errors[1].ShouldBeSameAs(second);
        ((ICollection<IValidationError>)exception.Errors).IsReadOnly.ShouldBeTrue();
    }
}
