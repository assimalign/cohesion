using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

/// <summary>
/// A rule that throws fails validation closed: the exception propagates from the validator instead of the
/// rule being recorded as not invoked, which let the value pass with no error (#1292). A null nested object
/// is still skipped, explicitly.
/// </summary>
public class ValidatorRuleFaultTests
{
    public sealed class Address
    {
        public string? City { get; set; }
    }

    public sealed class Customer
    {
        public string? Name { get; set; }

        public Address? Address { get; set; }
    }

    /// <summary>A custom rule on the name that throws.</summary>
    public sealed class ThrowingCustomRuleProfile : ValidationProfile<Customer>
    {
        public override void Configure(IValidationRuleDescriptor<Customer> descriptor)
        {
            descriptor.RuleFor(customer => customer.Name!)
                .Custom((name, context) => throw new InvalidOperationException("custom rule fault"));
        }
    }

    /// <summary>A nested profile whose custom rule on the city throws.</summary>
    public sealed class ThrowingNestedRuleProfile : ValidationProfile<Customer>
    {
        public override void Configure(IValidationRuleDescriptor<Customer> descriptor)
        {
            descriptor.RuleFor(customer => customer.Address!).ChildRules(address =>
                address.RuleFor(a => a.City!).Custom((city, context) => throw new InvalidOperationException("nested rule fault")));
        }
    }

    /// <summary>A nested profile that requires a city.</summary>
    public sealed class NestedCityProfile : ValidationProfile<Customer>
    {
        public override void Configure(IValidationRuleDescriptor<Customer> descriptor)
        {
            descriptor.RuleFor(customer => customer.Address!).ChildRules(address => address.RuleFor(a => a.City!).NotEmpty());
        }
    }

    // The typed overload configures the profile; AddProfile(IValidationProfile) expects one configured already.
    private static IValidator CreateValidator<T>(IValidationProfile<T> profile) => Validator.Create(builder => builder.AddProfile(profile));

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Rule faults: a custom rule that throws faults the validation instead of passing it")]
    public void Validate_CustomRuleThrows_ShouldPropagateTheException()
    {
        // Arrange
        IValidator validator = CreateValidator(new ThrowingCustomRuleProfile());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(() => validator.Validate(new Customer { Name = "Ada" }));

        // Assert
        exception.Message.ShouldBe("custom rule fault");
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Rule faults: a nested profile whose rule throws faults the validation instead of passing it")]
    public void Validate_NestedRuleThrows_ShouldPropagateTheException()
    {
        // Arrange
        IValidator validator = CreateValidator(new ThrowingNestedRuleProfile());

        // Act
        InvalidOperationException exception = Should.Throw<InvalidOperationException>(
            () => validator.Validate(new Customer { Address = new Address { City = "London" } }));

        // Assert
        exception.Message.ShouldBe("nested rule fault");
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Rule faults: ValidateAsync faults when a nested profile's rule throws")]
    public async Task ValidateAsync_NestedRuleThrows_ShouldPropagateTheException()
    {
        // Arrange
        IValidator validator = CreateValidator(new ThrowingNestedRuleProfile());

        // Act
        InvalidOperationException exception = await Should.ThrowAsync<InvalidOperationException>(
            () => validator.ValidateAsync(new Customer { Address = new Address { City = "London" } }));

        // Assert
        exception.Message.ShouldBe("nested rule fault");
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Rule faults: a null nested object is skipped, not faulted")]
    public void Validate_NullNestedObject_ShouldBeValid()
    {
        // Arrange
        IValidator validator = CreateValidator(new NestedCityProfile());

        // Act
        ValidationResult result = validator.Validate(new Customer { Address = null });

        // Assert
        result.IsValid.ShouldBeTrue();
    }
}
