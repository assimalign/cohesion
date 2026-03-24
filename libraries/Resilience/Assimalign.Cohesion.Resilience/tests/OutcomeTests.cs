using System;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Resilience.Tests;

public class OutcomeTests
{
    [Fact(DisplayName = "Cohesion Test [Resilience] - Outcome: ThrowIfException rethrows the captured exception")]
    public void Outcome_ThrowIfException_ShouldRethrowCapturedException()
    {
        InvalidOperationException expected = new("boom");
        Outcome outcome = expected;

        InvalidOperationException actual = Should.Throw<InvalidOperationException>(() => outcome.ThrowIfException());

        actual.Message.ShouldBe(expected.Message);
    }

    [Fact(DisplayName = "Cohesion Test [Resilience] - Outcome: Generic success exposes the result")]
    public void Outcome_GenericSuccess_ShouldExposeResult()
    {
        Outcome<int> outcome = 42;

        outcome.IsSuccess(out int result).ShouldBeTrue();
        result.ShouldBe(42);
    }
}
