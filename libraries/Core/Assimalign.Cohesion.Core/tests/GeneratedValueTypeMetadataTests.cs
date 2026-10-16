using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using Shouldly;
using Xunit;

using Assimalign.Cohesion.Build.Tasks;

namespace Assimalign.Cohesion.Core.Tests;

public sealed class GeneratedValueTypeMetadataTests
{
    private const string displayPrefix = "Cohesion Test [Core] - Generated value-type metadata: ";

    // The parameter names of the BCL contracts a generated member may document with <inheritdoc/>.
    private static readonly Dictionary<string, string[]> _inheritedParameters = new()
    {
        ["Equals"] = ["other", "obj"],
        ["CompareTo"] = ["other"],
        ["GetHashCode"] = [""],
        ["ToString"] = ["format,formatProvider"],
        ["TryFormat"] = ["destination,charsWritten,format,provider"]
    };

    public static TheoryData<string, string, bool> GeneratedShapes()
    {
        var data = new TheoryData<string, string, bool>();
        foreach (string runtimeType in new[] { "Int", "Short", "Long", "UInt", "UShort", "ULong", "Double", "Decimal", "String", "Guid", "Ulid" })
        {
            foreach (string kind in new[] { "Struct", "Class" })
            {
                data.Add(runtimeType, kind, false);
                data.Add(runtimeType, kind, true);
            }
        }

        return data;
    }

    [Fact(DisplayName = displayPrefix + "every accessibility mode compiles and preserves values")]
    public void Constructor_AllAccessibilityModes_ShouldPreserveValues()
    {
        // Arrange & Act
        var publicValue = CreatePublicValue("text");
        var internalValue = new GeneratedInt(1);
        var noneValue = new GeneratedNoneAccessInt(2);
        var defaultValue = CreateDefaultAccessValue(3);

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
    [InlineData("ObjectHasCustomToString", "yes", "String", "COHVT005")]
    [InlineData("ObjectHasCustomToString", "1", "String", "COHVT005")]
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
        if (expectedCode == "COHVT004")
        {
            error.Message.ShouldContain("Public, Internal, None (omitted or None emits no modifier)", Case.Sensitive);
        }
        task.ValueTypesCreated.ShouldBeNull();
        Directory.Exists(outputPath).ShouldBeFalse();
    }

    [Theory(DisplayName = displayPrefix + "custom display suppresses generated text and debugger members")]
    [InlineData("String", "Struct")]
    [InlineData("String", "Class")]
    [InlineData("Int", "Struct")]
    [InlineData("Int", "Class")]
    public void Execute_CustomToString_ShouldLeaveDisplayToHandwrittenPartial(string runtimeType, string kind)
    {
        // Arrange
        var item = CreateItem("ValueObjects/CustomDisplay.cs", runtimeType);
        item.SetMetadata("ObjectKind", kind);
        item.SetMetadata("ObjectHasCustomToString", "true");

        // Act
        string generated = GenerateSource(item);

        // Assert
        generated.ShouldNotContain("public override string ToString()", Case.Sensitive);
        generated.ShouldNotContain("DebuggerDisplay", Case.Sensitive);
        generated.ShouldContain("public override int GetHashCode()", Case.Sensitive);
        generated.ShouldContain("JsonConverter", Case.Sensitive);
    }

    [Theory(DisplayName = displayPrefix + "omitted or false custom display preserves generated text and debugger members")]
    [InlineData("")]
    [InlineData("false")]
    public void Execute_DefaultToString_ShouldGenerateDisplayMembers(string customToString)
    {
        // Arrange
        var item = CreateItem("ValueObjects/DefaultDisplay.cs", "String");
        item.SetMetadata("ObjectHasCustomToString", customToString);

        // Act
        string generated = GenerateSource(item);

        // Assert
        generated.ShouldContain("public override string ToString()", Case.Sensitive);
        generated.ShouldContain("DebuggerDisplay(\"{Value}\")", Case.Sensitive);
    }

    [Theory(DisplayName = displayPrefix + "every public member documents each parameter under its declared name")]
    [MemberData(nameof(GeneratedShapes))]
    public void Execute_AnyShape_ShouldDocumentEachParameterByDeclaredName(string runtimeType, string kind, bool validation)
    {
        // Arrange
        var item = CreateItem("ValueObjects/Documented.cs", runtimeType);
        item.SetMetadata("ObjectKind", kind);
        item.SetMetadata("ObjectHasImplicitOperators", "true");
        item.SetMetadata("ObjectHasIsValidMethod", validation.ToString());

        // Act
        IReadOnlyList<GeneratedMember> members = PublicMembers(GenerateSource(item));

        // Assert
        members.Count(member => member.Declaration.Contains(" operator ", StringComparison.Ordinal)).ShouldBe(8);
        foreach (GeneratedMember member in members)
        {
            member.Documentation.ShouldNotBeEmpty(member.Declaration);
            if (member.Documentation.Contains("<inheritdoc/>", StringComparison.Ordinal))
            {
                // The compiler does not check inherited docs, so only an exact name match may inherit them.
                _inheritedParameters.ShouldContainKey(member.Name, member.Declaration);
                _inheritedParameters[member.Name].ShouldContain(string.Join(",", member.Parameters), member.Declaration);
                continue;
            }

            member.Documentation.ShouldContain("<summary>", Case.Sensitive, member.Declaration);
            foreach (string parameter in member.Parameters)
            {
                member.Documentation.ShouldContain($"<param name=\"{parameter}\">", Case.Sensitive, member.Declaration);
            }
            Regex.Matches(member.Documentation, "<param name=\"(\\w+)\">").Select(match => match.Groups[1].Value)
                .ShouldBeSubsetOf(member.Parameters, member.Declaration);
            if (member.ReturnsValue)
            {
                member.Documentation.ShouldContain("<returns>", Case.Sensitive, member.Declaration);
            }
        }
    }

    [Theory(DisplayName = displayPrefix + "JSON write methods document the writer failures they propagate")]
    [InlineData("String", true)]
    [InlineData("Double", true)]
    [InlineData("Int", false)]
    [InlineData("Decimal", false)]
    [InlineData("Guid", false)]
    [InlineData("Ulid", false)]
    public void Execute_JsonWriteMethods_ShouldDocumentPropagatedWriterExceptions(string runtimeType, bool writeRejectsArgument)
    {
        // Arrange
        var item = CreateItem("ValueObjects/JsonWrite.cs", runtimeType);

        // Act
        IReadOnlyList<GeneratedMember> members = PublicMembers(GenerateSource(item));

        // Assert
        string write = members.Single(member => member.Name == "Write").Documentation;
        string propertyName = members.Single(member => member.Name == "WriteAsPropertyName").Documentation;
        write.ShouldContain("<exception cref=\"InvalidOperationException\">", Case.Sensitive);
        propertyName.ShouldContain("<exception cref=\"InvalidOperationException\">", Case.Sensitive);
        write.Contains("<exception cref=\"ArgumentException\">", StringComparison.Ordinal).ShouldBe(writeRejectsArgument);
        propertyName.Contains("<exception cref=\"ArgumentException\">", StringComparison.Ordinal).ShouldBe(runtimeType == "String");
        propertyName.Contains("JsonException\">", StringComparison.Ordinal).ShouldBe(runtimeType == "String");
    }

    [Theory(DisplayName = displayPrefix + "formatting and parsing document the runtime type's failures")]
    [InlineData("Int", true, true)]
    [InlineData("Decimal", true, true)]
    [InlineData("Double", true, false)]
    [InlineData("Guid", true, false)]
    [InlineData("Ulid", false, false)]
    public void Execute_FormattingAndParsing_ShouldDocumentRuntimeTypeExceptions(string runtimeType, bool formatRejectsUnknown, bool parseOverflows)
    {
        // Arrange
        var item = CreateItem("ValueObjects/Formatting.cs", runtimeType);

        // Act
        IReadOnlyList<GeneratedMember> members = PublicMembers(GenerateSource(item));

        // Assert
        GeneratedMember[] formatting = members.Where(member => member.Parameters.Contains("format")).ToArray();
        GeneratedMember[] parsing = members.Where(member => member.Name == "Parse").ToArray();
        formatting.Length.ShouldBe(2);
        parsing.Length.ShouldBe(4);
        foreach (GeneratedMember member in formatting)
        {
            member.Documentation.Contains("<exception cref=\"FormatException\">", StringComparison.Ordinal).ShouldBe(formatRejectsUnknown, member.Declaration);
        }
        foreach (GeneratedMember member in parsing)
        {
            member.Documentation.ShouldContain("<exception cref=\"FormatException\">", Case.Sensitive, member.Declaration);
            member.Documentation.Contains("<exception cref=\"OverflowException\">", StringComparison.Ordinal).ShouldBe(parseOverflows, member.Declaration);
        }
    }

    private static string GenerateSource(TaskItem item)
    {
        var engine = new RecordingBuildEngine();
        string outputPath = Path.Combine(AppContext.BaseDirectory, "MetadataTests", Guid.NewGuid().ToString("N"));
        var task = new CodeGenerationCreateValueTypeTask
        {
            BuildEngine = engine,
            CodeGenOutputPath = outputPath,
            ValueTypes = [item]
        };

        try
        {
            task.Execute().ShouldBeTrue();
            engine.Errors.ShouldBeEmpty();
            ITaskItem generated = task.ValueTypesCreated.ShouldNotBeNull().ShouldHaveSingleItem();
            return File.ReadAllText(generated.GetMetadata("ObjectFilePath"));
        }
        finally
        {
            if (Directory.Exists(outputPath))
            {
                Directory.Delete(outputPath, recursive: true);
            }
        }
    }

    // The public signature makes the compiler check explicit Public metadata without reflection.
    /// <summary>Creates the fixture whose public accessibility is generated from metadata.</summary>
    /// <param name="value">The fixture value.</param>
    /// <returns>The generated public value.</returns>
    public static GeneratedPublicString CreatePublicValue(string value) => new GeneratedPublicString(value);

    // This public signature is a compile-time assertion that omitted metadata preserves public accessibility.
    /// <summary>Creates the fixture whose public accessibility comes from its handwritten partial.</summary>
    /// <param name="value">The fixture value.</param>
    /// <returns>The generated public value.</returns>
    public static GeneratedDefaultAccessInt CreateDefaultAccessValue(int value) => new GeneratedDefaultAccessInt(value);

    private static TaskItem CreateItem(string path, string runtimeType)
    {
        var item = new TaskItem(path);
        item.SetMetadata("ObjectRuntimeType", runtimeType);
        item.SetMetadata("ObjectNamespace", "Assimalign.Cohesion.Core.Tests");
        return item;
    }

    // Reads each public method, operator or constructor (one-line signatures) and the doc lines directly above it.
    private static IReadOnlyList<GeneratedMember> PublicMembers(string source)
    {
        string[] lines = source.Replace("\r\n", "\n").Split('\n');
        var members = new List<GeneratedMember>();
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].Trim();
            int open = line.IndexOf('(');
            int assignment = line.IndexOf(" = ", StringComparison.Ordinal);
            if (!line.StartsWith("public ", StringComparison.Ordinal) || open < 0 || (assignment >= 0 && assignment < open))
            {
                continue;
            }

            string[] prefix = line[..open].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            int close = open;
            for (int depth = 0; ; close++)
            {
                depth += line[close] == '(' ? 1 : line[close] == ')' ? -1 : 0;
                if (depth == 0)
                {
                    break;
                }
            }
            string[] parameters = Regex.Replace(line[(open + 1)..close], @"\[[^\]]*\]", string.Empty)
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(parameter => parameter.Split(' ')[^1])
                .ToArray();

            var documentation = new List<string>();
            for (int above = index - 1; above >= 0; above--)
            {
                string previous = lines[above].Trim();
                if (previous.StartsWith('['))
                {
                    continue;
                }
                if (!previous.StartsWith("///", StringComparison.Ordinal))
                {
                    break;
                }
                documentation.Insert(0, previous);
            }

            bool returnsValue = prefix.Length > 2 && !prefix.Contains("void");
            members.Add(new GeneratedMember(line, prefix[^1], parameters, string.Join("\n", documentation), returnsValue));
        }

        return members;
    }

    private sealed record GeneratedMember(string Declaration, string Name, string[] Parameters, string Documentation, bool ReturnsValue);

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
