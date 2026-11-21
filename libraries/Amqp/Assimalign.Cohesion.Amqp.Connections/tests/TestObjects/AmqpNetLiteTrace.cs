using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading;

using Xunit.Abstractions;

using NetLite = Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Routes AMQPNetLite's frame trace into the running test's output, so a failing interoperability test shows
/// every frame the peer sent and received, in order, as the peer decoded them. That is what tells a Cohesion
/// defect (a frame Cohesion should not have sent, or one it failed to accept) from a feature the peer does not
/// support (the peer refusing with <c>amqp:not-implemented</c>, or never sending the frame). A test that asserts
/// on the exchange itself reads the frames from <see cref="Frames"/>.
/// </summary>
/// <remarks>
/// AMQPNetLite's trace is a static field, so only one test may own it; the tests that attach it run in
/// <see cref="AmqpInteropCollection"/>, which is never parallelized.
/// </remarks>
internal sealed partial class AmqpNetLiteTrace : IDisposable
{
    private static readonly Lock _sync = new();
    private static ITestOutputHelper? _output;
    private static List<string>? _frames;

    private AmqpNetLiteTrace()
    {
    }

    /// <summary>
    /// Gets the frames the peer sent and received since the trace was attached, in order, each as AMQPNetLite
    /// traced it: <c>SEND</c> or <c>RECV</c>, the channel, then the performative and its fields.
    /// </summary>
    public IReadOnlyList<string> Frames
    {
        get
        {
            lock (_sync)
            {
                return _frames is null ? [] : [.. _frames];
            }
        }
    }

    /// <summary>
    /// Gets the <c>attach</c> and <c>detach</c> frames of <see cref="Frames"/>, each as its direction and
    /// performative, with <c>closed</c> after a detach that closes its link: <c>RECV attach</c>,
    /// <c>SEND detach closed</c>.
    /// </summary>
    public IReadOnlyList<string> LinkFrames
    {
        get
        {
            List<string> frames = [];

            foreach (string frame in Frames)
            {
                Match match = LinkFrame().Match(frame);

                if (match.Success)
                {
                    string performative = match.Groups["performative"].Value;
                    bool closes = performative == "detach" && frame.Contains("closed:True", StringComparison.Ordinal);

                    frames.Add($"{match.Groups["direction"].Value} {performative}{(closes ? " closed" : string.Empty)}");
                }
            }

            return frames;
        }
    }

    /// <summary>
    /// Sends the peer's frames and errors to <paramref name="output"/> until the returned object is disposed.
    /// </summary>
    public static AmqpNetLiteTrace Attach(ITestOutputHelper output)
    {
        ArgumentNullException.ThrowIfNull(output);

        lock (_sync)
        {
            _output = output;
            _frames = [];
            NetLite.Trace.TraceLevel = NetLite.TraceLevel.Frame | NetLite.TraceLevel.Error;
            NetLite.Trace.TraceListener = Write;
        }

        return new AmqpNetLiteTrace();
    }

    public void Dispose()
    {
        lock (_sync)
        {
            NetLite.Trace.TraceListener = null;
            NetLite.Trace.TraceLevel = 0;
            _output = null;
            _frames = null;
        }
    }

    private static void Write(NetLite.TraceLevel level, string format, params object[] args)
    {
        string line = args is { Length: > 0 }
            ? string.Format(CultureInfo.InvariantCulture, format, args)
            : format;

        lock (_sync)
        {
            if (level == NetLite.TraceLevel.Frame)
            {
                _frames?.Add(line);
            }

            try
            {
                _output?.WriteLine($"[AMQPNetLite {level}] {line}");
            }
            catch (InvalidOperationException)
            {
                // A peer thread traced after the test ended: xUnit has no test to write to any more.
            }
        }
    }

    /// <summary>
    /// Matches the start of a traced <c>attach</c> or <c>detach</c>: <c>RECV (ch=0) attach(</c>.
    /// </summary>
    [GeneratedRegex(@"^(?<direction>SEND|RECV) \(ch=\d+\) (?<performative>attach|detach)\(", RegexOptions.CultureInvariant)]
    private static partial Regex LinkFrame();
}
