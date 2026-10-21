using System;
using System.IO;
using System.Linq;
using System.Threading;

using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>Connects a model's pass recorder to requested delays through the root's test grant.</summary>
public sealed class WorkerPumpObservation : IDisposable
{
    private static readonly Lazy<string[]> _unobservedWaits = new(ReadUnobservedWaits);
    private readonly DatabaseEngineWorker _worker;
    private readonly Action<TimeSpan> _observer;
    private readonly Action<TimeSpan>? _previous;

    /// <summary>Observes delays without changing their execution.</summary>
    /// <param name="worker">The pump to observe.</param>
    /// <param name="observer">Receives every requested unconditional delay.</param>
    /// <exception cref="InvalidOperationException">The pump contains an unobserved blocking wait.</exception>
    private WorkerPumpObservation(DatabaseEngineWorker worker, Action<TimeSpan> observer)
    {
        // A raw Sleep/WaitOne added beside Delay would bypass requested-wait accounting. Keep
        // that invariant executable too; the embedded input is the source built for this test run.
        string[] unobserved = _unobservedWaits.Value;
        if (unobserved.Length != 0)
        {
            throw new InvalidOperationException("Unobserved worker waits: " + string.Join(", ", unobserved));
        }
        _worker = worker;
        _observer = observer;
        _previous = Interlocked.Exchange(ref worker.DelayRequested, observer);
    }

    /// <summary>Observes a pump's unconditional delays without changing their execution.</summary>
    /// <param name="worker">The pump to observe.</param>
    /// <param name="observer">Receives every requested unconditional delay.</param>
    /// <returns>The scoped observation.</returns>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="InvalidOperationException">The pump contains an unobserved blocking wait.</exception>
    public static WorkerPumpObservation Create(DatabaseEngineWorker worker, Action<TimeSpan> observer)
    {
        ArgumentNullException.ThrowIfNull(worker);
        ArgumentNullException.ThrowIfNull(observer);
        return new WorkerPumpObservation(worker, observer);
    }

    /// <summary>Removes this observation without displacing a later observer.</summary>
    public void Dispose() => Interlocked.CompareExchange(ref _worker.DelayRequested, _previous, _observer);

    internal static string[] FindUnobservedWaits(string source, params string[] observedMethods)
        => CSharpSyntaxTree.ParseText(source).GetRoot().DescendantNodes().OfType<MethodDeclarationSyntax>()
            .Where(method => !observedMethods.Contains(method.Identifier.ValueText, StringComparer.Ordinal))
            .SelectMany(method => method.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(invocation => IsUnobservedWait(invocation, observedMethods.Contains("Delay", StringComparer.Ordinal)))
                .Select(invocation => method.Identifier.ValueText + ": " + invocation))
            .ToArray();

    private static bool IsUnobservedWait(InvocationExpressionSyntax invocation, bool observesDelay)
    {
        string? name = invocation.Expression switch
        {
            MemberAccessExpressionSyntax member => member.Name.Identifier.ValueText,
            MemberBindingExpressionSyntax member => member.Name.Identifier.ValueText,
            IdentifierNameSyntax identifier => identifier.Identifier.ValueText,
            _ => null,
        };
        if (name == "Delay" && observesDelay
            && invocation.Expression is IdentifierNameSyntax or MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax })
        {
            return false;
        }
        return name is "WaitOne" or "WaitAny" or "WaitAll" or "Wait" or "Sleep" or "Delay" or "Join" or "GetResult" or "SpinUntil";
    }

    private static string[] ReadUnobservedWaits()
    {
        return [.. Read("WorkerPumpSource", "Delay", "WaitForTrigger"),
            .. Read("CheckpointPumpSource", "WaitForTrigger"),
            .. Read("CheckpointLaneSource", "WaitForLane", "Dispose", "Park", "WaitForIdle")];

        static string[] Read(string resource, params string[] observedMethods)
        {
            using var input = typeof(WorkerPumpObservation).Assembly.GetManifestResourceStream(resource)
                ?? throw new InvalidOperationException("The worker source observation input is missing.");
            using var reader = new StreamReader(input);
            return FindUnobservedWaits(reader.ReadToEnd(), observedMethods)
                .Select(wait => resource + ": " + wait).ToArray();
        }
    }
}
