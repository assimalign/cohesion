using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Build.Tasks;

/// <summary>Generates the value objects declared by CohesionValueType items.</summary>
public class CodeGenerationCreateValueTypeTask : CodeGenerationTask
{
    /// <summary>Gets or sets the value objects to generate.</summary>
    [Required]
    public ITaskItem[]? ValueTypes { get; set; }

    /// <summary>Gets the generated value object items, including unchanged files.</summary>
    [Output]
    public ITaskItem[]? ValueTypesCreated { get; set; }

    /// <inheritdoc/>
    public override bool Execute()
    {
        try
        {
            var contexts = new List<ValueTypeContext>();
            foreach (var item in ValueTypes ?? Array.Empty<ITaskItem>())
            {
                var context = ReadContext(item);
                if (context is not null)
                {
                    contexts.Add(context);
                }
            }

            // Validate every item before changing any generated output.
            if (Log.HasLoggedErrors)
            {
                return false;
            }

            var outputDirectory = Path.GetFullPath(CodeGenOutputPath!);
            var produced = new HashSet<string>(Path.DirectorySeparatorChar == '\\' ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
            var created = new List<ITaskItem>();
            foreach (var context in contexts)
            {
                var builder = new StringBuilder();
                GenerateValueType(builder, context);
                // Generated sources use CRLF on either MSBuild runtime and either checkout style.
                string content = builder.ToString().Replace("\r\n", "\n").Replace("\n", "\r\n");
                if (!File.Exists(context.FilePath) || File.ReadAllText(context.FilePath) != content)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(context.FilePath)!);
                    File.WriteAllText(context.FilePath, content, new UTF8Encoding(false));
                    Log.LogMessage(MessageImportance.Normal, "Generated: {0}", context.FilePath);
                }

                produced.Add(Path.GetFullPath(context.FilePath));
                var generated = new TaskItem(context.Item);
                generated.SetMetadata("ObjectName", context.Name);
                generated.SetMetadata("ObjectFilePath", context.FilePath);
                created.Add(generated);
            }

            if (Directory.Exists(outputDirectory))
            {
                foreach (var file in Directory.EnumerateFiles(outputDirectory, "*.generated.cs", SearchOption.AllDirectories))
                {
                    if (!produced.Contains(Path.GetFullPath(file)))
                    {
                        File.Delete(file);
                    }
                }
            }

            ValueTypesCreated = created.ToArray();
            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
    }

    private ValueTypeContext? ReadContext(ITaskItem item)
    {
        bool valid = true;
        var context = new ValueTypeContext { Item = item, Name = Path.GetFileNameWithoutExtension(item.ItemSpec) };
        valid &= ReadChoice(item, "ObjectRuntimeType", "COHVT001", null, out ObjectRuntimeType runtimeType);
        context.RuntimeType = runtimeType;
        context.Namespace = item.GetMetadata("ObjectNamespace");
        if (string.IsNullOrWhiteSpace(context.Namespace))
        {
            MetadataError(item, "ObjectNamespace", "COHVT002", "a nonempty C# namespace");
            valid = false;
        }

        valid &= ReadChoice(item, "ObjectKind", "COHVT003", ObjectKind.Struct, out ObjectKind kind);
        context.IsClass = kind == ObjectKind.Class;
        valid &= ReadChoice(item, "ObjectAccessModifier", "COHVT004", ObjectAccessModifier.None, out ObjectAccessModifier access,
            "Public, Internal, None (omitted or None emits no modifier)");
        // An omitted modifier preserves the accessibility supplied by another partial declaration.
        context.Access = access == ObjectAccessModifier.None ? "" : access.ToString().ToLowerInvariant() + " ";
        valid &= ReadBoolean(item, "ObjectHasImplicitOperators", out bool implicitOperators);
        context.HasImplicitOperators = implicitOperators;
        valid &= ReadBoolean(item, "ObjectHasIsValidMethod", out bool validation);
        context.HasValidation = validation;
        valid &= ReadBoolean(item, "ObjectHasCustomToString", out bool customToString);
        context.HasCustomToString = customToString;
        valid &= ReadChoice(item, "ObjectComparison", "COHVT006", ObjectComparison.Ordinal, out ObjectComparison comparison);
        context.Comparison = comparison.ToString();
        if (!string.IsNullOrEmpty(item.GetMetadata("ObjectComparison")) && runtimeType != ObjectRuntimeType.String)
        {
            MetadataError(item, "ObjectComparison", "COHVT006", "Ordinal or OrdinalIgnoreCase, only with ObjectRuntimeType=String");
            valid = false;
        }

        context.FilePath = Path.Combine(CodeGenOutputPath!, Path.GetDirectoryName(item.ItemSpec) ?? "", context.Name + ".generated.cs");
        return valid ? context : null;
    }

    private bool ReadChoice<T>(ITaskItem item, string metadata, string code, T? defaultValue, out T value, string? allowedValues = null) where T : struct
    {
        string text = item.GetMetadata(metadata);
        if (text.Length == 0 && defaultValue.HasValue)
        {
            value = defaultValue.Value;
            return true;
        }

        if (Enum.GetNames(typeof(T)).Any(name => string.Equals(name, text, StringComparison.OrdinalIgnoreCase)) && Enum.TryParse(text, true, out value))
        {
            return true;
        }

        value = default;
        MetadataError(item, metadata, code, allowedValues ?? string.Join(", ", Enum.GetNames(typeof(T))));
        return false;
    }

    private bool ReadBoolean(ITaskItem item, string metadata, out bool value)
    {
        string text = item.GetMetadata(metadata);
        value = false;
        if (text.Length == 0 || bool.TryParse(text, out value))
        {
            return true;
        }

        MetadataError(item, metadata, "COHVT005", "true, false (default: false)");
        return false;
    }

    private void MetadataError(ITaskItem item, string metadata, string code, string allowed)
    {
        Log.LogError(null, code, null, null, 0, 0, 0, 0,
            "CohesionValueType '{0}' has invalid or missing {1} '{2}'. Allowed values: {3}.",
            item.ItemSpec, metadata, item.GetMetadata(metadata), allowed);
    }

    private sealed class ValueTypeContext
    {
        public ITaskItem Item { get; set; } = null!;
        public string Name { get; set; } = "";
        public string Namespace { get; set; } = "";
        public string FilePath { get; set; } = "";
        public string Access { get; set; } = "";
        public string Comparison { get; set; } = "Ordinal";
        public ObjectRuntimeType RuntimeType { get; set; }
        public bool IsClass { get; set; }
        public bool HasValidation { get; set; }
        public bool HasImplicitOperators { get; set; }
        public bool HasCustomToString { get; set; }
        public bool IsString => RuntimeType == ObjectRuntimeType.String;
        // Double parsing saturates to infinity instead of throwing; identifiers have no numeric range.
        public bool CanOverflow => !IsString && RuntimeType != ObjectRuntimeType.Double && RuntimeType != ObjectRuntimeType.Guid && RuntimeType != ObjectRuntimeType.Ulid;
        public string NullableName => Name + (IsClass ? "?" : "");
        public string Type => RuntimeType == ObjectRuntimeType.Guid || RuntimeType == ObjectRuntimeType.Ulid
            ? "global::System." + RuntimeType
            : RuntimeType.ToString().ToLowerInvariant();
        public string Construct(string value) => "new " + Name + "(" + value + (HasValidation ? ", true)" : ")");
    }

    private enum ObjectRuntimeType { Int, Short, Long, UInt, UShort, ULong, Double, Decimal, String, Guid, Ulid }
    private enum ObjectKind { Struct, Class }
    private enum ObjectAccessModifier { None, Public, Internal }
    private enum ObjectComparison { Ordinal, OrdinalIgnoreCase }

    private static void GenerateValueType(StringBuilder builder, ValueTypeContext context)
    {
        builder.AppendLine("// <auto-generated/>");
        builder.AppendLine("// Generator: CodeGenerationCreateValueTypeTask; CohesionValueType: " + context.Item.ItemSpec);
        builder.AppendLine("#nullable enable");
        builder.AppendLine("using System;");
        builder.AppendLine();
        builder.AppendLine("namespace " + context.Namespace + ";");
        builder.AppendLine();
        builder.AppendLine("/// <summary>Represents a strongly typed " + context.Type + " value.</summary>");
        builder.AppendLine("[global::System.Text.Json.Serialization.JsonConverter(typeof(" + context.Name + "JsonConverter))]");
        if (!context.HasCustomToString)
        {
            builder.AppendLine("[global::System.Diagnostics.DebuggerDisplay(\"{Value}\")]");
        }
        builder.AppendLine(context.Access + (context.IsClass ? "sealed partial class " : "readonly partial struct ") + context.Name + " :");
        builder.AppendTabbedLine(1, $$"""
            global::System.Numerics.IEqualityOperators<{{context.Name}}, {{context.Name}}, bool>,
            global::System.Numerics.IComparisonOperators<{{context.Name}}, {{context.Name}}, bool>,
            global::System.IComparable<{{context.Name}}>,
            global::System.IEquatable<{{context.Name}}>{{(context.IsString ? "" : ",")}}
            """);
        if (!context.IsString)
        {
            builder.AppendTabbedLine(1, $"global::System.ISpanParsable<{context.Name}>,\nglobal::System.ISpanFormattable");
        }
        builder.AppendLine("{");
        GenerateConstruction(builder, context);
        GenerateEquality(builder, context);
        GenerateOperators(builder, context);
        if (!context.IsString)
        {
            GenerateParsing(builder, context);
        }
        GenerateWellKnownValues(builder, context);
        GenerateJsonConverter(builder, context);
        builder.AppendLine("}");
    }

    private static void GenerateConstruction(StringBuilder builder, ValueTypeContext context)
    {
        builder.AppendTabbedLine(1, $$"""
            /// <summary>Initializes a value object from its runtime value.</summary>
            /// <param name="value">The runtime value.</param>
            """);
        if (context.IsString && context.IsClass)
        {
            builder.AppendTabbedLine(1, "/// <exception cref=\"ArgumentNullException\">The value is null.</exception>");
        }
        if (context.HasValidation)
        {
            builder.AppendTabbedLine(1, "/// <exception cref=\"ArgumentException\">The value fails validation.</exception>");
        }
        builder.AppendTabbedLine(1, $"public {context.Name}({context.Type} value)\n{{");
        if (context.IsString && context.IsClass)
        {
            builder.AppendTabbedLine(2, "ArgumentNullException.ThrowIfNull(value);");
        }
        if (context.HasValidation)
        {
            builder.AppendTabbedLine(2, "if (!IsValid(value, out string? message))\n{\n\tthrow new ArgumentException(message, nameof(value));\n}");
        }
        builder.AppendTabbedLine(2, "Value = value;");
        builder.AppendTabbedLine(1, "}\n");
        builder.AppendTabbedLine(1, $$"""
            /// <summary>Gets the underlying runtime value. A default string struct has a null value.</summary>
            public {{context.Type}} Value { get; }
            """);
        if (!context.HasValidation)
        {
            return;
        }
        builder.AppendTabbedLine(1, $$"""

            private {{context.Name}}({{context.Type}} value, bool validated)
            {
                Value = value;
            }

            /// <summary>Determines whether a candidate runtime value satisfies this type's rules.</summary>
            /// <param name="value">The candidate value.</param>
            /// <param name="message">The failure reason, or null on success.</param>
            /// <returns>True when the value satisfies the rules; otherwise false.</returns>
            public static partial bool IsValid({{context.Type}} value, [global::System.Diagnostics.CodeAnalysis.NotNullWhen(false)] out string? message);

            /// <summary>Creates a value object when the candidate satisfies its validation rules.</summary>
            /// <param name="value">The candidate runtime value.</param>
            /// <param name="result">The created value on success; otherwise default.</param>
            /// <returns>True when the value is valid; otherwise false.</returns>
            public static bool TryCreate({{context.Type}}{{(context.IsString ? "?" : "")}} value, {{(context.IsClass ? "[global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] " : "")}}out {{context.NullableName}} result)
            {
                result = default;
                if ({{(context.IsString ? "value is null || " : "")}}!IsValid(value, out _))
                {
                    return false;
                }

                result = {{context.Construct("value")}};
                return true;
            }
            """);
    }

    private static void GenerateEquality(StringBuilder builder, ValueTypeContext context)
    {
        string equality = context.IsString ? $"string.Equals(Value, other.Value, StringComparison.{context.Comparison})" : "Value.Equals(other.Value)";
        string comparison = context.IsString ? $"string.Compare(Value, other.Value, StringComparison.{context.Comparison})" : "Value.CompareTo(other.Value)";
        string hash = context.IsString ? $"Value is null ? 0 : StringComparer.{context.Comparison}.GetHashCode(Value)" : "Value.GetHashCode()";
        string display = context.IsString ? "Value ?? string.Empty" : context.RuntimeType == ObjectRuntimeType.Guid || context.RuntimeType == ObjectRuntimeType.Ulid ? "Value.ToString()" : "Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture)";
        builder.AppendTabbedLine(1, $$"""

            /// <inheritdoc/>
            public bool Equals({{context.NullableName}} other) => {{(context.IsClass ? "other is not null && " : "")}}{{equality}};

            /// <inheritdoc/>
            public int CompareTo({{context.NullableName}} other) => {{(context.IsClass ? "other is null ? 1 : " : "")}}{{comparison}};

            /// <inheritdoc/>
            public override bool Equals(object? obj) => obj is {{context.Name}} other && Equals(other);

            /// <inheritdoc/>
            public override int GetHashCode() => {{hash}};
            """);
        if (!context.HasCustomToString)
        {
            builder.AppendTabbedLine(1, $$"""

                /// <summary>Returns the value's text, or an empty string for a default string value.</summary>
                /// <returns>The non-null textual representation.</returns>
                public override string ToString() => {{display}};
                """);
        }
    }

    private static void GenerateOperators(StringBuilder builder, ValueTypeContext context)
    {
        if (context.IsClass)
        {
            builder.AppendTabbedLine(1, $"\nprivate static int Compare({context.Name}? a, {context.Name}? b) => a is null ? (b is null ? 0 : -1) : a.CompareTo(b);");
        }
        // Explicit docs, not <inheritdoc/>: the operator interfaces name these parameters left and right.
        string operand = context.IsClass ? ", or null" : "";
        foreach (var (op, summary, relation) in new[]
        {
            ("==", "Determines whether two values are equal.", "equals"),
            ("!=", "Determines whether two values are not equal.", "does not equal"),
            (">", "Determines whether the first value is greater than the second.", "is greater than"),
            ("<", "Determines whether the first value is less than the second.", "is less than"),
            (">=", "Determines whether the first value is greater than or equal to the second.", "is greater than or equal to"),
            ("<=", "Determines whether the first value is less than or equal to the second.", "is less than or equal to")
        })
        {
            string equality = context.IsClass ? "(ReferenceEquals(a, b) || (a is not null && a.Equals(b)))" : "a.Equals(b)";
            string body = op == "==" ? equality : op == "!=" ? "!" + equality : (context.IsClass ? "Compare(a, b)" : "a.CompareTo(b)") + " " + op + " 0";
            string nulls = !context.IsClass ? "" : op is "==" or "!=" ? " Two nulls are equal; null never equals an instance." : " Null is less than every instance.";
            builder.AppendTabbedLine(1, $$"""

                /// <summary>{{summary}}{{nulls}}</summary>
                /// <param name="a">The first value to compare{{operand}}.</param>
                /// <param name="b">The second value to compare{{operand}}.</param>
                /// <returns>True when <paramref name="a"/> {{relation}} <paramref name="b"/>; otherwise false.</returns>
                public static bool operator {{op}}({{context.NullableName}} a, {{context.NullableName}} b) => {{body}};
                """);
        }
        if (!context.HasImplicitOperators)
        {
            return;
        }
        bool nullableString = context.IsClass && context.IsString;
        builder.AppendTabbedLine(1, $$"""

            /// <summary>Converts a value object to its runtime value.</summary>
            /// <param name="item">The value object.</param>
            /// <returns>The underlying runtime value{{(nullableString ? ", or null for a null object" : "")}}.</returns>
            """);
        if (context.IsClass && !context.IsString)
        {
            builder.AppendTabbedLine(1, "/// <exception cref=\"ArgumentNullException\">The object is null.</exception>");
        }
        if (nullableString)
        {
            builder.AppendTabbedLine(1, "[return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(item))]");
        }
        string toValue = nullableString ? "item?.Value" : context.IsClass ? "(item ?? throw new ArgumentNullException(nameof(item))).Value" : "item.Value";
        builder.AppendTabbedLine(1, $"public static implicit operator {context.Type}{(nullableString ? "?" : "")}({context.NullableName} item) => {toValue};");
        builder.AppendTabbedLine(1, $$"""

            /// <summary>Converts a runtime value to its value object.</summary>
            /// <param name="item">The runtime value.</param>
            /// <returns>The value object{{(nullableString ? ", or null for a null string" : "")}}.</returns>
            """);
        if (context.HasValidation)
        {
            builder.AppendTabbedLine(1, "/// <exception cref=\"ArgumentException\">The value fails validation.</exception>");
        }
        if (nullableString)
        {
            builder.AppendTabbedLine(1, "[return: global::System.Diagnostics.CodeAnalysis.NotNullIfNotNull(nameof(item))]");
        }
        builder.AppendTabbedLine(1, $"public static implicit operator {context.Name}{(nullableString ? "?" : "")}({context.Type}{(nullableString ? "?" : "")} item) => {(nullableString ? "item is null ? null : " : "")}new {context.Name}(item);");
    }

    private static void GenerateParsing(StringBuilder builder, ValueTypeContext context)
    {
        string syntax = context.RuntimeType == ObjectRuntimeType.Ulid ? "" : "[global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute." + (context.RuntimeType == ObjectRuntimeType.Guid ? "GuidFormat" : "NumericFormat") + ")] ";
        // Ulid formatting ignores the format; Guid and numeric formatting reject one they do not know.
        string formatException = context.RuntimeType == ObjectRuntimeType.Ulid ? "" : "\n/// <exception cref=\"FormatException\"><paramref name=\"format\"/> is not a format the runtime type supports.</exception>";
        builder.AppendTabbedLine(1, $$"""

            /// <inheritdoc/>{{formatException}}
            public string ToString({{syntax}}string? format, IFormatProvider? formatProvider) => Value.ToString(format, formatProvider);

            /// <inheritdoc/>{{formatException}}
            public bool TryFormat(Span<char> destination, out int charsWritten, {{syntax}}ReadOnlySpan<char> format, IFormatProvider? provider) => Value.TryFormat(destination, out charsWritten, format{{(context.RuntimeType == ObjectRuntimeType.Guid ? "" : ", provider")}});
            """);
        foreach (bool span in new[] { false, true })
        {
            string inputType = span ? "ReadOnlySpan<char>" : "string";
            string parameter = span ? "span" : "value";
            // Explicit docs, not <inheritdoc/>: IParsable<T> and ISpanParsable<T> name the text parameter s.
            string exceptions = DocLines(
                "/// <exception cref=\"FormatException\">The text cannot be parsed" + (context.HasValidation ? " or its value fails validation" : "") + ".</exception>",
                context.CanOverflow ? "/// <exception cref=\"OverflowException\">The parsed number is outside the runtime type's range.</exception>" : null,
                span ? null : "/// <exception cref=\"ArgumentNullException\">The text is null.</exception>");
            builder.AppendTabbedLine(1, $$"""

                /// <summary>Parses a runtime value using the current culture and validates it.</summary>
                /// <param name="{{parameter}}">The text to parse.</param>
                /// <returns>The parsed value object.</returns>
                {{exceptions}}
                public static {{context.Name}} Parse({{inputType}} {{parameter}}) => Parse({{parameter}}, null);

                /// <summary>Parses a runtime value using a format provider and validates it.</summary>
                /// <param name="{{parameter}}">The text to parse.</param>
                /// <param name="provider">An object that supplies culture-specific formatting information, or null for the current culture.</param>
                /// <returns>The parsed value object.</returns>
                {{exceptions}}
                public static {{context.Name}} Parse({{inputType}} {{parameter}}, IFormatProvider? provider)
                {
                """);
            if (!span)
            {
                builder.AppendTabbedLine(2, $"ArgumentNullException.ThrowIfNull({parameter});");
            }
            if (context.RuntimeType == ObjectRuntimeType.Ulid)
            {
                builder.AppendTabbedLine(2, $$"""
                    if (!{{context.Type}}.TryParse({{parameter}}, provider, out var parsed))
                    {
                        throw new FormatException();
                    }
                    """);
            }
            else
            {
                builder.AppendTabbedLine(2, $"var parsed = {context.Type}.Parse({parameter}, provider);");
            }
            if (context.HasValidation)
            {
                builder.AppendTabbedLine(2, "if (!IsValid(parsed, out string? message))\n{\n\tthrow new FormatException(message);\n}");
            }
            builder.AppendTabbedLine(2, $"return {context.Construct("parsed")};");
            builder.AppendTabbedLine(1, "}");
            string tryType = span ? "ReadOnlySpan<char>" : "[global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string?";
            string resultType = "[global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out " + context.Name;
            builder.AppendTabbedLine(1, $$"""

                /// <summary>Attempts to parse and validate a runtime value using the current culture.</summary>
                /// <param name="{{parameter}}">The text to parse.</param>
                /// <param name="result">The parsed value on success; otherwise default.</param>
                /// <returns>True when parsing and validation succeed; otherwise false.</returns>
                public static bool TryParse({{tryType}} {{parameter}}, {{resultType}} result) => TryParse({{parameter}}, null, out result);

                /// <summary>Attempts to parse and validate a runtime value using a format provider.</summary>
                /// <param name="{{parameter}}">The text to parse.</param>
                /// <param name="provider">An object that supplies culture-specific formatting information, or null for the current culture.</param>
                /// <param name="result">The parsed value on success; otherwise default.</param>
                /// <returns>True when parsing and validation succeed; otherwise false.</returns>
                public static bool TryParse({{tryType}} {{parameter}}, IFormatProvider? provider, {{resultType}} result)
                {
                    result = default;
                    if (!{{context.Type}}.TryParse({{parameter}}, provider, out var parsed){{(context.HasValidation ? " || !IsValid(parsed, out _)" : "")}})
                    {
                        return false;
                    }

                    result = {{context.Construct("parsed")}};
                    return true;
                }
                """);
        }
    }

    private static void GenerateWellKnownValues(StringBuilder builder, ValueTypeContext context)
    {
        if (context.RuntimeType != ObjectRuntimeType.Guid && context.RuntimeType != ObjectRuntimeType.Ulid)
        {
            return;
        }
        string newException = context.HasValidation ? "\n/// <exception cref=\"ArgumentException\">The generated identifier fails validation.</exception>" : "";
        string emptyException = context.HasValidation ? "\n/// <exception cref=\"ArgumentException\">The empty identifier fails validation.</exception>" : "";
        builder.AppendTabbedLine(1, $$"""

            /// <summary>Creates a new identifier.</summary>
            /// <returns>A newly generated identifier.</returns>{{newException}}
            public static {{context.Name}} New() => new {{context.Name}}({{context.Type}}.{{(context.RuntimeType == ObjectRuntimeType.Guid ? "NewGuid" : "NewUlid")}}());

            /// <summary>The identifier whose underlying value is empty.</summary>{{emptyException}}
            public static readonly {{context.Name}} Empty = new {{context.Name}}({{context.Type}}.Empty);
            """);
    }

    private static void GenerateJsonConverter(StringBuilder builder, ValueTypeContext context)
    {
        string getter = context.RuntimeType switch
        {
            ObjectRuntimeType.Int => "GetInt32()", ObjectRuntimeType.Short => "GetInt16()",
            ObjectRuntimeType.Long => "GetInt64()", ObjectRuntimeType.UInt => "GetUInt32()",
            ObjectRuntimeType.UShort => "GetUInt16()", ObjectRuntimeType.ULong => "GetUInt64()",
            ObjectRuntimeType.Double => "GetDouble()", ObjectRuntimeType.Decimal => "GetDecimal()",
            ObjectRuntimeType.Guid => "GetGuid()", _ => "GetString()!"
        };
        string write = context.IsString || context.RuntimeType == ObjectRuntimeType.Guid ? "WriteStringValue(value.Value)" : context.RuntimeType == ObjectRuntimeType.Ulid ? "WriteStringValue(value.Value.ToString())" : "WriteNumberValue(value.Value)";
        // Utf8JsonWriter failures the write methods propagate: oversized strings, non-finite doubles and writer state.
        string writeExceptions = DocLines(
            context.IsString ? "/// <exception cref=\"ArgumentException\">The underlying string is too long for the writer to encode.</exception>" : null,
            context.RuntimeType == ObjectRuntimeType.Double ? "/// <exception cref=\"ArgumentException\">The underlying value is NaN or infinite, which a JSON number cannot represent.</exception>" : null,
            "/// <exception cref=\"InvalidOperationException\">Validation is enabled, and writing a value at the writer's current position would produce invalid JSON.</exception>");
        string propertyNameExceptions = DocLines(
            context.IsString ? "/// <exception cref=\"global::System.Text.Json.JsonException\">The underlying string is null.</exception>" : null,
            context.IsString ? "/// <exception cref=\"ArgumentException\">The underlying string is too long for a JSON property name.</exception>" : null,
            "/// <exception cref=\"InvalidOperationException\">Validation is enabled, and writing a property name at the writer's current position would produce invalid JSON.</exception>");
        builder.AppendTabbedLine(1, $$"""

            /// <summary>Converts <see cref="{{context.Name}}"/> values and dictionary keys to and from JSON.</summary>
            /// <remarks>Public so source-generated serializer contexts in other assemblies can construct it.</remarks>
            public sealed partial class {{context.Name}}JsonConverter : global::System.Text.Json.Serialization.JsonConverter<{{context.Name}}>
            {
                // Deviates from the private-constructor rule: System.Text.Json requires a public parameterless constructor.
                /// <summary>Initializes the converter for use by System.Text.Json serializer contexts.</summary>
                public {{context.Name}}JsonConverter()
                {
                }

                /// <summary>Reads a value from its JSON representation.</summary>
                /// <param name="reader">The reader positioned at the value.</param>
                /// <param name="typeToConvert">The type being converted.</param>
                /// <param name="options">The serializer options.</param>
                /// <returns>The converted value{{(context.IsClass ? "" : ", or default for JSON null")}}.</returns>
                /// <exception cref="global::System.Text.Json.JsonException">The JSON value cannot be parsed or fails validation.</exception>
                public override {{context.Name}} Read(ref global::System.Text.Json.Utf8JsonReader reader, Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)
                {
            """);
        if (!context.IsClass)
        {
            builder.AppendTabbedLine(3, "if (reader.TokenType == global::System.Text.Json.JsonTokenType.Null)\n{\n\treturn default;\n}\n");
        }
        builder.AppendTabbedLine(3, "try\n{");
        if (context.RuntimeType == ObjectRuntimeType.Ulid)
        {
            builder.AppendTabbedLine(4, "var value = ParseUlid(reader.GetString());");
        }
        else
        {
            builder.AppendTabbedLine(4, "var value = reader." + getter + ";");
        }
        AppendJsonConstruction(builder, context, 4);
        AppendJsonCatch(builder, 3);
        builder.AppendTabbedLine(2, $$"""
                }

                /// <summary>Writes the underlying runtime value as JSON.</summary>
                /// <param name="writer">The writer receiving the value.</param>
                /// <param name="value">The value to write.</param>
                /// <param name="options">The serializer options.</param>
                {{writeExceptions}}
                public override void Write(global::System.Text.Json.Utf8JsonWriter writer, {{context.Name}} value, global::System.Text.Json.JsonSerializerOptions options) => writer.{{write}};

                /// <summary>Reads a dictionary key from a JSON property name.</summary>
                /// <param name="reader">The reader positioned at the property name.</param>
                /// <param name="typeToConvert">The type being converted.</param>
                /// <param name="options">The serializer options.</param>
                /// <returns>The converted dictionary key.</returns>
                /// <exception cref="global::System.Text.Json.JsonException">The property name cannot be parsed or fails validation.</exception>
                public override {{context.Name}} ReadAsPropertyName(ref global::System.Text.Json.Utf8JsonReader reader, Type typeToConvert, global::System.Text.Json.JsonSerializerOptions options)
                {
                    try
                    {
                """);
        if (context.IsString)
        {
            builder.AppendTabbedLine(4, "var value = reader.GetString()!;");
            AppendJsonConstruction(builder, context, 4);
        }
        else if (context.RuntimeType == ObjectRuntimeType.Ulid)
        {
            builder.AppendTabbedLine(4, "var value = ParseUlid(reader.GetString());");
            AppendJsonConstruction(builder, context, 4);
        }
        else
        {
            builder.AppendTabbedLine(4, "return Parse(reader.GetString()!, global::System.Globalization.CultureInfo.InvariantCulture);");
        }
        AppendJsonCatch(builder, 3);
        builder.AppendTabbedLine(2, $$"""
                }

                /// <summary>Writes a dictionary key as a JSON property name.</summary>
                /// <param name="writer">The writer receiving the property name.</param>
                /// <param name="value">The dictionary key to write.</param>
                /// <param name="options">The serializer options.</param>
                {{propertyNameExceptions}}
                public override void WriteAsPropertyName(global::System.Text.Json.Utf8JsonWriter writer, {{context.Name}} value, global::System.Text.Json.JsonSerializerOptions options)
                {
                """);
        if (context.IsString)
        {
            builder.AppendTabbedLine(3, "if (value.Value is null)\n{\n\tthrow new global::System.Text.Json.JsonException(new ArgumentNullException(nameof(value.Value)).Message);\n}\nwriter.WritePropertyName(value.Value);");
        }
        else
        {
            builder.AppendTabbedLine(3, "writer.WritePropertyName(value.Value.ToString(null, global::System.Globalization.CultureInfo.InvariantCulture));");
        }
        builder.AppendTabbedLine(2, "}");
        if (context.RuntimeType == ObjectRuntimeType.Ulid)
        {
            builder.AppendTabbedLine(2, """

                /// <summary>Parses the underlying ULID from JSON text.</summary>
                /// <param name="text">The text to parse.</param>
                /// <returns>The parsed ULID.</returns>
                /// <exception cref="FormatException">The text is not a valid ULID.</exception>
                private static global::System.Ulid ParseUlid(string? text)
                {
                    if (!global::System.Ulid.TryParse(text, out var value))
                    {
                        throw new FormatException();
                    }

                    return value;
                }
                """);
        }
        builder.AppendTabbedLine(1, "}");
    }

    private static void AppendJsonConstruction(StringBuilder builder, ValueTypeContext context, ushort tabs)
    {
        if (context.HasValidation)
        {
            builder.AppendTabbedLine(tabs, "if (!IsValid(value, out string? message))\n{\n\tthrow new global::System.Text.Json.JsonException(message);\n}");
        }
        builder.AppendTabbedLine(tabs, "return " + context.Construct("value") + ";");
    }

    private static void AppendJsonCatch(StringBuilder builder, ushort tabs)
    {
        builder.AppendTabbedLine(tabs, "}\ncatch (Exception exception) when (exception is FormatException || exception is ArgumentException || exception is OverflowException || exception is InvalidOperationException)\n{\n\tthrow new global::System.Text.Json.JsonException(exception.Message, exception);\n}");
    }

    // Joins the documentation lines that apply, so an omitted line leaves no blank line before the member.
    private static string DocLines(params string?[] lines) => string.Join("\n", lines.Where(line => line is not null));
}
