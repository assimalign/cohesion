using System;
using System.Globalization;
using System.Threading;

using Xunit.Abstractions;

using NetLite = Amqp;

namespace Assimalign.Cohesion.Amqp.Connections.Tests;

/// <summary>
/// Routes AMQPNetLite's frame trace into the running test's output, so a failing interoperability test shows
/// every frame the peer sent and received, in order, as the peer decoded them. That is what tells a Cohesion
/// defect (a frame Cohesion should not have sent, or one it failed to accept) from a feature the peer does not
/// support (the peer refusing with <c>amqp:not-implemented</c>, or never sending the frame).
/// </summary>
/// <remarks>
/// AMQPNetLite's trace is a static field, so only one test may own it; the tests that attach it run in
/// <see cref="AmqpInteropCollection"/>, which is never parallelized.
/// </remarks>
internal sealed class AmqpNetLiteTrace : IDisposable
{
    private static readonly Lock _sync = new();
    private static ITestOutputHelper? _output;

    private AmqpNetLiteTrace()
    {
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
        }
    }

    private static void Write(NetLite.TraceLevel level, string format, params object[] args)
    {
        string line = args is { Length: > 0 }
            ? string.Format(CultureInfo.InvariantCulture, format, args)
            : format;

        lock (_sync)
        {
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
}
