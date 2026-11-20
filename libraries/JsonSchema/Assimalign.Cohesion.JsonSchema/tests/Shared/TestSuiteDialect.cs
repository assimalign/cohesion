using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// A dialect the JSON Schema Test Suite runs against: its built-in dialect and the release number the
/// suite's <c>compatibility</c> property uses for it.
/// </summary>
internal sealed class TestSuiteDialect
{
    private TestSuiteDialect(string name, JsonSchemaDialect dialect)
    {
        Name = name;
        Dialect = dialect;
        Release = (int)dialect.Version;
    }

    public static IReadOnlyList<TestSuiteDialect> All { get; } =
    [
        new("draft4", JsonSchemaDialect.Draft04),
        new("draft6", JsonSchemaDialect.Draft06),
        new("draft7", JsonSchemaDialect.Draft07),
        new("draft2019-09", JsonSchemaDialect.Draft201909),
        new("draft2020-12", JsonSchemaDialect.Draft202012),
    ];

    /// <summary>Gets the name test rows display.</summary>
    public string Name { get; }

    public JsonSchemaDialect Dialect { get; }

    /// <summary>Gets the release number: 4, 6, 7, 2019 or 2020.</summary>
    public int Release { get; }

    public static TestSuiteDialect Get(string name)
    {
        foreach (TestSuiteDialect dialect in All)
        {
            if (string.Equals(dialect.Name, name, StringComparison.Ordinal))
            {
                return dialect;
            }
        }

        throw new ArgumentOutOfRangeException(nameof(name), name, "Not a test suite dialect.");
    }

    /// <summary>
    /// Determines whether a test case's <c>compatibility</c> property admits this dialect, with the
    /// algorithm the suite's README prescribes: constraints are read in order, and the first one whose
    /// release is not below the dialect's decides.
    /// </summary>
    public bool IsCompatible(string? compatibility)
    {
        if (string.IsNullOrEmpty(compatibility))
        {
            return true;
        }

        bool compatible = true;
        foreach (string constraint in compatibility.Split(','))
        {
            string text = constraint.Trim();
            int release;
            if (text.StartsWith("<=", StringComparison.Ordinal))
            {
                release = int.Parse(text.AsSpan(2), provider: null);
                compatible = Release <= release;
            }
            else if (text.StartsWith('='))
            {
                release = int.Parse(text.AsSpan(1), provider: null);
                compatible = Release == release;
            }
            else
            {
                release = int.Parse(text, provider: null);
                compatible = Release >= release;
            }

            if (Release <= release)
            {
                break;
            }
        }

        return compatible;
    }

    public override string ToString() => Name;
}
