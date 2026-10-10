using System;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

using Assimalign.Cohesion.ObjectValidation;
using Assimalign.Cohesion.Web.Testing;
using Assimalign.Cohesion.Web.Validation.Tests.TestObjects;

namespace Assimalign.Cohesion.Web.Validation.Tests;

/// <summary>
/// Registration through <see cref="EndpointValidationOptions"/> and <c>AddValidation</c>: validators keyed by
/// model type, without reflection, and rejected when ambiguous.
/// </summary>
public class EndpointValidationOptionsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Options: validation is on by default and no validator is registered")]
    public void EndpointValidationOptions_New_ShouldBeEnabledAndEmpty()
    {
        // Act
        EndpointValidationOptions options = new();

        // Assert
        options.Enabled.ShouldBeTrue();
        options.Validators.ShouldBeEmpty();
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Options: AddValidator registers the validator for every type it has a profile for")]
    public void AddValidator_ValidatorWithProfiles_ShouldRegisterEachProfileType()
    {
        // Arrange
        IValidator validator = Validator.Create(builder => builder.AddProfile(new CustomerProfile()));
        EndpointValidationOptions options = new();

        // Act
        options.AddValidator(validator);

        // Assert — keyed by the profile's typeof(T), not by inspecting anything.
        options.Validators.Count.ShouldBe(1);
        options.Validators[typeof(Customer)].ShouldBeSameAs(validator);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Options: AddValidator rejects a validator with no profiles")]
    public void AddValidator_ValidatorWithoutProfiles_ShouldThrow()
    {
        // Arrange
        EndpointValidationOptions options = new();

        // Act
        Action act = () => options.AddValidator(new ProfilelessValidator());

        // Assert
        act.ShouldThrow<ArgumentException>().Message.ShouldContain("AddValidator<T>", Case.Sensitive);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Options: AddValidator<T> registers a validator for the named type")]
    public void AddValidatorOfT_AnyValidator_ShouldRegisterForTheType()
    {
        // Arrange
        ProfilelessValidator validator = new();
        EndpointValidationOptions options = new();

        // Act
        options.AddValidator<Note>(validator);

        // Assert
        options.Validators[typeof(Note)].ShouldBeSameAs(validator);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Options: AddProfile builds a validator for the profile's type")]
    public void AddProfile_Profile_ShouldRegisterAValidatorForItsType()
    {
        // Arrange
        EndpointValidationOptions options = new();

        // Act
        options.AddProfile(new CustomerProfile());

        // Assert — the built validator reports every failing member, not only the first.
        IValidator validator = options.Validators[typeof(Customer)];
        ValidationResult result = validator.Validate(new Customer { Name = "", Age = 1 });
        result.Errors.ShouldContain(error => error.Source.Contains("Name"));
        result.Errors.ShouldContain(error => error.Source.Contains("Age"));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Options: a second validator for the same type is rejected")]
    public void AddProfile_TypeAlreadyRegistered_ShouldThrow()
    {
        // Arrange
        EndpointValidationOptions options = new();
        options.AddProfile(new CustomerProfile());

        // Act
        Action again = () => options.AddValidator(Validator.Create(builder => builder.AddProfile(new CustomerProfile())));

        // Assert
        again.ShouldThrow<InvalidOperationException>().Message.ShouldContain(nameof(Customer), Case.Sensitive);
        options.Validators.Count.ShouldBe(1);
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Options: null arguments are rejected")]
    public void Add_NullArguments_ShouldThrow()
    {
        // Arrange
        EndpointValidationOptions options = new();

        // Act & Assert
        Should.Throw<ArgumentNullException>(() => options.AddValidator(null!));
        Should.Throw<ArgumentNullException>(() => options.AddValidator<Customer>(null!));
        Should.Throw<ArgumentNullException>(() => options.AddProfile<Customer>(null!));
    }

    [Fact(DisplayName = "Cohesion Test [Web.Validation] - Registration: AddValidation rejects a null callback")]
    public async Task AddValidation_NullConfigure_ShouldThrow()
    {
        // Arrange
        await using WebApplicationTestFactory factory = new();

        // Act
        Action act = () => factory.Builder.Services.AddValidation(null!);

        // Assert
        act.ShouldThrow<ArgumentNullException>();
    }
}
