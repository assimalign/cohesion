using System;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.Amqp.Tests;

public class AmqpSaslServerStepTests
{
    [Fact(DisplayName = "Cohesion Test [Amqp] - Challenge: Should carry the challenge and leave the exchange incomplete")]
    public void Challenge_WithData_ShouldBeIncomplete()
    {
        // Act
        AmqpSaslServerStep step = AmqpSaslServerStep.Challenge(new byte[] { 1, 2 });

        // Assert
        step.IsComplete.ShouldBeFalse();
        step.Code.ShouldBeNull();
        step.Data.ToArray().ShouldBe(new byte[] { 1, 2 });
        step.Identity.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp] - Success: Should complete with the ok code, the identity and any additional data")]
    public void Success_WithIdentityAndData_ShouldCompleteOk()
    {
        // Act
        AmqpSaslServerStep step = AmqpSaslServerStep.Success("alice", new byte[] { 9 });
        AmqpSaslServerStep bare = AmqpSaslServerStep.Success();

        // Assert
        step.IsComplete.ShouldBeTrue();
        step.Code.ShouldBe(AmqpSaslCode.Ok);
        step.Identity.ShouldBe("alice");
        step.Data.ToArray().ShouldBe(new byte[] { 9 });
        bare.Identity.ShouldBeNull();
        bare.Data.IsEmpty.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Fail: Should complete with the failure code given, authentication by default")]
    [InlineData(null, AmqpSaslCode.Auth)]
    [InlineData(AmqpSaslCode.Sys, AmqpSaslCode.Sys)]
    [InlineData(AmqpSaslCode.SysPerm, AmqpSaslCode.SysPerm)]
    [InlineData(AmqpSaslCode.SysTemp, AmqpSaslCode.SysTemp)]
    public void Fail_WithCode_ShouldCompleteWithCode(AmqpSaslCode? code, AmqpSaslCode expected)
    {
        // Act
        AmqpSaslServerStep step = code is null ? AmqpSaslServerStep.Fail() : AmqpSaslServerStep.Fail(code.Value);

        // Assert
        step.IsComplete.ShouldBeTrue();
        step.Code.ShouldBe(expected);
        step.Identity.ShouldBeNull();
        step.Data.IsEmpty.ShouldBeTrue();
    }

    [Theory(DisplayName = "Cohesion Test [Amqp] - Fail: Should refuse the ok code and codes the specification does not define")]
    [InlineData(AmqpSaslCode.Ok)]
    [InlineData((AmqpSaslCode)5)]
    public void Fail_WithOkOrUndefinedCode_ShouldThrowArgumentOutOfRangeException(AmqpSaslCode code)
    {
        // Act
        ArgumentOutOfRangeException exception = Should.Throw<ArgumentOutOfRangeException>(() => AmqpSaslServerStep.Fail(code));

        // Assert
        exception.ParamName.ShouldBe("code");
    }
}
