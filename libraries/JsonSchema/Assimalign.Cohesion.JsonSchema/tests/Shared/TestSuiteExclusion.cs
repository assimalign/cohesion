using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// A test of the JSON Schema Test Suite the library deliberately does not pass, with the reason
/// <c>docs/DESIGN.md</c> records for it.
/// </summary>
/// <param name="File">The suite file, relative to <c>validation/tests</c> or <c>annotations/tests</c>.</param>
/// <param name="Case">The test case description.</param>
/// <param name="Test">The test description, or <see langword="null"/> for every test of the case.</param>
/// <param name="Releases">The dialect releases the exclusion applies to, or <see langword="null"/> for all.</param>
/// <param name="Reason">Why the test is excluded.</param>
internal sealed record TestSuiteExclusion(string File, string Case, string? Test, int[]? Releases, string Reason)
{
    /// <summary>
    /// The excluded tests. Each entry is listed in the project's <c>docs/DESIGN.md</c>; a test that
    /// starts passing fails its suite row until its entry is removed.
    /// </summary>
    public static IReadOnlyList<TestSuiteExclusion> All { get; } =
    [
    ];

    public static TestSuiteExclusion? Find(TestSuiteDialect dialect, string file, string testCase, string test)
    {
        foreach (TestSuiteExclusion exclusion in All)
        {
            if (string.Equals(exclusion.File, file, StringComparison.Ordinal)
                && string.Equals(exclusion.Case, testCase, StringComparison.Ordinal)
                && (exclusion.Test is null || string.Equals(exclusion.Test, test, StringComparison.Ordinal))
                && (exclusion.Releases is null || Array.IndexOf(exclusion.Releases, dialect.Release) >= 0))
            {
                return exclusion;
            }
        }

        return null;
    }
}
