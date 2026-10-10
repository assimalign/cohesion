using System;
using System.Collections.Generic;
using System.Linq;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

/// <summary>
/// The order a validator evaluates in (#1221): a profile's members in the order they are declared, and each
/// member's rules in the order they are chained, for single members, collections (<c>RuleForEach</c>), nested
/// profiles (<c>ChildRules</c>, <c>UseProfile</c>) and <c>When</c> blocks. The order decides what is reported:
/// with the default options a chain reports the first of its rules that fails, and
/// <see cref="ValidationMode.Stop"/> stops at the first failing member. Errors are listed in the order they
/// were reported.
/// </summary>
public class ValidatorEvaluationOrderTests
{
    private const string required = "required";
    private const string tooShort = "too short";

    public sealed class Address
    {
        public string? City { get; set; }

        public string? Zip { get; set; }
    }

    public sealed class Parcel
    {
        public string? Label { get; set; }

        public string? Sender { get; set; }

        public string? Recipient { get; set; }

        public string? Reference { get; set; }

        public Address? Destination { get; set; }

        public IEnumerable<string>? Tags { get; set; }

        public IEnumerable<Address>? Stops { get; set; }
    }

    /// <summary>A city, then a zip.</summary>
    public sealed class AddressProfile : ValidationProfile<Address>
    {
        public override void Configure(IValidationRuleDescriptor<Address> descriptor)
        {
            descriptor.RuleFor(address => address.City!).NotEmpty();
            descriptor.RuleFor(address => address.Zip!).NotEmpty();
        }
    }

    /// <summary>The rules each test declares.</summary>
    public sealed class ParcelProfile : ValidationProfile<Parcel>
    {
        private readonly Action<IValidationRuleDescriptor<Parcel>> _configure;

        public ParcelProfile(Action<IValidationRuleDescriptor<Parcel>> configure)
        {
            _configure = configure;
        }

        public override void Configure(IValidationRuleDescriptor<Parcel> descriptor) => _configure(descriptor);
    }

    private static IValidator CreateValidator(Action<IValidationRuleDescriptor<Parcel>> configure, Action<ValidationOptions>? options = null) => Validator.Create(builder =>
    {
        if (options is not null)
        {
            builder.AddOptions(options);
        }

        builder.AddProfile(new ParcelProfile(configure));
    });

    private static string[] Messages(ValidationResult result) => result.Errors.Select(error => error.Message).ToArray();

    private static string[] Sources(ValidationResult result) => result.Errors.Select(error => error.Source).ToArray();

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Order: with the default options a chain reports the first of its rules that fails")]
    public void Validate_DefaultOptionsChainWithSeveralFailingRules_ShouldReportTheFirstChainedRule()
    {
        // Arrange — an empty label fails both of its rules.
        IValidator validator = CreateValidator(descriptor => descriptor.RuleFor(parcel => parcel.Label!)
            .NotEmpty(error => error.Message = required)
            .MinLength(3, error => error.Message = tooShort));

        // Act
        ValidationResult result = validator.Validate(new Parcel { Label = string.Empty });

        // Assert
        Messages(result).ShouldBe([required]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Order: ContinueThroughValidationChain reports a chain's failing rules in the order they are chained")]
    public void Validate_ContinueThroughValidationChain_ShouldReportRulesInChainOrder()
    {
        // Arrange — an empty label fails both of its rules.
        IValidator validator = CreateValidator(
            descriptor => descriptor.RuleFor(parcel => parcel.Label!)
                .NotEmpty(error => error.Message = required)
                .MinLength(3, error => error.Message = tooShort),
            options => options.ContinueThroughValidationChain = true);

        // Act
        ValidationResult result = validator.Validate(new Parcel { Label = string.Empty });

        // Assert
        Messages(result).ShouldBe([required, tooShort]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Order: Stop inside a nested profile reports its first declared failing member")]
    public void Validate_StopModeNestedProfileWithSeveralFailingMembers_ShouldReportTheFirstDeclaredMember()
    {
        // Arrange — both of the destination's members fail.
        IValidator validator = CreateValidator(
            descriptor => descriptor.RuleFor(parcel => parcel.Destination!).ChildRules(destination =>
            {
                destination.RuleFor(address => address.City!).NotEmpty();
                destination.RuleFor(address => address.Zip!).NotEmpty();
            }),
            options => options.ValidationMode = ValidationMode.Stop);

        // Act
        ValidationResult result = validator.Validate(new Parcel { Destination = new Address() });

        // Assert
        Sources(result).ShouldBe(["parcel => parcel.Destination.City"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Order: a collection's chain stops after its first rule, which reports the elements in sequence")]
    public void Validate_DefaultOptionsCollectionWithSeveralFailingRules_ShouldReportTheFirstChainedRuleForEveryElement()
    {
        // Arrange — both rules fail every element.
        IValidator validator = CreateValidator(descriptor => descriptor.RuleForEach(parcel => parcel.Tags!)
            .Custom((tag, context) => context.AddFailure("tags", $"first rule: {tag}"))
            .Custom((tag, context) => context.AddFailure("tags", $"second rule: {tag}")));

        // Act
        ValidationResult result = validator.Validate(new Parcel { Tags = ["a", "b"] });

        // Assert
        Messages(result).ShouldBe(["first rule: a", "first rule: b"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Order: ContinueThroughValidationChain runs a collection's rules in chain order, each over the elements in sequence")]
    public void Validate_ContinueThroughValidationChainCollection_ShouldReportRulesInChainOrderAndElementsInSequence()
    {
        // Arrange — both rules fail every element.
        IValidator validator = CreateValidator(
            descriptor => descriptor.RuleForEach(parcel => parcel.Tags!)
                .Custom((tag, context) => context.AddFailure("tags", $"first rule: {tag}"))
                .Custom((tag, context) => context.AddFailure("tags", $"second rule: {tag}")),
            options => options.ContinueThroughValidationChain = true);

        // Act
        ValidationResult result = validator.Validate(new Parcel { Tags = ["a", "b"] });

        // Assert
        Messages(result).ShouldBe(["first rule: a", "first rule: b", "second rule: a", "second rule: b"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Order: a collection of nested profiles reports element by element, each element's members in declaration order")]
    public void Validate_CollectionOfNestedProfiles_ShouldReportElementsInSequenceAndMembersInDeclarationOrder()
    {
        // Arrange — the first stop has no city; the second has neither a city nor a zip.
        IValidator validator = CreateValidator(descriptor => descriptor.RuleForEach(parcel => parcel.Stops!).UseProfile(new AddressProfile()));

        // Act
        ValidationResult result = validator.Validate(new Parcel { Stops = [new Address { Zip = "N1" }, new Address()] });

        // Assert
        Sources(result).ShouldBe(["parcel => parcel.Stops.City", "parcel => parcel.Stops.City", "parcel => parcel.Stops.Zip"]);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Order: a When block's members are evaluated where the block is declared")]
    public void Validate_WhenBlockBetweenMembers_ShouldEvaluateItsMembersInPlace()
    {
        // Arrange — every member fails; the sender and the recipient are declared inside the block.
        IValidator validator = CreateValidator(descriptor =>
        {
            descriptor.RuleFor(parcel => parcel.Label!).NotEmpty();
            descriptor.When(parcel => parcel.Destination is null, unaddressed =>
            {
                unaddressed.RuleFor(parcel => parcel.Sender!).NotEmpty();
                unaddressed.RuleFor(parcel => parcel.Recipient!).NotEmpty();
            });
            descriptor.RuleFor(parcel => parcel.Reference!).NotEmpty();
        });

        // Act
        ValidationResult result = validator.Validate(new Parcel());

        // Assert
        Sources(result).ShouldBe(
        [
            "parcel => parcel.Label",
            "parcel => parcel.Sender",
            "parcel => parcel.Recipient",
            "parcel => parcel.Reference"
        ]);
    }
}
