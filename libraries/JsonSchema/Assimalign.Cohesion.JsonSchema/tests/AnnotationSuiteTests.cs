using System;
using System.Collections.Generic;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.JsonSchema.Tests;

/// <summary>
/// Runs the annotation tests of the official JSON Schema Test Suite against every built-in dialect.
/// </summary>
public class AnnotationSuiteTests
{
    private const string DisplayPrefix = "Cohesion Test [JsonSchema] - Test suite: ";

    public static TheoryData<string, string> Files => TestSuiteRunner.GetAnnotationRows();

    [Theory(DisplayName = DisplayPrefix + "Every annotation test produces exactly the expected annotations")]
    [MemberData(nameof(Files))]
    public void Evaluate_AnnotationSuiteFile_ShouldProduceExpectedAnnotations(string dialect, string file)
    {
        // Arrange
        TestSuiteDialect suiteDialect = TestSuiteDialect.Get(dialect);

        // Act
        List<string> failures = TestSuiteRunner.RunAnnotationFile(suiteDialect, file);

        // Assert
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }
}
