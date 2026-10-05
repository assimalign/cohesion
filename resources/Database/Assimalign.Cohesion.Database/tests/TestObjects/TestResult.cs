using System.Collections.Generic;

using Assimalign.Cohesion.Database.Execution;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Tests;

/// <summary>
/// The result the base-suite session's execute cores return.
/// </summary>
internal sealed class TestResult : QueryResult
{
    public static TestResult Instance { get; } = new();

    public override QueryResultStatus Status => QueryResultStatus.Success;

    public override long AffectedCount => 0;

    public override IReadOnlyList<Diagnostic>? Diagnostics => null;
}
