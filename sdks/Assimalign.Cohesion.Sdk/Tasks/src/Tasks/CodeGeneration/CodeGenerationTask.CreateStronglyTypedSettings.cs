using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;

using Microsoft.Build.Framework;

namespace Assimalign.Cohesion.Sdk.Tasks;

/// <summary>
/// Generates a strongly typed settings object from a consumer's appsettings JSON files.
/// </summary>
public sealed class CreateStronglyTypedSettingsTask : CodeGenerationTask
{
    /// <summary>
    /// Gets or sets the appsettings JSON files that define the generated settings shape.
    /// </summary>
    public ITaskItem[] AppSettingsFiles { get; set; } = [];

    /// <summary>
    /// Gets or sets the namespace for the generated settings types.
    /// </summary>
    [Required]
    public string AppSettingsNamespace { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the name of the generated root settings type.
    /// </summary>
    [Required]
    public string AppSettingsClass { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the generated source output path.
    /// </summary>
    [Required]
    public string AppSettingsOutputPath { get; set; } = string.Empty;

    /// <summary>
    /// Generates the settings source file.
    /// </summary>
    /// <returns><see langword="true"/> when generation succeeds; otherwise, <see langword="false"/>.</returns>
    public override bool Execute()
    {
        var schema = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

        try
        {
            string[] appSettingsPaths = new string[AppSettingsFiles.Length];
            for (int i = 0; i < AppSettingsFiles.Length; i++)
            {
                appSettingsPaths[i] = AppSettingsFiles[i].ItemSpec;
            }
            Array.Sort(appSettingsPaths, StringComparer.OrdinalIgnoreCase);

            // Merge each environment-specific file into one compile-time shape.
            for (int i = 0; i < appSettingsPaths.Length; i++)
            {
                string path = appSettingsPaths[i];
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Log.LogError($"Settings file not found: {path}");
                    return false;
                }

                using Stream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
                using JsonDocument document = JsonDocument.Parse(
                    stream,
                    new JsonDocumentOptions
                    {
                        AllowTrailingCommas = true,
                        CommentHandling = JsonCommentHandling.Skip
                    });

                Parse(schema, document.RootElement);
            }

            var source = new StringBuilder();
            source.AppendLine("#nullable enable");
            source.AppendLine();
            source.AppendLine("using System;");
            if (ContainsArray(schema))
            {
                source.AppendLine("using System.Collections.Generic;");
            }
            if (ContainsScalarType(schema, "long") || ContainsScalarType(schema, "double"))
            {
                source.AppendLine("using System.Globalization;");
            }
            source.AppendLine();
            source.AppendLine("using Assimalign.Cohesion.Configuration;");
            source.AppendLine();
            source.AppendLine($"namespace {AppSettingsNamespace};");
            source.AppendLine();

            WriteType(source, schema, AppSettingsClass, writeBinder: true);

            string? outputDirectory = Path.GetDirectoryName(AppSettingsOutputPath);
            if (!string.IsNullOrEmpty(outputDirectory))
            {
                Directory.CreateDirectory(outputDirectory);
            }

            File.WriteAllText(
                AppSettingsOutputPath,
                source.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            Log.LogMessage(MessageImportance.High, $"Generated: {AppSettingsOutputPath}");

            return true;
        }
        // This is the MSBuild task boundary: every generation failure must become a build error
        // instead of escaping into the host and obscuring the consumer project that caused it.
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception, showStackTrace: false);
            return false;
        }
    }

    private static void WriteType(
        StringBuilder source,
        Dictionary<string, object> schema,
        string typeName,
        bool writeBinder)
    {
        source.AppendLine("/// <summary>");
        source.AppendLine("/// Represents settings generated from the consumer's appsettings JSON files.");
        source.AppendLine("/// </summary>");
        source.AppendLine($"public class {typeName}");
        source.AppendLine("{");

        foreach (KeyValuePair<string, object> entry in schema)
        {
            string propertyType = GetPropertyType(typeName, entry.Key, entry.Value);
            source.AppendLine("    /// <summary>");
            source.AppendLine("    /// Gets or sets the generated configuration value.");
            source.AppendLine("    /// </summary>");
            source.AppendLine($"    public {propertyType}? {entry.Key} {{ get; set; }}");
            source.AppendLine();
        }

        if (writeBinder)
        {
            WriteBinder(source, schema, typeName);
        }

        source.AppendLine("}");
        source.AppendLine();

        foreach (KeyValuePair<string, object> entry in schema)
        {
            if (entry.Value is Dictionary<string, object> child)
            {
                WriteType(source, child, $"{typeName}{entry.Key}", writeBinder: false);
            }
            else if (entry.Value is ArraySchema { Element: Dictionary<string, object> elementSchema })
            {
                WriteType(source, elementSchema, $"{typeName}{entry.Key}", writeBinder: false);
            }
        }
    }

    private static void WriteBinder(
        StringBuilder source,
        Dictionary<string, object> schema,
        string typeName)
    {
        source.AppendLine("    /// <summary>");
        source.AppendLine("    /// Binds values from the supplied Cohesion configuration without reflection.");
        source.AppendLine("    /// </summary>");
        source.AppendLine("    /// <param name=\"configuration\">The configuration root to read.</param>");
        source.AppendLine("    /// <exception cref=\"ArgumentNullException\">Thrown when <paramref name=\"configuration\"/> is null.</exception>");
        source.AppendLine("    /// <exception cref=\"FormatException\">Thrown when a configured scalar cannot be parsed as its generated type.</exception>");
        if (ContainsScalarType(schema, "long") || ContainsScalarType(schema, "double"))
        {
            source.AppendLine("    /// <exception cref=\"OverflowException\">Thrown when a configured number is outside the range of its generated type.</exception>");
        }
        source.AppendLine("    public void Bind(IConfiguration configuration)");
        source.AppendLine("    {");
        source.AppendLine("        ArgumentNullException.ThrowIfNull(configuration);");

        int variableIndex = 0;
        WriteBindings(
            source,
            schema,
            targetExpression: "this",
            typeName,
            path: string.Empty,
            indentation: 2,
            boundFlag: null,
            ref variableIndex);

        source.AppendLine("    }");

        if (ContainsScalarType(schema, "long"))
        {
            source.AppendLine();
            source.AppendLine("    private static long? ParseInt64(string? value)");
            source.AppendLine("    {");
            source.AppendLine("        return value is null");
            source.AppendLine("            ? null");
            source.AppendLine("            : long.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);");
            source.AppendLine("    }");
        }

        if (ContainsScalarType(schema, "double"))
        {
            source.AppendLine();
            source.AppendLine("    private static double? ParseDouble(string? value)");
            source.AppendLine("    {");
            source.AppendLine("        return value is null");
            source.AppendLine("            ? null");
            source.AppendLine("            : double.Parse(value, NumberStyles.Float | NumberStyles.AllowThousands, CultureInfo.InvariantCulture);");
            source.AppendLine("    }");
        }

        if (ContainsScalarType(schema, "bool"))
        {
            source.AppendLine();
            source.AppendLine("    private static bool? ParseBoolean(string? value)");
            source.AppendLine("    {");
            source.AppendLine("        return value is null ? null : bool.Parse(value);");
            source.AppendLine("    }");
        }
    }

    private static void WriteBindings(
        StringBuilder source,
        Dictionary<string, object> schema,
        string targetExpression,
        string typeName,
        string path,
        int indentation,
        string? boundFlag,
        ref int variableIndex)
    {
        foreach (KeyValuePair<string, object> entry in schema)
        {
            string propertyPath = CombinePath(path, entry.Key);
            string propertyExpression = $"{targetExpression}.{entry.Key}";

            if (entry.Value is string scalarType)
            {
                WriteScalarBinding(
                    source,
                    propertyExpression,
                    scalarType,
                    propertyPath,
                    indentation,
                    boundFlag,
                    ref variableIndex);
                continue;
            }

            if (entry.Value is Dictionary<string, object> child)
            {
                int currentIndex = variableIndex++;
                string childName = $"child{currentIndex}";
                string childBoundName = $"childBound{currentIndex}";
                string childTypeName = $"{typeName}{entry.Key}";

                AppendIndented(source, indentation, $"var {childName} = {propertyExpression} ?? new {childTypeName}();");
                AppendIndented(source, indentation, $"bool {childBoundName} = false;");
                WriteBindings(
                    source,
                    child,
                    childName,
                    childTypeName,
                    propertyPath,
                    indentation,
                    childBoundName,
                    ref variableIndex);
                AppendIndented(source, indentation, $"if ({childBoundName})");
                AppendIndented(source, indentation, "{");
                AppendIndented(source, indentation + 1, $"{propertyExpression} = {childName};");
                MarkBound(source, indentation + 1, boundFlag);
                AppendIndented(source, indentation, "}");
                continue;
            }

            if (entry.Value is ArraySchema array)
            {
                WriteArrayBinding(
                    source,
                    propertyExpression,
                    typeName,
                    entry.Key,
                    array,
                    propertyPath,
                    indentation,
                    boundFlag,
                    ref variableIndex);
            }
        }
    }

    private static void WriteArrayBinding(
        StringBuilder source,
        string propertyExpression,
        string declaringTypeName,
        string propertyName,
        ArraySchema array,
        string path,
        int indentation,
        string? boundFlag,
        ref int variableIndex)
    {
        int currentIndex = variableIndex++;
        string valuesName = $"values{currentIndex}";
        string valuesBoundName = $"valuesBound{currentIndex}";
        string elementType = GetArrayElementType(declaringTypeName, propertyName, array.Element);

        AppendIndented(
            source,
            indentation,
            $"var {valuesName} = {propertyExpression} is null");
        AppendIndented(source, indentation + 1, $"? new List<{elementType}?>()");
        AppendIndented(source, indentation + 1, $": new List<{elementType}?>({propertyExpression});");
        AppendIndented(source, indentation, $"bool {valuesBoundName} = false;");

        for (int index = 0; index < array.Length; index++)
        {
            string elementPath = CombinePath(path, index.ToString(CultureInfo.InvariantCulture));

            if (array.Element is Dictionary<string, object> objectElement)
            {
                int itemIndex = variableIndex++;
                string itemName = $"item{itemIndex}";
                string itemBoundName = $"itemBound{itemIndex}";
                string existingItemName = $"existing{itemIndex}";
                AppendIndented(
                    source,
                    indentation,
                    $"var {itemName} = {valuesName}.Count > {index} && {valuesName}[{index}] is {elementType} {existingItemName}");
                AppendIndented(source, indentation + 1, $"? {existingItemName}");
                AppendIndented(source, indentation + 1, $": new {elementType}();");
                AppendIndented(source, indentation, $"bool {itemBoundName} = false;");
                WriteBindings(
                    source,
                    objectElement,
                    itemName,
                    elementType,
                    elementPath,
                    indentation,
                    itemBoundName,
                    ref variableIndex);
                AppendIndented(source, indentation, $"if ({itemBoundName})");
                AppendIndented(source, indentation, "{");
                PadListToIndex(source, indentation + 1, valuesName, index);
                AppendIndented(source, indentation + 1, $"{valuesName}[{index}] = {itemName};");
                AppendIndented(source, indentation + 1, $"{valuesBoundName} = true;");
                AppendIndented(source, indentation, "}");
            }
            else
            {
                int rawIndex = variableIndex++;
                string rawName = $"raw{rawIndex}";
                string valueName = $"value{rawIndex}";
                string scalarType = array.Element as string ?? "string";
                AppendIndented(
                    source,
                    indentation,
                    $"string? {rawName} = configuration.GetEntry({StringLiteral(elementPath)}) is IConfigurationValue {valueName}");
                AppendIndented(source, indentation + 1, $"? {valueName}.Value");
                AppendIndented(source, indentation + 1, ": null;");
                AppendIndented(source, indentation, $"if ({rawName} is not null)");
                AppendIndented(source, indentation, "{");
                PadListToIndex(source, indentation + 1, valuesName, index);
                AppendIndented(
                    source,
                    indentation + 1,
                    $"{valuesName}[{index}] = {ReadScalarExpression(scalarType, rawName)};");
                AppendIndented(source, indentation + 1, $"{valuesBoundName} = true;");
                AppendIndented(source, indentation, "}");
            }
        }

        AppendIndented(source, indentation, $"if ({valuesBoundName})");
        AppendIndented(source, indentation, "{");
        AppendIndented(source, indentation + 1, $"{propertyExpression} = {valuesName};");
        MarkBound(source, indentation + 1, boundFlag);
        AppendIndented(source, indentation, "}");
    }

    private static void WriteScalarBinding(
        StringBuilder source,
        string propertyExpression,
        string scalarType,
        string path,
        int indentation,
        string? boundFlag,
        ref int variableIndex)
    {
        int currentIndex = variableIndex++;
        string rawName = $"raw{currentIndex}";
        string valueName = $"value{currentIndex}";
        AppendIndented(
            source,
            indentation,
            $"string? {rawName} = configuration.GetEntry({StringLiteral(path)}) is IConfigurationValue {valueName}");
        AppendIndented(source, indentation + 1, $"? {valueName}.Value");
        AppendIndented(source, indentation + 1, ": null;");
        AppendIndented(source, indentation, $"if ({rawName} is not null)");
        AppendIndented(source, indentation, "{");
        AppendIndented(
            source,
            indentation + 1,
            $"{propertyExpression} = {ReadScalarExpression(scalarType, rawName)};");
        MarkBound(source, indentation + 1, boundFlag);
        AppendIndented(source, indentation, "}");
    }

    private static void PadListToIndex(
        StringBuilder source,
        int indentation,
        string valuesName,
        int index)
    {
        AppendIndented(source, indentation, $"while ({valuesName}.Count <= {index})");
        AppendIndented(source, indentation, "{");
        AppendIndented(source, indentation + 1, $"{valuesName}.Add(null);");
        AppendIndented(source, indentation, "}");
    }

    private static void MarkBound(StringBuilder source, int indentation, string? boundFlag)
    {
        if (boundFlag is not null)
        {
            AppendIndented(source, indentation, $"{boundFlag} = true;");
        }
    }

    private static string GetPropertyType(string typeName, string propertyName, object value)
    {
        return value switch
        {
            string scalarType => scalarType,
            Dictionary<string, object> => $"{typeName}{propertyName}",
            ArraySchema array => $"IEnumerable<{GetArrayElementType(typeName, propertyName, array.Element)}?>",
            _ => "string"
        };
    }

    private static string GetArrayElementType(string typeName, string propertyName, object element)
    {
        return element is Dictionary<string, object>
            ? $"{typeName}{propertyName}"
            : element as string ?? "string";
    }

    private static string ReadScalarExpression(string scalarType, string valueExpression)
    {
        return scalarType switch
        {
            "long" => $"ParseInt64({valueExpression})",
            "double" => $"ParseDouble({valueExpression})",
            "bool" => $"ParseBoolean({valueExpression})",
            _ => valueExpression
        };
    }

    private static bool ContainsArray(Dictionary<string, object> schema)
    {
        foreach (object value in schema.Values)
        {
            if (value is ArraySchema)
            {
                return true;
            }

            if (value is Dictionary<string, object> child && ContainsArray(child))
            {
                return true;
            }
        }

        return false;
    }

    private static bool ContainsScalarType(Dictionary<string, object> schema, string scalarType)
    {
        foreach (object value in schema.Values)
        {
            if (value is string currentType && string.Equals(currentType, scalarType, StringComparison.Ordinal))
            {
                return true;
            }

            if (value is Dictionary<string, object> child && ContainsScalarType(child, scalarType))
            {
                return true;
            }

            if (value is ArraySchema array)
            {
                if (array.Element is string elementType && string.Equals(elementType, scalarType, StringComparison.Ordinal))
                {
                    return true;
                }

                if (array.Element is Dictionary<string, object> elementSchema && ContainsScalarType(elementSchema, scalarType))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static void Parse(Dictionary<string, object> schema, JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        foreach (JsonProperty property in element.EnumerateObject())
        {
            string name = property.Name;

            if (property.Value.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                if (!schema.TryGetValue(name, out object? item))
                {
                    schema[name] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }
                else if (item is not Dictionary<string, object>)
                {
                    throw new InvalidOperationException(
                        $"Settings property '{name}' has incompatible object and scalar shapes.");
                }

                Parse((Dictionary<string, object>)schema[name], property.Value);
                continue;
            }

            if (property.Value.ValueKind == JsonValueKind.Array)
            {
                ArraySchema array;
                if (!schema.TryGetValue(name, out object? item))
                {
                    schema[name] = array = new ArraySchema();
                }
                else if (item is ArraySchema existingArray)
                {
                    array = existingArray;
                }
                else
                {
                    throw new InvalidOperationException(
                        $"Settings property '{name}' has incompatible array and non-array shapes.");
                }

                Parse(array, property.Value);
                continue;
            }

            string scalarType = GetScalarType(property.Value);
            if (!schema.TryGetValue(name, out object? existing))
            {
                schema[name] = scalarType;
            }
            else if (existing is string existingScalarType)
            {
                schema[name] = MergeScalarTypes(existingScalarType, scalarType, name);
            }
            else
            {
                throw new InvalidOperationException(
                    $"Settings property '{name}' has incompatible scalar and composite shapes.");
            }
        }
    }

    private static void Parse(ArraySchema schema, JsonElement element)
    {
        int index = 0;
        foreach (JsonElement child in element.EnumerateArray())
        {
            schema.Length = Math.Max(schema.Length, index + 1);
            if (child.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                index++;
                continue;
            }

            if (child.ValueKind == JsonValueKind.Object)
            {
                Dictionary<string, object> objectElement;
                if (!schema.HasElement)
                {
                    schema.Element = objectElement = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                }
                else if (schema.Element is Dictionary<string, object> existingObjectElement)
                {
                    objectElement = existingObjectElement;
                }
                else
                {
                    throw new InvalidOperationException(
                        "A settings array cannot mix object and scalar elements.");
                }

                Parse(objectElement, child);
            }
            else if (child.ValueKind == JsonValueKind.Array)
            {
                throw new InvalidOperationException("Nested settings arrays are not supported.");
            }
            else
            {
                string scalarType = GetScalarType(child);
                schema.Element = !schema.HasElement
                    ? scalarType
                    : schema.Element is string existingScalarType
                        ? MergeScalarTypes(existingScalarType, scalarType, "array element")
                        : throw new InvalidOperationException(
                            "A settings array cannot mix scalar and object elements.");
            }

            schema.HasElement = true;
            index++;
        }
    }

    private static string GetScalarType(JsonElement element)
    {
        return element.ValueKind switch
        {
            JsonValueKind.String => "string",
            JsonValueKind.Number => element.TryGetInt64(out _) ? "long" : "double",
            JsonValueKind.True or JsonValueKind.False => "bool",
            _ => "string"
        };
    }

    private static string MergeScalarTypes(string left, string right, string propertyName)
    {
        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return left;
        }

        if ((string.Equals(left, "long", StringComparison.Ordinal) &&
             string.Equals(right, "double", StringComparison.Ordinal)) ||
            (string.Equals(left, "double", StringComparison.Ordinal) &&
             string.Equals(right, "long", StringComparison.Ordinal)))
        {
            return "double";
        }

        throw new InvalidOperationException(
            $"Settings property '{propertyName}' has incompatible scalar types '{left}' and '{right}'.");
    }

    private static string CombinePath(string path, string segment)
    {
        return string.IsNullOrEmpty(path) ? segment : $"{path}:{segment}";
    }

    private static string StringLiteral(string value)
    {
        var literal = new StringBuilder(value.Length + 2);
        literal.Append('"');
        foreach (char character in value)
        {
            switch (character)
            {
                case '"':
                    literal.Append("\\\"");
                    break;
                case '\\':
                    literal.Append("\\\\");
                    break;
                case '\0':
                    literal.Append("\\0");
                    break;
                case '\a':
                    literal.Append("\\a");
                    break;
                case '\b':
                    literal.Append("\\b");
                    break;
                case '\f':
                    literal.Append("\\f");
                    break;
                case '\n':
                    literal.Append("\\n");
                    break;
                case '\r':
                    literal.Append("\\r");
                    break;
                case '\t':
                    literal.Append("\\t");
                    break;
                case '\v':
                    literal.Append("\\v");
                    break;
                default:
                    if (char.IsControl(character) || character is '\u2028' or '\u2029')
                    {
                        literal.Append("\\u");
                        literal.Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    }
                    else
                    {
                        literal.Append(character);
                    }
                    break;
            }
        }

        literal.Append('"');
        return literal.ToString();
    }

    private static void AppendIndented(StringBuilder source, int indentation, string value)
    {
        source.Append(' ', indentation * 4);
        source.AppendLine(value);
    }

    private sealed class ArraySchema
    {
        public object Element { get; set; } = "string";

        public bool HasElement { get; set; }

        public int Length { get; set; }
    }
}
