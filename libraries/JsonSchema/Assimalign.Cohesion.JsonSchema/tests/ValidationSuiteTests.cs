using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Runs the validation tests of the official JSON Schema Test Suite against every built-in dialect.
/// </summary>
public class ValidationSuiteTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Test suite: ";

    public static TheoryData<string, string> RequiredFiles => TestSuiteRunner.GetRequiredValidationRows();

    public static TheoryData<string, string> OptionalFiles => TestSuiteRunner.GetOptionalValidationRows();

    [Theory(DisplayName = DisplayPrefix + "Every required validation test matches its expected outcome")]
    [MemberData(nameof(RequiredFiles))]
    public void Evaluate_RequiredSuiteFile_ShouldMatchExpectedOutcome(string dialect, string file)
    {
        // Arrange
        TestSuiteDialect suiteDialect = TestSuiteDialect.Get(dialect);

        // Act
        List<string> failures = TestSuiteRunner.RunValidationFile(suiteDialect, file);

        // Assert
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    [Theory(DisplayName = DisplayPrefix + "Every optional validation test matches its expected outcome")]
    [MemberData(nameof(OptionalFiles))]
    public void Evaluate_OptionalSuiteFile_ShouldMatchExpectedOutcome(string dialect, string file)
    {
        // Arrange
        TestSuiteDialect suiteDialect = TestSuiteDialect.Get(dialect);

        // Act
        List<string> failures = TestSuiteRunner.RunValidationFile(suiteDialect, file);

        // Assert
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }
}
