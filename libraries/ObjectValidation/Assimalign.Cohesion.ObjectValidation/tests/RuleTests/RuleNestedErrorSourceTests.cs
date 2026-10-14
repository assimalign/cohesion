using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

/// <summary>
/// The source an error carries when a nested profile (<c>ChildRules</c> or <c>UseProfile</c>) reports it:
/// the nested member is named under its parent member, so equally named members of different nested
/// objects stay distinguishable.
/// </summary>
public class RuleNestedErrorSourceTests
{
    public sealed class Address
    {
        public string City { get; set; }

        public string Zip { get; set; }

        public Region Region { get; set; }
    }

    public sealed class Region
    {
        public string Code { get; set; }
    }

    public sealed class Order
    {
        public Address Shipping { get; set; }

        public Address Billing { get; set; }
    }

    public sealed class AddressProfile : ValidationProfile<Address>
    {
        public override void Configure(IValidationRuleDescriptor<Address> descriptor)
        {
            descriptor.RuleFor(address => address.City).NotEmpty();
            descriptor.RuleFor(address => address.Zip).NotEmpty(error =>
            {
                error.Source = "zip";
                error.Message = "The postal code is required.";
            });
        }
    }

    public sealed class OrderProfile : ValidationProfile<Order>
    {
        public override void Configure(IValidationRuleDescriptor<Order> descriptor)
        {
            descriptor.RuleFor(order => order.Shipping).ChildRules(address =>
            {
                address.RuleFor(a => a.City).NotEmpty();
                address.RuleFor(a => a.Region).ChildRules(region => region.RuleFor(r => r.Code).NotEmpty());
            });

            descriptor.RuleFor(order => order.Billing).UseProfile(new AddressProfile());
        }
    }

    private static IValidator CreateValidator() => Validator.Create(builder =>
    {
        builder.AddOptions(options => options.ContinueThroughValidationChain = true);
        builder.AddProfile(new OrderProfile());
    });

    private static IReadOnlyList<string> Sources(ValidationResult result) => result.Errors.Select(error => error.Source).ToArray();

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Nested rules: an error from ChildRules is reported under the parent member")]
    public void Validate_ChildRulesMemberError_ShouldComposeSourceUnderParent()
    {
        // Arrange
        Order order = new() { Shipping = new Address { Region = new Region { Code = "WA" } }, Billing = new Address { City = "Seattle", Zip = "98101" } };

        // Act
        ValidationResult result = CreateValidator().Validate(order);

        // Assert
        Sources(result).ShouldBe(["order => order.Shipping.City"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Nested rules: an error from UseProfile is reported under the parent member, and an explicit source is kept")]
    public void Validate_UseProfileMemberErrors_ShouldComposeDefaultSourcesAndKeepExplicitOnes()
    {
        // Arrange
        Order order = new() { Shipping = new Address { City = "Portland", Region = new Region { Code = "OR" } }, Billing = new Address() };

        // Act
        ValidationResult result = CreateValidator().Validate(order);

        // Assert — in the order the nested profile declares its members.
        Sources(result).ShouldBe(["order => order.Billing.City", "zip"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Nested rules: nesting two levels deep composes both parent members")]
    public void Validate_TwoLevelsDeep_ShouldComposeEveryParent()
    {
        // Arrange
        Order order = new() { Shipping = new Address { City = "Portland", Region = new Region() }, Billing = new Address { City = "Seattle", Zip = "98101" } };

        // Act
        ValidationResult result = CreateValidator().Validate(order);

        // Assert
        Sources(result).ShouldBe(["order => order.Shipping.Region.Code"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Nested rules: only the default member-selector form is composed")]
    public void ComposeSource_NonSelectorSources_ShouldKeepTheNestedSource()
    {
        // Act
        string composed = Internal.ChildValidationRule<Address>.ComposeSource("p => p.Address", "a => a.City");
        string explicitNested = Internal.ChildValidationRule<Address>.ComposeSource("p => p.Address", "city");
        string explicitParent = Internal.ChildValidationRule<Address>.ComposeSource("address", "a => a.City");
        string missing = Internal.ChildValidationRule<Address>.ComposeSource("p => p.Address", null);

        // Assert
        composed.ShouldBe("p => p.Address.City");
        explicitNested.ShouldBe("city");
        explicitParent.ShouldBe("a => a.City");
        missing.ShouldBeNull();
    }
}
