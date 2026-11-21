using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;
using Assimalign.Cohesion.Connections;

using static Assimalign.Cohesion.Amqp.Connections.Tests.AmqpPeerScript;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Runs the terminus compliance corpus (AMQP 1.0 Part 3, section 3.5) through the codec, the attach rules
/// and the engine: every source and target the specification allows is accepted, and every one it forbids
/// is refused with the description for its rule, ending only the link where the model can hold the value
/// and the connection where it cannot.
/// </summary>
public class AmqpTerminusComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public AmqpTerminusComplianceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> CaseNames()
    {
        return Names(entry => true);
    }

    public static TheoryData<string> OpeningCaseNames()
    {
        return Names(entry => entry.Opening);
    }

    public static TheoryData<string> ValueCaseNames()
    {
        return Names(entry => AmqpTerminusCorpus.FindRule(entry.Rule).Values);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - FindViolation: Should accept or refuse each attach in the terminus corpus as its rule requires")]
    [MemberData(nameof(CaseNames))]
    public void FindViolation_WithCorpusCase_ShouldFollowItsRule(string name)
    {
        // Arrange
        AmqpTerminusCase entry = AmqpTerminusCorpus.Find(name);
        byte[] encoded = Convert.FromHexString(entry.Hex);

        // Act
        Exception? exception = Record.Exception(() => Decode(encoded));
        string? violation = exception is null ? AmqpAttachRules.FindViolation(Decode(encoded), entry.Opening) : null;

        // Assert
        if (entry.Decoding)
        {
            AmqpException refusal = exception.ShouldBeOfType<AmqpException>($"{entry.Rule}: {name}");
            refusal.ErrorCode.ShouldBe(AmqpErrorCode.ProtocolViolation);
            refusal.Error.ShouldNotBeNull().Condition.ShouldBe(entry.Condition ?? AmqpErrorCondition.InvalidField);
            refusal.Message.ShouldBe(entry.Refusal);
            return;
        }

        exception.ShouldBeNull($"{entry.Rule}: {name}");
        violation.ShouldBe(entry.Refusal, $"{entry.Rule}: {name}");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - OnAttach: Should accept each opening attach in the terminus corpus or refuse it with the scope its rule requires")]
    [MemberData(nameof(OpeningCaseNames))]
    public async Task OnAttach_WithOpeningCorpusCase_ShouldFollowItsRule(string name)
    {
        // Arrange
        AmqpTerminusCase entry = AmqpTerminusCorpus.Find(name);
        using CancellationTokenSource timeout = new(_testTimeout);
        await using AmqpScriptedHost host = await AmqpScriptedHost.AcceptAsync(cancellationToken: timeout.Token);
        AmqpTestPeer peer = host.Peer;
        await peer.BeginAsync(null, timeout.Token);
        IAmqpSession session = await host.Connection.AcceptSessionAsync(timeout.Token);

        // Act
        await peer.WriteFrameBodyAsync(0, Convert.FromHexString(entry.Hex), timeout.Token);

        // Assert
        if (entry.Refusal is null)
        {
            IAmqpLinkRequest request = await session.AcceptLinkAsync(timeout.Token);
            request.Name.ShouldBe("link");
            host.Connection.State.ShouldBe(ConnectionState.Open);
            return;
        }

        if (entry.Decoding)
        {
            // The model cannot hold the value, so the attach cannot be answered and the connection ends.
            AmqpError closed = (await peer.ReadCloseAsync(timeout.Token)).ShouldNotBeNull();
            closed.Condition.ShouldBe(entry.Condition ?? AmqpErrorCondition.InvalidField);
            closed.Description.ShouldBe(entry.Refusal);
            return;
        }

        AmqpAttachPerformative answer = await ReadUntilAsync<AmqpAttachPerformative>(peer, timeout.Token);
        AmqpDetachPerformative detach = await ReadUntilAsync<AmqpDetachPerformative>(peer, timeout.Token);
        answer.Name.ShouldBe("link");
        detach.Handle.ShouldBe(answer.Handle);
        detach.Closed.ShouldBeTrue();
        AmqpError error = detach.Error.ShouldNotBeNull();
        error.Condition.ShouldBe(AmqpErrorCondition.InvalidField);
        error.Description.ShouldBe(entry.Refusal);
        host.Connection.State.ShouldBe(ConnectionState.Open);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should write each terminus value in the corpus that AMQP allows and refuse each one it forbids")]
    [MemberData(nameof(ValueCaseNames))]
    public void Encode_WithValueCorpusCase_ShouldFollowItsRule(string name)
    {
        // Arrange
        AmqpTerminusCase entry = AmqpTerminusCorpus.Find(name);
        AmqpAttachPerformative attach = Decode(Convert.FromHexString(entry.Hex));
        using AmqpBufferWriter writer = new();

        // Act
        Exception? exception = Record.Exception(() => attach.Encode(writer));

        // Assert
        if (entry.Refusal is null)
        {
            exception.ShouldBeNull($"{entry.Rule}: {name}");
            AmqpAttachRules.FindViolation(Decode(writer.WrittenSpan.ToArray()), entry.Opening).ShouldBeNull();
            return;
        }

        ArgumentException refusal = exception.ShouldBeOfType<ArgumentException>($"{entry.Rule}: {name}");
        refusal.ParamName.ShouldBe("value");
        refusal.Message.ShouldStartWith(entry.Refusal);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should refuse a terminus value the model holds and AMQP does not allow, writing nothing")]
    [InlineData("default-symbol-mode")]
    [InlineData("default-symbol-in-modes")]
    [InlineData("outcome-as-policy")]
    [InlineData("outcome-as-filter")]
    public void Encode_WithModelOnlyValue_ShouldThrowArgumentException(string scenario)
    {
        // Arrange: values only the model can hold, since the wire carries no default symbol and decodes an
        // outcome as an outcome.
        (AmqpSource source, string expected) = CreateModelOnlySource(scenario);
        using AmqpBufferWriter writer = new();

        // Act
        ArgumentException exception = Should.Throw<ArgumentException>(() => AmqpTypeCodec.WriteSource(writer, source));

        // Assert
        exception.ParamName.ShouldBe("value");
        exception.Message.ShouldStartWith(expected);
        writer.Length.ShouldBe(0);
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Corpus: Should hold an allowed case for every terminus rule and a forbidden one for every rule that forbids")]
    public void Corpus_OnEveryRule_ShouldHoldAllowedAndForbiddenCases()
    {
        // Arrange
        string[] names = AmqpTerminusCorpus.Cases.Select(entry => entry.Name).ToArray();

        foreach (AmqpTerminusRule rule in AmqpTerminusCorpus.Rules)
        {
            // Act
            int allowed = AmqpTerminusCorpus.Cases.Count(entry => entry.Rule == rule.Id && entry.Refusal is null);
            int forbidden = AmqpTerminusCorpus.Cases.Count(entry => entry.Rule == rule.Id && entry.Refusal is not null);

            _output.WriteLine($"{rule.Id} ({rule.Reference}): {allowed} allowed, {forbidden} forbidden. {rule.Statement}");

            // Assert
            allowed.ShouldBeGreaterThan(0, rule.Id);
            (forbidden > 0).ShouldBe(rule.Forbids, rule.Id);
        }

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
        AmqpTerminusCorpus.Cases.ShouldAllBe(entry => AmqpTerminusCorpus.Rules.Any(rule => rule.Id == entry.Rule));
        AmqpTerminusCorpus.Cases.ShouldAllBe(entry => entry.Refusal != null || !entry.Decoding);

        // A value the model holds is refused with its link, never by decoding.
        AmqpTerminusCorpus.Cases
            .Where(entry => AmqpTerminusCorpus.FindRule(entry.Rule).Values)
            .ShouldAllBe(entry => !entry.Decoding);
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should give a source whose fields are absent or null the defaults")]
    [InlineData("a minimal source")]
    [InlineData("a source whose every field is null")]
    public void Decode_WithMinimalSource_ShouldTakeTheDefaults(string name)
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(AmqpTerminusCorpus.Find(name).Hex);

        // Act
        AmqpSource source = Decode(encoded).Source.ShouldBeOfType<AmqpSource>();

        // Assert
        source.Address.ShouldBeNull();
        source.Durable.ShouldBe(AmqpTerminusDurability.None);
        source.ExpiryPolicy.ShouldBe(AmqpTerminusExpiryPolicy.SessionEnd);
        source.Timeout.ShouldBe(0u);
        source.Dynamic.ShouldBeFalse();
        source.DynamicNodeProperties.ShouldBeNull();
        source.DistributionMode.ShouldBeNull();
        source.Filter.ShouldBeNull();
        source.DefaultOutcome.ShouldBeNull();
        source.Outcomes.ShouldBeNull();
        source.Capabilities.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should give a target whose fields are absent the defaults")]
    public void Decode_WithMinimalTarget_ShouldTakeTheDefaults()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(AmqpTerminusCorpus.Find("a minimal target").Hex);

        // Act
        AmqpTarget target = Decode(encoded).Target.ShouldBeOfType<AmqpTarget>();

        // Assert
        target.Address.ShouldBeNull();
        target.Durable.ShouldBe(AmqpTerminusDurability.None);
        target.ExpiryPolicy.ShouldBe(AmqpTerminusExpiryPolicy.SessionEnd);
        target.Timeout.ShouldBe(0u);
        target.Dynamic.ShouldBeFalse();
        target.DynamicNodeProperties.ShouldBeNull();
        target.Capabilities.ShouldBeNull();
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Encode: Should write a source and a target that hold only defaults as empty lists")]
    public void Encode_WithDefaultTermini_ShouldWriteEmptyLists()
    {
        // Arrange
        using AmqpBufferWriter source = new();
        using AmqpBufferWriter target = new();

        // Act
        AmqpTypeCodec.WriteSource(source, new AmqpSource());
        AmqpTypeCodec.WriteTarget(target, new AmqpTarget());

        // Assert: the defaults are what an absent field means, so nothing is written for them.
        Convert.ToHexStringLower(source.WrittenSpan).ShouldBe("00532845");
        Convert.ToHexStringLower(target.WrittenSpan).ShouldBe("00532945");
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should read each lifetime policy as its instance")]
    [InlineData("the delete-on-close lifetime policy", "delete-on-close")]
    [InlineData("the delete-on-no-links lifetime policy", "delete-on-no-links")]
    [InlineData("the delete-on-no-messages lifetime policy", "delete-on-no-messages")]
    [InlineData("the delete-on-no-links-or-messages lifetime policy", "delete-on-no-links-or-messages")]
    [InlineData("a lifetime policy under its symbolic descriptor", "delete-on-close")]
    public void Decode_WithLifetimePolicy_ShouldReadItsInstance(string name, string policy)
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(AmqpTerminusCorpus.Find(name).Hex);

        // Act
        AmqpSource source = Decode(encoded).Source.ShouldBeOfType<AmqpSource>();

        // Assert
        object? value = source.DynamicNodeProperties.ShouldNotBeNull()[AmqpNodeProperties.LifetimePolicy];
        value.ShouldBeOfType<AmqpLifetimePolicy>().ToString().ShouldBe(policy);
        value.ShouldBeSameAs(policy switch
        {
            "delete-on-close" => AmqpLifetimePolicy.DeleteOnClose,
            "delete-on-no-links" => AmqpLifetimePolicy.DeleteOnNoLinks,
            "delete-on-no-messages" => AmqpLifetimePolicy.DeleteOnNoMessages,
            _ => AmqpLifetimePolicy.DeleteOnNoLinksOrMessages
        });
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Decode: Should keep a lifetime policy an extension defines as a described value")]
    public void Decode_WithExtensionLifetimePolicy_ShouldKeepTheDescribedValue()
    {
        // Arrange
        byte[] encoded = Convert.FromHexString(AmqpTerminusCorpus.Find("a lifetime policy an extension defines").Hex);

        // Act
        AmqpSource source = Decode(encoded).Source.ShouldBeOfType<AmqpSource>();

        // Assert
        AmqpDescribedValue policy = source.DynamicNodeProperties.ShouldNotBeNull()[AmqpNodeProperties.LifetimePolicy].ShouldBeOfType<AmqpDescribedValue>();
        policy.Descriptor.ShouldBe(new AmqpSymbol("com.example:delete-at-noon:list"));
    }

    private static (AmqpSource Source, string Expected) CreateModelOnlySource(string scenario)
    {
        string modesInvalid = string.Format(CultureInfo.CurrentCulture, ErrorMessages.SupportedDistributionModesInvalid, "source");

        return scenario switch
        {
            "default-symbol-mode" => (CreateDynamicSource(AmqpNodeProperties.SupportedDistributionModes, default(AmqpSymbol)), modesInvalid),
            "default-symbol-in-modes" => (
                CreateDynamicSource(AmqpNodeProperties.SupportedDistributionModes, new AmqpSymbol[] { AmqpDistributionMode.Move, default }),
                modesInvalid),
            "outcome-as-policy" => (
                CreateDynamicSource(AmqpNodeProperties.LifetimePolicy, AmqpAccepted.Instance),
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.LifetimePolicyInvalid, "source")),
            _ => (
                new AmqpSource { Address = "queue", Filter = new Dictionary<AmqpSymbol, object?> { ["f"] = AmqpAccepted.Instance } },
                string.Format(CultureInfo.CurrentCulture, ErrorMessages.FilterNotDescribed, "f"))
        };
    }

    private static AmqpSource CreateDynamicSource(AmqpSymbol property, object? value)
    {
        return new AmqpSource
        {
            Address = "tmp-1",
            Dynamic = true,
            DynamicNodeProperties = new Dictionary<AmqpSymbol, object?> { [property] = value }
        };
    }

    private static TheoryData<string> Names(Func<AmqpTerminusCase, bool> predicate)
    {
        TheoryData<string> names = new();

        foreach (AmqpTerminusCase entry in AmqpTerminusCorpus.Cases.Where(predicate))
        {
            names.Add(entry.Name);
        }

        return names;
    }

    private static AmqpAttachPerformative Decode(byte[] encoded)
    {
        AmqpReader reader = new(encoded);
        AmqpPerformative performative = AmqpPerformative.Decode(ref reader);

        reader.IsEnd.ShouldBeTrue();
        return performative.ShouldBeOfType<AmqpAttachPerformative>();
    }
}
