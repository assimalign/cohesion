using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.ObjectValidation.Tests;

/// <summary>
/// A validator is shared: Web.Validation registers one per model type and every request validates through
/// it. Concurrent validations and concurrently built validators must therefore share no evaluation state,
/// and the times each validation reports are its own, in <see cref="TimeSpan"/> ticks (#1291).
/// </summary>
public class ValidatorConcurrencyTests
{
    public sealed class OrderLine
    {
        public string? Sku { get; set; }
    }

    public sealed class Order
    {
        public string? Reference { get; set; }

        public int Quantity { get; set; }

        public OrderLine? Line { get; set; }
    }

    public sealed class OrderProfile : ValidationProfile<Order>
    {
        public override void Configure(IValidationRuleDescriptor<Order> descriptor)
        {
            descriptor.RuleFor(order => order.Reference!).NotEmpty();
            descriptor.RuleFor(order => order.Quantity).GreaterThan(0);
            descriptor.RuleFor(order => order.Line!).ChildRules(line => line.RuleFor(l => l.Sku!).NotEmpty());
        }
    }

    /// <summary>A profile with one rule that takes at least 50 ms.</summary>
    public sealed class SlowOrderProfile : ValidationProfile<Order>
    {
        public override void Configure(IValidationRuleDescriptor<Order> descriptor)
        {
            descriptor.RuleFor(order => order.Reference!).Custom((reference, context) => Thread.Sleep(50));
        }
    }

    private static IValidator CreateValidator() => Validator.Create(builder => builder.AddProfile(new OrderProfile()));

    private static Order CreateOrder(bool valid) => valid
        ? new Order { Reference = "A-1", Quantity = 2, Line = new OrderLine { Sku = "S-1" } }
        : new Order { Reference = "", Quantity = 0, Line = new OrderLine { Sku = "" } };

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Concurrency: validations run concurrently on shared and newly built validators each report their own errors")]
    public void Validate_ConcurrentValidations_ShouldReportEachValidationsOwnErrors()
    {
        // Arrange — every other order fails all three members; every tenth validation also builds its own
        // validator while the others run, as applications build them during startup.
        IValidator shared = CreateValidator();
        const int validations = 4000;
        int[] errorCounts = new int[validations];
        ParallelOptions parallelism = new() { MaxDegreeOfParallelism = Math.Max(4, Environment.ProcessorCount) };

        // Act
        Parallel.For(0, validations, parallelism, i =>
        {
            IValidator validator = i % 10 == 0 ? CreateValidator() : shared;
            errorCounts[i] = validator.Validate(CreateOrder(valid: i % 2 == 0)).Errors.Count();
        });

        // Assert
        for (int i = 0; i < validations; i++)
        {
            errorCounts[i].ShouldBe(i % 2 == 0 ? 0 : 3, $"validation {i}");
        }
    }

    [Fact(DisplayName = "Cohesion Test [ObjectValidation] - Timing: a validation and its rule invocations report elapsed time in TimeSpan ticks")]
    public void Validate_RuleThatTakesTime_ShouldReportElapsedTimeInTimeSpanTicks()
    {
        // Arrange
        IValidator validator = Validator.Create(builder => builder.AddProfile(new SlowOrderProfile()));

        // Act
        ValidationResult result = validator.Validate(CreateOrder(valid: true));

        // Assert — at least the rule's 50 ms, and nowhere near the hundredfold reading that Stopwatch ticks
        // produced where the timer does not run at 10 MHz.
        ValidationInvocation invocation = result.Invocations.ShouldHaveSingleItem();
        invocation.Invoked.ShouldBeTrue();
        invocation.ElapsedMilliseconds!.Value.ShouldBeGreaterThanOrEqualTo(45);
        invocation.ElapsedMilliseconds!.Value.ShouldBeLessThan(2000);
        result.ValidationElapsedMilliseconds!.Value.ShouldBeGreaterThanOrEqualTo(45);
        result.ValidationElapsedMilliseconds!.Value.ShouldBeLessThan(2000);
    }
}
