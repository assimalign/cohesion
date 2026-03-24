using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Resilience.Tests;

public class ResilienceExceptionTests
{
    [Fact(DisplayName = "Cohesion Test [Resilience] - Exception: PipelineFailure sets code and operation key")]
    public void Exception_PipelineFailure_ShouldSetCodeAndOperationKey()
    {
        ResilienceException exception = ResilienceException.PipelineFailure("operation-key");

        exception.Code.ShouldBe(ResilienceErrorCode.PipelineFailure);
        exception.OperationKey.ToString().ShouldBe("operation-key");
    }

    [Fact(DisplayName = "Cohesion Test [Resilience] - Exception: ExecutionRejected inherits resilience exception")]
    public void Exception_ExecutionRejected_ShouldInheritFromResilienceException()
    {
        ExecutionRejectedException exception = new("rejected");

        exception.ShouldBeOfType<ExecutionRejectedException>();
        exception.ShouldBeAssignableTo<ResilienceException>();
        exception.Code.ShouldBe(ResilienceErrorCode.ExecutionRejected);
    }
}
