using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;
using Xunit.Abstractions;

using Assimalign.Cohesion.Amqp;
using Assimalign.Cohesion.Amqp.Connections.Internal;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Runs the header-phase compliance corpus (AMQP 1.0 Part 2, sections 2.2 to 2.4; Part 5, sections 5.2 and 5.3)
/// against a listener and a factory: a scripted peer plays the other side of each establishment, checks
/// every byte the engine answers with, and the case states whether the connection opens or establishment
/// fails, and with which exact exception.
/// </summary>
/// <remarks>
/// A factory's failure is the exception its <c>ConnectAsync</c> throws. A listener keeps accepting when one
/// connection fails, and reports the failure only through the <c>Assimalign.Cohesion.Connections</c> event
/// source, so these tests run in a collection of their own.
/// </remarks>
[Collection(AmqpHeaderPhaseCollection.Name)]
public class AmqpHeaderPhaseComplianceTests
{
    private static readonly TimeSpan _testTimeout = TimeSpan.FromSeconds(10);

    private readonly ITestOutputHelper _output;

    public AmqpHeaderPhaseComplianceTests(ITestOutputHelper output)
    {
        _output = output;
    }

    public static TheoryData<string> CaseNames()
    {
        TheoryData<string> names = new();

        foreach (AmqpHeaderPhaseCase entry in AmqpHeaderPhaseCorpus.Cases)
        {
            names.Add(entry.Name);
        }

        return names;
    }

    [Theory(DisplayName = "Cohesion Test [Amqp.Connections] - EstablishAsync: Should open or refuse each establishment in the header-phase corpus as its rule requires")]
    [MemberData(nameof(CaseNames))]
    public async Task EstablishAsync_WithCorpusCase_ShouldFollowItsRule(string name)
    {
        // Arrange
        AmqpHeaderPhaseCase entry = AmqpHeaderPhaseCorpus.Find(name);
        using CancellationTokenSource timeout = new(_testTimeout);
        using AmqpUpgradeFailureRecorder failures = new();
        (AmqpTestPeer peer, AmqpConnectionListener? listener, Task<IAmqpConnection> establish) = Start(entry, timeout.Token);
        IAmqpConnection? connection = null;

        try
        {
            // Act
            for (int index = 0; index < entry.Script.Count; index++)
            {
                await RunStepAsync(entry, index, peer, timeout.Token);
            }

            // Assert
            if (entry.Outcome.Opened)
            {
                connection = await establish.WaitAsync(timeout.Token);
                entry.Outcome.Verify?.Invoke(connection);
            }
            else if (entry.Role == AmqpHeaderPhaseRole.Factory)
            {
                AmqpException exception = await Should.ThrowAsync<AmqpException>(establish.WaitAsync(timeout.Token));

                exception.ErrorCode.ShouldBe(entry.Outcome.ErrorCode, entry.Name);
                (exception.Error?.Condition).ShouldBe(entry.Outcome.Condition, entry.Name);
                exception.Message.ShouldBe(entry.Outcome.Message, entry.Name);
            }
            else
            {
                (string exceptionType, string exceptionMessage) = await failures.WaitForFailureAsync(peer.Carrier.Id, timeout.Token);

                exceptionType.ShouldBe(typeof(AmqpException).FullName, entry.Name);
                exceptionMessage.ShouldBe(entry.Outcome.Message, entry.Name);
                establish.IsCompleted.ShouldBeFalse(entry.Name);
            }
        }
        finally
        {
            // Hang up first, so neither the connection nor the listener waits for a close the peer never sends.
            await peer.DisposeAsync();

            if (connection is not null)
            {
                await connection.DisposeAsync();
            }

            if (listener is not null)
            {
                await listener.DisposeAsync();
                await Record.ExceptionAsync(() => establish);
            }
        }
    }

    [Fact(DisplayName = "Cohesion Test [Amqp.Connections] - Corpus: Should hold an accepted case for every header-phase rule and a refused one for every rule that forbids")]
    public void Corpus_OnEveryRule_ShouldHoldAcceptedAndRefusedCases()
    {
        // Arrange
        string[] names = AmqpHeaderPhaseCorpus.Cases.Select(entry => entry.Name).ToArray();

        foreach (AmqpHeaderPhaseRule rule in AmqpHeaderPhaseCorpus.Rules)
        {
            // Act
            AmqpHeaderPhaseCase[] cases = AmqpHeaderPhaseCorpus.Cases.Where(entry => entry.Rule == rule.Id).ToArray();
            int refused = cases.Count(entry => entry.Refuses);
            int accepted = cases.Length - refused;
            int listener = cases.Count(entry => entry.Role == AmqpHeaderPhaseRole.Listener);
            int factory = cases.Length - listener;

            _output.WriteLine($"{rule.Id} ({rule.Reference}): {accepted} accepted, {refused} refused; {listener} against the listener, {factory} against the factory. {rule.Statement}");

            // Assert
            cases.ShouldNotBeEmpty(rule.Id);
            (refused > 0).ShouldBe(rule.Forbids, rule.Id);

            if (rule.Leniency)
            {
                accepted.ShouldBeGreaterThan(0, rule.Id);
            }
        }

        names.Distinct(StringComparer.Ordinal).Count().ShouldBe(names.Length);
        AmqpHeaderPhaseCorpus.Cases.ShouldAllBe(entry => AmqpHeaderPhaseCorpus.Rules.Any(rule => rule.Id == entry.Rule));
        AmqpHeaderPhaseCorpus.Cases.ShouldAllBe(entry => entry.Outcome.Opened || entry.Outcome.Message != null);
        AmqpHeaderPhaseCorpus.Cases.Count(entry => entry.Role == AmqpHeaderPhaseRole.Listener).ShouldBeGreaterThan(0);
        AmqpHeaderPhaseCorpus.Cases.Count(entry => entry.Role == AmqpHeaderPhaseRole.Factory).ShouldBeGreaterThan(0);

        // A factory requires TLS by asking for it, so only a listener's case can require it.
        AmqpHeaderPhaseCorpus.Cases.ShouldAllBe(entry => entry.Tls != AmqpHeaderPhaseTls.Required || entry.Role == AmqpHeaderPhaseRole.Listener);
    }

    private static (AmqpTestPeer Peer, AmqpConnectionListener? Listener, Task<IAmqpConnection> Establish) Start(
        AmqpHeaderPhaseCase entry,
        CancellationToken cancellationToken)
    {
        if (entry.Role == AmqpHeaderPhaseRole.Listener)
        {
            AmqpConnectionListenerOptions options = new()
            {
                TlsLayer = entry.Tls == AmqpHeaderPhaseTls.None ? null : new AmqpTestTlsLayer(),
                RequireTls = entry.Tls == AmqpHeaderPhaseTls.Required
            };

            switch (entry.Sasl)
            {
                case AmqpHeaderPhaseSasl.Anonymous:
                    options.SaslMechanisms.Add(new AmqpSaslAnonymousServerMechanism());
                    break;
                case AmqpHeaderPhaseSasl.Challenge:
                    options.SaslMechanisms.Add(new TestChallengeSaslServerMechanism());
                    break;
            }

            (AmqpTestPeer peer, AmqpConnectionListener listener, Task<IAmqpConnection> accept) = AmqpTestPeer.StartListener(options, cancellationToken);

            return (peer, listener, accept);
        }

        AmqpConnectionFactoryOptions factoryOptions = new()
        {
            TlsLayer = entry.Tls == AmqpHeaderPhaseTls.Layer ? new AmqpTestTlsLayer() : null,
            SaslMechanism = entry.Sasl switch
            {
                AmqpHeaderPhaseSasl.Anonymous => new AmqpSaslAnonymousClientMechanism(),
                AmqpHeaderPhaseSasl.Challenge => new TestChallengeSaslClientMechanism(),
                _ => null
            }
        };

        (AmqpTestPeer server, AmqpConnectionFactory _, Task<IAmqpConnection> connect) = AmqpTestPeer.StartFactory(factoryOptions, cancellationToken);

        return (server, null, connect);
    }

    private static async Task RunStepAsync(AmqpHeaderPhaseCase entry, int index, AmqpTestPeer peer, CancellationToken cancellationToken)
    {
        AmqpHeaderPhaseStep step = entry.Script[index];

        try
        {
            switch (step.Kind)
            {
                case AmqpHeaderPhaseStepKind.Write:
                    await peer.WriteAsync(step.Bytes!, cancellationToken);
                    break;
                case AmqpHeaderPhaseStepKind.ExpectHeader:
                    (await peer.ReadProtocolHeaderAsync(cancellationToken)).ShouldBe(step.Bytes!);
                    break;
                case AmqpHeaderPhaseStepKind.ExpectFrame:
                    AmqpTestFrame frame = await peer.ReadNonEmptyFrameAsync(cancellationToken);
                    bool saslPerformative = step.Performative!.StartsWith("sasl-", StringComparison.Ordinal);

                    frame.Performative!.PerformativeName.ShouldBe(step.Performative);
                    frame.Header.Type.ShouldBe(saslPerformative ? AmqpFrame.SaslType : AmqpFrame.AmqpType);
                    step.Verify?.Invoke(frame.Performative);
                    break;
                case AmqpHeaderPhaseStepKind.ExpectEmptyFrame:
                    AmqpTestFrame emptyFrame = (await ReadWithinAsync(peer, step.Duration, cancellationToken))
                        .ShouldNotBeNull($"No frame arrived within {step.Duration.TotalMilliseconds} ms.");

                    emptyFrame.Performative.ShouldBeNull();
                    emptyFrame.Header.Type.ShouldBe(AmqpFrame.AmqpType);
                    emptyFrame.Header.Channel.ShouldBe((ushort)0);
                    break;
                case AmqpHeaderPhaseStepKind.ExpectClose:
                    AmqpTestFrame closeFrame = await peer.ReadNonEmptyFrameAsync(cancellationToken);
                    AmqpClosePerformative close = closeFrame.Performative.ShouldBeOfType<AmqpClosePerformative>();

                    closeFrame.Header.Channel.ShouldBe((ushort)0);
                    (close.Error?.Condition).ShouldBe(step.Condition);

                    if (step.Description is not null)
                    {
                        close.Error.ShouldNotBeNull().Description.ShouldBe(step.Description);
                    }

                    break;
                case AmqpHeaderPhaseStepKind.ExpectSilence:
                    AmqpTestFrame? unexpected = await ReadWithinAsync(peer, step.Duration, cancellationToken);

                    unexpected.ShouldBeNull($"The engine wrote a frame within {step.Duration.TotalMilliseconds} ms.");
                    break;
                case AmqpHeaderPhaseStepKind.ExpectEnd:
                    List<AmqpTestFrame> rest = [];

                    while (await peer.ReadFrameAsync(cancellationToken) is AmqpTestFrame next)
                    {
                        rest.Add(next);
                    }

                    rest.ShouldBeEmpty();
                    break;
                case AmqpHeaderPhaseStepKind.ExpectAlive:
                    await peer.WriteAsync(AmqpHeaderPhaseCorpus.Frame(AmqpTestPeer.CreateBegin()), cancellationToken);

                    AmqpTestFrame answer = await peer.ReadNonEmptyFrameAsync(cancellationToken);

                    answer.Performative.ShouldBeOfType<AmqpBeginPerformative>().RemoteChannel.ShouldBe((ushort)0);
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(index), step.Kind, null);
            }
        }
        catch (ShouldAssertException exception)
        {
            throw new ShouldAssertException($"{entry.Rule} '{entry.Name}', step {index + 1} ({step}): {exception.Message}", exception);
        }
    }

    /// <summary>
    /// Reads the next frame the engine writes within <paramref name="window"/>, or returns
    /// <see langword="null"/> when none arrives in time or the engine hangs up.
    /// </summary>
    private static async Task<AmqpTestFrame?> ReadWithinAsync(AmqpTestPeer peer, TimeSpan window, CancellationToken cancellationToken)
    {
        using CancellationTokenSource limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        limit.CancelAfter(window);

        try
        {
            return await peer.ReadFrameAsync(limit.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
    }
}
