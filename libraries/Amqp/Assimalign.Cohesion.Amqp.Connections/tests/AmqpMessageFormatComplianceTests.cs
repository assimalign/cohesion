using System;
using System.Linq;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Runs the message-format compliance corpus (AMQP 1.0 Part 3, section 3.2) through the codec: every layout
/// the specification allows decodes, and every one it forbids is refused with the message for its rule.
/// </summary>
public class AmqpMessageFormatComplianceTests
{
    private readonly ITestOutputHelper _output;

    public AmqpMessageFormatComplianceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> CaseNames()
    {
        TheoryData<string> names = new();

        foreach (AmqpMessageFormatCase entry in AmqpMessageFormatCorpus.Cases)
        {
            names.Add(entry.Name);
        }

        return names;
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should accept or refuse each message in the format corpus as its rule requires")]
    [MemberData(nameof(CaseNames))]
    public void Decode_WithCorpusCase_ShouldFollowItsRule(string name)
    {
        // Arrange
        AmqpMessageFormatCase entry = AmqpMessageFormatCorpus.Find(name);
        byte[] payload = Convert.FromHexString(entry.Hex);

        // Act
        Exception? exception = Record.Exception(() => AmqpMessageCodec.Decode(payload));

        // Assert
        if (entry.Refusal is null)
        {
            exception.ShouldBeNull($"{entry.Rule}: {name}");
            return;
        }

        AmqpException refusal = exception.ShouldBeOfType<AmqpException>($"{entry.Rule}: {name}");
        refusal.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
        refusal.Error.ShouldNotBeNull().Condition.ShouldBe(entry.Condition ?? AmqpErrorCondition.DecodeError);
        refusal.Message.ShouldBe(entry.Refusal);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - GetBareMessage: Should hold each message in the format corpus to its layout rules")]
    [MemberData(nameof(CaseNames))]
    public void GetBareMessage_WithCorpusCase_ShouldFollowItsLayoutRule(string name)
    {
        // Arrange
        AmqpMessageFormatCase entry = AmqpMessageFormatCorpus.Find(name);
        bool refused = entry.Refusal is not null && AmqpMessageFormatCorpus.FindRule(entry.Rule).Layout;
        byte[] payload = Convert.FromHexString(entry.Hex);

        // Act
        Exception? exception = Record.Exception(() => AmqpMessageCodec.GetBareMessage(payload));

        // Assert: value rules are the decoder's, since finding the bare message skips the values.
        if (!refused)
        {
            exception.ShouldBeNull($"{entry.Rule}: {name}");
            return;
        }

        exception.ShouldBeOfType<AmqpException>($"{entry.Rule}: {name}").Message.ShouldBe(entry.Refusal);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Corpus: Should hold an allowed and a forbidden message for every format rule")]
    public void Corpus_OnEveryRule_ShouldHoldAllowedAndForbiddenCases()
    {
        // Arrange
        string[] names = AmqpMessageFormatCorpus.Cases.Select(entry => entry.Name).ToArray();

        foreach (AmqpMessageFormatRule rule in AmqpMessageFormatCorpus.Rules)
        {
            // Act
            int allowed = AmqpMessageFormatCorpus.Cases.Count(entry => entry.Rule == rule.Id && entry.Refusal is null);
            int forbidden = AmqpMessageFormatCorpus.Cases.Count(entry => entry.Rule == rule.Id && entry.Refusal is not null);

            _output.WriteLine($"{rule.Id} ({rule.Reference}): {allowed} allowed, {forbidden} forbidden. {rule.Statement}");

            // Assert
            allowed.ShouldBeGreaterThan(0, rule.Id);
            forbidden.ShouldBeGreaterThan(0, rule.Id);
        }

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
        AmqpMessageFormatCorpus.Cases.ShouldAllBe(entry => AmqpMessageFormatCorpus.Rules.Any(rule => rule.Id == entry.Rule));
    }
}
