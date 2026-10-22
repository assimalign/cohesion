using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

public class ValidationContextStorageTests
{
    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Retained empty queues observe later additions")]
    public void AddFailure_WithRetainedCollections_PreservesLiveQueueIdentity()
    {
        // Arrange
        var context = new ValidationContext<string>("value");
        var errors = context.Errors;
        var invocations = context.Invocations;
        errors.ShouldBeEmpty();
        invocations.ShouldBeEmpty();

        // Act
        context.AddFailure("first");
        context.AddFailure("source", "second");
        context.AddFailure(new ValidationError { Message = "third" });
        context.AddInvocation(new ValidationInvocation("rule", true));

        // Assert
        context.Errors.ShouldBeSameAs(errors);
        context.Invocations.ShouldBeSameAs(invocations);
        errors.Select(error => error.Message).ShouldBe(new[] { "first", "second", "third" });
        invocations.Single().RuleName.ShouldBe("rule");
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Result collections retain their context's live queues")]
    public void Create_BeforeContextChanges_ObservesLaterAdditions()
    {
        // Arrange
        var context = new ValidationContext<string>("value");
        var result = ValidationResult.Create(context);

        // Act
        context.AddFailure("later");
        context.AddInvocation(new ValidationInvocation("later rule", true));

        // Assert
        result.Errors.ShouldBeSameAs(context.Errors);
        result.Invocations.ShouldBeSameAs(context.Invocations);
        result.IsValid.ShouldBeFalse();
        result.Errors.Single().Message.ShouldBe("later");
        result.Invocations.Single().RuleName.ShouldBe("later rule");
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Concurrent first writes keep every failure and invocation")]
    public void AddFailure_WithConcurrentFirstWrites_PublishesOneQueue()
    {
        // Arrange
        var context = new ValidationContext<int>(1);

        // Act
        Parallel.For(0, 128, index =>
        {
            context.AddFailure(index.ToString());
            context.AddInvocation(new ValidationInvocation(index.ToString(), true));
        });

        // Assert
        context.Errors.Count().ShouldBe(128);
        context.Errors.Select(error => error.Message).Distinct().Count().ShouldBe(128);
        context.Invocations.Count().ShouldBe(128);
        context.Invocations.Select(invocation => invocation.RuleName).Distinct().Count().ShouldBe(128);
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Retained custom rule contexts stay independent")]
    public void Validate_WithRetainedCustomContexts_PreservesInstancesAndCollections()
    {
        // Arrange
        var contexts = new List<IValidationContext>();
        var validator = Validator.Create(builder => builder.AddProfile(profiles => profiles
            .CreateProfile<string>(rules => rules.RuleFor(value => value.Length).Custom((value, context) =>
            {
                contexts.Add(context);
                _ = context.Errors;
                _ = context.Invocations;
            }))));

        // Act
        var first = validator.Validate("first");
        var second = validator.Validate("second");
        contexts[0].AddFailure("retained");

        // Assert
        contexts.Count.ShouldBe(2);
        contexts[0].ShouldNotBeSameAs(contexts[1]);
        contexts[0].Instance.ShouldBe(5);
        contexts[1].Instance.ShouldBe(6);
        contexts[0].Errors.Single().Message.ShouldBe("retained");
        contexts[1].Errors.ShouldBeEmpty();
        first.Errors.ShouldBeEmpty();
        second.Errors.ShouldBeEmpty();
        first.Invocations.Single().Invoked.ShouldBeTrue();
    }
}
