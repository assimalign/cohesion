using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Build.Tasks;

namespace Assimalign.Cohesion.Core.Tests;

public sealed class GeneratedValueTypeMetadataTests
{
    private const string displayPrefix = "Cohesion Test [Core] - Generated value-type metadata: ";

    [Fact(DisplayName = displayPrefix + "every accessibility mode compiles and preserves values")]
    public void Constructor_AllAccessibilityModes_ShouldPreserveValues()
    {
        // Arrange & Act
        var publicValue = CreatePublicValue("text");
        var internalValue = new GeneratedInt(1);
        var noneValue = new GeneratedNoneAccessInt(2);
        var defaultValue = new GeneratedDefaultAccessInt(3);

        // Assert
        publicValue.Value.ShouldBe("text");
        internalValue.Value.ShouldBe(1);
        noneValue.Value.ShouldBe(2);
        defaultValue.Value.ShouldBe(3);
    }

    [Theory(DisplayName = displayPrefix + "invalid metadata identifies the item and diagnostic code")]
    [InlineData("ObjectRuntimeType", "", "String", "COHVT001")]
    [InlineData("ObjectRuntimeType", "DateTime", "String", "COHVT001")]
    [InlineData("ObjectRuntimeType", "1", "String", "COHVT001")]
    [InlineData("ObjectNamespace", "", "String", "COHVT002")]
    [InlineData("ObjectNamespace", " ", "String", "COHVT002")]
    [InlineData("ObjectKind", "Record", "String", "COHVT003")]
    [InlineData("ObjectKind", "1", "String", "COHVT003")]
    [InlineData("ObjectAccessModifier", "Protected", "String", "COHVT004")]
    [InlineData("ObjectAccessModifier", "1", "String", "COHVT004")]
    [InlineData("ObjectHasImplicitOperators", "yes", "String", "COHVT005")]
    [InlineData("ObjectHasImplicitOperators", "1", "String", "COHVT005")]
    [InlineData("ObjectHasIsValidMethod", "yes", "String", "COHVT005")]
    [InlineData("ObjectHasIsValidMethod", "1", "String", "COHVT005")]
    [InlineData("ObjectComparison", "CurrentCulture", "String", "COHVT006")]
    [InlineData("ObjectComparison", "1", "String", "COHVT006")]
    [InlineData("ObjectComparison", "Ordinal", "Int", "COHVT006")]
    [InlineData("ObjectComparison", "OrdinalIgnoreCase", "Int", "COHVT006")]
    public void Execute_InvalidMetadata_ShouldReportItemAndDiagnosticCode(string metadata, string value, string runtimeType, string expectedCode)
    {
        // Arrange
        var engine = new RecordingBuildEngine();
        var validItem = CreateItem("ValueObjects/ValidValue.cs", "String");
        var invalidItem = CreateItem("ValueObjects/InvalidValue.cs", runtimeType);
        invalidItem.SetMetadata(metadata, value);
        string outputPath = Path.Combine(AppContext.BaseDirectory, "MetadataTests", Guid.NewGuid().ToString("N"));
        var task = new CodeGenerationCreateValueTypeTask
        {
            BuildEngine = engine,
            CodeGenOutputPath = outputPath,
            ValueTypes = [validItem, invalidItem]
        };

        // Act
        bool succeeded = task.Execute();

        // Assert
        succeeded.ShouldBeFalse();
        BuildErrorEventArgs error = engine.Errors.ShouldHaveSingleItem();
        error.Code.ShouldBe(expectedCode);
        error.Message.ShouldNotBeNull().ShouldContain(invalidItem.ItemSpec, Case.Sensitive);
        error.Message.ShouldContain(metadata, Case.Sensitive);
        task.ValueTypesCreated.ShouldBeNull();
        Directory.Exists(outputPath).ShouldBeFalse();
    }

    // The public signature makes the compiler check explicit Public metadata without reflection.
    /// <summary>Creates the fixture whose public accessibility is generated from metadata.</summary>
    /// <param name="value">The fixture value.</param>
    /// <returns>The generated public value.</returns>
    public static GeneratedPublicString CreatePublicValue(string value) => new GeneratedPublicString(value);

    private static TaskItem CreateItem(string path, string runtimeType)
    {
        var item = new TaskItem(path);
        item.SetMetadata("ObjectRuntimeType", runtimeType);
        item.SetMetadata("ObjectNamespace", "Assimalign.Cohesion.Core.Tests");
        return item;
    }

    private sealed class RecordingBuildEngine : IBuildEngine
    {
        internal List<BuildErrorEventArgs> Errors { get; } = [];

        public bool ContinueOnError => false;
        public int LineNumberOfTaskNode => 0;
        public int ColumnNumberOfTaskNode => 0;
        public string ProjectFileOfTaskNode => string.Empty;

        public void LogErrorEvent(BuildErrorEventArgs error) => Errors.Add(error);
        public void LogWarningEvent(BuildWarningEventArgs warning) { }
        public void LogMessageEvent(BuildMessageEventArgs message) { }
        public void LogCustomEvent(CustomBuildEventArgs customEvent) { }
        public bool BuildProjectFile(string projectFileName, string[] targetNames, IDictionary globalProperties, IDictionary targetOutputs) => false;
    }
}
