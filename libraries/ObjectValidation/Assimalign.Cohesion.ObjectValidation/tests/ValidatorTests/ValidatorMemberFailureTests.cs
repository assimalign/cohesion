using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

/// <summary>
/// Which failures a validator reports under each option. <see cref="ValidationMode"/> decides whether every
/// failing member is reported (<see cref="ValidationMode.Cascade"/>, the default) or validation stops at the
/// first failing member (<see cref="ValidationMode.Stop"/>).
/// <see cref="ValidationOptions.ContinueThroughValidationChain"/> decides whether a failing rule stops the rest
/// of its own member's chain (the default) or every rule runs. A member's chain never stops because another
/// member failed.
/// </summary>
/// <remarks>
/// The assertions ignore order: the item and rule queues enumerate newest first, so the order members and
/// chained rules run in is not the order they are declared in.
/// </remarks>
public class ValidatorMemberFailureTests
{
    private const string nameSource = "customer => customer.Name";
    private const string ageSource = "customer => customer.Age";
    private const string citySource = "customer => customer.Address.City";
    private const string zipSource = "customer => customer.Address.Zip";
    private const string tagsSource = "customer => customer.Tags";

    public sealed class Address
    {
        public string? City { get; set; }

        public string? Zip { get; set; }
    }

    public sealed class Customer
    {
        public string? Name { get; set; }

        public int Age { get; set; }

        public Address? Address { get; set; }

        public IEnumerable<string>? Tags { get; set; }
    }

    /// <summary>
    /// A name checked by two chained rules, an adult age, a nested address with two members, and tags checked
    /// by two chained rules per element.
    /// </summary>
    public sealed class CustomerProfile : ValidationProfile<Customer>
    {
        public override void Configure(IValidationRuleDescriptor<Customer> descriptor)
        {
            descriptor.RuleFor(customer => customer.Name!).NotEmpty().MinLength(3);
            descriptor.RuleFor(customer => customer.Age).GreaterThanOrEqualTo(18);
            descriptor.RuleFor(customer => customer.Address!).ChildRules(address =>
            {
                address.RuleFor(a => a.City!).NotEmpty();
                address.RuleFor(a => a.Zip!).NotEmpty();
            });
            descriptor.RuleForEach(customer => customer.Tags!).NotEmpty().MinLength(3);
        }
    }

    private static IValidator CreateValidator(Action<ValidationOptions>? configure = null) => Validator.Create(builder =>
    {
        if (configure is not null)
        {
            builder.AddOptions(configure);
        }

        builder.AddProfile(new CustomerProfile());
    });

    // Valid in every member: each test breaks the members it is about.
    private static Customer CreateCustomer() => new()
    {
        Name = "Ada",
        Age = 36,
        Address = new Address { City = "London", Zip = "N1" },
        Tags = ["vip"]
    };

    private static string[] Sources(ValidationResult result) => result.Errors.Select(error => error.Source).ToArray();

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Default options: every failing member is reported, nested members included")]
    public void Validate_DefaultOptionsSeveralFailingMembers_ShouldReportEveryMember()
    {
        // Arrange — the name fails one of its two rules; the age and both nested members fail.
        Customer customer = CreateCustomer();
        customer.Name = "Al";
        customer.Age = 12;
        customer.Address = new Address();

        // Act
        ValidationResult result = CreateValidator().Validate(customer);

        // Assert
        Sources(result).ShouldBe([nameSource, ageSource, citySource, zipSource], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Default options: a member's chain stops at its first failing rule, and the other members still run")]
    public void Validate_DefaultOptionsMemberWithSeveralFailingRules_ShouldReportOneErrorForThatMember()
    {
        // Arrange — an empty name fails both of its rules; the age fails too.
        Customer customer = CreateCustomer();
        customer.Name = string.Empty;
        customer.Age = 12;

        // Act
        ValidationResult result = CreateValidator().Validate(customer);

        // Assert — one error for the name, whose chain stopped, and the age's.
        Sources(result).ShouldBe([nameSource, ageSource], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - ContinueThroughValidationChain: every failing rule of every member is reported")]
    public void Validate_ContinueThroughValidationChain_ShouldReportEveryFailingRuleOfEveryMember()
    {
        // Arrange — an empty name fails both of its rules; the age fails too.
        Customer customer = CreateCustomer();
        customer.Name = string.Empty;
        customer.Age = 12;

        // Act
        ValidationResult result = CreateValidator(options => options.ContinueThroughValidationChain = true).Validate(customer);

        // Assert — both of the name's rules, and the age's.
        Sources(result).ShouldBe([nameSource, nameSource, ageSource], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Stop mode: validation stops at the first failing member")]
    public void Validate_StopModeSeveralFailingMembers_ShouldReportOneMember()
    {
        // Arrange — three members fail, each in one rule.
        Customer customer = CreateCustomer();
        customer.Name = "Al";
        customer.Age = 12;
        customer.Address = new Address { City = string.Empty, Zip = "N1" };

        // Act
        ValidationResult result = CreateValidator(options => options.ValidationMode = ValidationMode.Stop).Validate(customer);

        // Assert
        result.Errors.ShouldHaveSingleItem();
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Default options: a failing collection member does not stop another member")]
    public void Validate_DefaultOptionsFailingCollectionAndMember_ShouldReportBoth()
    {
        // Arrange — one tag is too short; the age fails too.
        Customer customer = CreateCustomer();
        customer.Tags = ["vip", "x"];
        customer.Age = 12;

        // Act
        ValidationResult result = CreateValidator().Validate(customer);

        // Assert
        Sources(result).ShouldBe([tagsSource, ageSource], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Default options: a collection's chain stops after the rule that fails first, which reports every failing element")]
    public void Validate_DefaultOptionsCollectionWithSeveralFailingRules_ShouldReportOneRulePerFailingElement()
    {
        // Arrange — both tags are empty, so each fails both rules; the age fails too.
        Customer customer = CreateCustomer();
        customer.Tags = [string.Empty, string.Empty];
        customer.Age = 12;

        // Act
        ValidationResult result = CreateValidator().Validate(customer);

        // Assert — one error per element from the rule that ran first, none from the rule after it, and the age's.
        Sources(result).ShouldBe([tagsSource, tagsSource, ageSource], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Default options: ValidateAsync reports every failing member")]
    public async Task ValidateAsync_DefaultOptionsSeveralFailingMembers_ShouldReportEveryMember()
    {
        // Arrange
        Customer customer = CreateCustomer();
        customer.Name = "Al";
        customer.Age = 12;

        // Act
        ValidationResult result = await CreateValidator().ValidateAsync(customer, CancellationToken.None);

        // Assert
        Sources(result).ShouldBe([nameSource, ageSource], ignoreOrder: true);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Default options: an error already in the context does not stop any member's rules")]
    public void Validate_ContextWithEarlierError_ShouldRunEveryMembersRules()
    {
        // Arrange — the caller's context already holds an error that no member reported.
        Customer customer = CreateCustomer();
        customer.Name = "Al";
        customer.Age = 12;

        IValidationContext context = new ValidationContext<Customer>(customer);
        context.AddFailure("order", "The order is closed.");

        // Act
        ValidationResult result = CreateValidator().Validate(context);

        // Assert
        Sources(result).ShouldBe(["order", nameSource, ageSource], ignoreOrder: true);
    }
}
