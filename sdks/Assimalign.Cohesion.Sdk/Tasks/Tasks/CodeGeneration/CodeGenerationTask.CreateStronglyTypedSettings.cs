using Microsoft.Build.Framework;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace Assimalign.Cohesion.Sdk.Tasks;

/*
Converts .json settings files into C# types that can be used in the codebase. The Task should be invoked 
in the design build phase, and the generated code should be added as an additional file to the compilation.
 */
public class CreateStronglyTypedSettingsTask : CodeGenerationTask
{
    [Required]
    public ITaskItem[]? AppSettingsFiles { get; set; }

    [Required]
    public string? AppSettingsNamespace { get; set; }

    [Required]
    public string? AppSettingsClass { get; set; }

    [Required]
    public string? AppSettingsOutputPath { get; set; }
    
    public override bool Execute()
    {
        Dictionary<string, object> schema = new(StringComparer.OrdinalIgnoreCase);

        try
        {
            // We need to iterate through each appsettings.json file to merge them
            // into a single schema. This allows us to support multiple files for
            // different environments (e.g., appsettings.Development.json, appsettings.Production.json)
            // and merge their settings together.
            for (int i = 0; i < AppSettingsFiles!.Length; i++)
            {
                ITaskItem item = AppSettingsFiles[i];

                // The appsettings.json path
                string? path = item.ItemSpec;

                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    Log.LogError($"Settings file not found: {path}");
                    return false;
                }

                // Read the appsettings.json file
                using Stream stream = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);

                // Parse the JSON document
                using JsonDocument document = JsonDocument.Parse(stream);

                // Parse the JSON document and populate the schema
                Parse(schema, document.RootElement);
            }

            // Generate the C# code based on the merged schema

            StringBuilder sb = new StringBuilder();
            sb.AppendLine($"namespace {AppSettingsNamespace};");
            sb.AppendLine();

            Write(sb, schema, AppSettingsClass);

            Directory.CreateDirectory(Path.GetDirectoryName(AppSettingsOutputPath)!);

            using (Stream stream = File.Open(AppSettingsOutputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
            {
                byte[] buffer = Encoding.UTF8.GetBytes(sb.ToString());

                stream.Write(buffer, 0, buffer.Length);
            }

            Log.LogMessage(MessageImportance.High, $"Generated: {AppSettingsOutputPath}");

            return true;
        }
        catch (Exception exception)
        {
            Log.LogErrorFromException(exception);
            return false;
        }
    }

    private void Write(StringBuilder sb, Dictionary<string, object> schema, string? typeName)
    {
        sb.AppendLine($"internal class {typeName}");
        sb.AppendLine("{");

        foreach (KeyValuePair<string, object> entry in schema)
        {
            string name = entry.Key;

            if (entry.Value is string str)
            {
                sb.AppendLine($"	public {str}? {name} {{ get; set; }}");
            }
            else if (entry.Value is Dictionary<string, object>)
            {
                sb.AppendLine($"	public {typeName}{name}? {name} {{ get; set; }}");
            }
            else if (entry.Value is List<object> items1 && items1.First() is Dictionary<string, object>)
            {
                sb.AppendLine($"	public IEnumerable<{typeName}{name}?>? {name} {{ get; set; }}");
            }
            else if (entry.Value is List<object> items2 && items2.First() is string val)
            {
                sb.AppendLine($"	public IEnumerable<{val}?>? {name} {{ get; set; }}");
            }
        }

        sb.AppendLine("}");
        sb.AppendLine();

        foreach (KeyValuePair<string, object> children in schema.Where(item => item.Value is Dictionary<string, object>))
        {
            Write(sb, (Dictionary<string, object>)children.Value, $"{typeName}{children.Key}");
        }

        foreach (KeyValuePair<string, object> children in schema.Where(item => item.Value is List<object> array && array.First() is Dictionary<string, object>))
        {
            Write(sb, (Dictionary<string, object>)((List<object>)children.Value).First(), $"{typeName}{children.Key}");
        }
    }

    private void Parse(Dictionary<string, object> schema, JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (JsonProperty property in element.EnumerateObject())
            {
                string name = property.Name;
                string type = property.Value.ValueKind switch
                {
                    JsonValueKind.String => "string",
                    JsonValueKind.Number => property.Value.TryGetInt64(out _) ? "long" : "double",
                    JsonValueKind.True or JsonValueKind.False => "bool",
                    JsonValueKind.Object => "object",
                    JsonValueKind.Array => "array",
                    _ => "string"
                };

                if (type == "object")
                {
                    if (!schema.TryGetValue(name, out object? item) || item is not Dictionary<string, object>)
                    {
                        schema[name] = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
                    }

                    Parse((Dictionary<string, object>)schema[name], property.Value);
                }

                else if (type == "array")
                {
                    if (!schema.TryGetValue(name, out object? item) || item is not List<object>)
                    {
                        schema[name] = new List<object>();
                    }

                    List<object> items = (List<object>)schema[name];

                    foreach (JsonElement child in property.Value.EnumerateArray())
                    {
                        if (child.ValueKind == JsonValueKind.Object)
                        {
                            Dictionary<string, object>? nested = default;

                            if (items.Count == 0)
                            {
                                items.Add((nested = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)));
                            }

                            if (items.First() is not Dictionary<string, object> nested1)
                            {
                                items.Clear();
                                items.Add((nested = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)));
                            }
                            else
                            {
                                nested = nested1;
                            }

                            Parse(nested, child);
                        }

                        else
                        {
                            items.Add(child.ValueKind switch
                            {
                                JsonValueKind.String => "string",
                                JsonValueKind.Number => property.Value.TryGetInt64(out _) ? "long" : "double",
                                JsonValueKind.True or JsonValueKind.False => "bool",
                                JsonValueKind.Object => "object",
                                JsonValueKind.Array => "array",
                                _ => "string"
                            });

                            break;
                        }
                    }
                }

                else
                {
                    schema[name] = type;
                }
            }
        }
    }
}
