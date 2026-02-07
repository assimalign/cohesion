using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Build.Tasks;

public class CodeGenerationCreateValueTypeTask : CodeGenerationTask
{
    [Required]
    public ITaskItem[]? ValueTypes { get; set; }

    [Output]
    public ITaskItem[]? ValueTypesCreated { get; set; }

    public override bool Execute()
    {
        // Ensure Value Types where provided
        if (ValueTypes is null || ValueTypes.Length == 0)
        {
            Log.LogError("No ValueTypes were specified for code generation.");
            return false;
        }

        try
        {
            StringBuilder? builder = null;
            DirectoryInfo? directoryInfo = null;
            List<ValueTypeContext> contexts = new List<ValueTypeContext>();
            List<ITaskItem> created = new List<ITaskItem>();

            foreach (var valueType in ValueTypes)
            {
                // Set Current Execution Stage
                Log.LogMessage("Beginning Value Type Generation: {0}", valueType.ItemSpec);

                string name = Path.GetFileNameWithoutExtension(valueType.ItemSpec);
                string fileName = Path.GetFileName(valueType.ItemSpec);
                string fileDirectory = Path.GetDirectoryName(valueType.ItemSpec)!;
                string filePath = string.IsNullOrEmpty(fileDirectory) ?
                         Path.Combine(CodeGenOutputPath!, fileName) :
                         Path.Combine(CodeGenOutputPath!, fileDirectory!, fileName);

                // Set File System information
                var context = new ValueTypeContext()
                {
                    FileName = fileName,
                    FileDirectory = fileDirectory,
                    FilePath = filePath,
                    ObjectName = name,
                    ObjectRuntimeType = valueType.GetMetadata<ObjectRuntimeType>("ObjectRuntimeType", isRequired: true),
                    ObjectNamespace = valueType.GetMetadata<string>("ObjectNamespace", isRequired: true)!,
                    ObjectAccessModifier = valueType.GetMetadata<ObjectAccessModifier>("ObjectAccessModifier"),
                    ObjectKind = valueType.GetMetadata<ObjectKind>("ObjectKind"),
                    ObjectHasImplicitOperators = valueType.GetMetadata<bool>("ObjectHasImplicitOperators"),
                    ObjectHasIsValidMethod = valueType.GetMetadata<bool>("ObjectHasIsValidMethod"),
                    Item = valueType
                };

                // If no ObjectKind parameter was supplied, then default to struct
                if (context.ObjectKind == ObjectKind.None)
                {
                    context.ObjectKind = ObjectKind.Struct;
                }

                // Generate Code
                builder ??= new StringBuilder();
                GenerateValueType(builder, context);

                // Get and Create directory if not existing. 
                directoryInfo = new DirectoryInfo(Path.GetDirectoryName(context.FilePath)!);
                if (!directoryInfo.Exists)
                {
                    directoryInfo.Create();
                }

                // Create Code File
                using var stream = File.Open(context.FilePath!, FileMode.Create, FileAccess.ReadWrite);

                string content = builder.ToString();
                byte[] bytes = Encoding.UTF8.GetBytes(content);

                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();


                TaskItem taskItem = new TaskItem(context.Item?.ItemSpec);
                taskItem.SetMetadata("ObjectRuntimeType", context.ObjectRuntimeType.ToString());
                taskItem.SetMetadata("ObjectName", context.ObjectName);
                taskItem.SetMetadata("ObjectNamespace", context.ObjectNamespace);
                taskItem.SetMetadata("ObjectFilePath", context.FilePath);

                contexts.Add(context);
                created.Add(taskItem);

                // Clear out existing data
                builder.Clear();
            }

            // Clean up old files
            //RemoveOldGeneratedFiles(contexts);

            ValueTypesCreated = created.ToArray();
        }
        catch (Exception exception)
        {
            Log.LogError("An unexpected error occurred. Error Message: '{0}'.", exception.GetBaseException().Message);
            return false;
        }

        return true;
    }

    #region Models

    partial class ValueTypeContext
    {
        public string? FileName { get; set; }
        public string? FileDirectory { get; set; }
        public string? FilePath { get; set; }
        public required string ObjectName { get; set; }
        public required string ObjectNamespace { get; set; }
        public required ObjectRuntimeType ObjectRuntimeType { get; set; }
        public ObjectKind ObjectKind { get; set; }
        public ObjectAccessModifier ObjectAccessModifier { get; set; }
        public bool ObjectHasIsValidMethod { get; set; }
        public bool ObjectHasImplicitOperators { get; set; }
        public ITaskItem? Item { get; set; }
    }
    enum ObjectRuntimeType
    {
        Int,
        Short,
        Long,
        UInt,
        UShort,
        ULong,
        Double,
        Decimal,
        String,
        Guid,
        Ulid
    }
    enum ObjectKind
    {
        None,
        Struct,
        Class
    }
    enum ObjectAccessModifier
    {
        None,
        Public,
        Internal
    }



    #endregion

    private void RemoveOldGeneratedFiles(List<ValueTypeContext> contexts)
    {
        // Get the base Directory for all code generation output
        DirectoryInfo basePath = new DirectoryInfo(CodeGenOutputPath!);

        if (!basePath.Exists)
        {
            basePath.Create();
        }

        IEnumerable<FileInfo> files = basePath.EnumerateFiles("*.cs", new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        });

        foreach (var file in files)
        {
            // If there is no file matching the exiting Value Types items then remove.
            if (!contexts.Any(context => context.FilePath == file.FullName))
            {
                file.Delete();
            }
        }
    }

    private static void GenerateValueType(StringBuilder builder, ValueTypeContext context)
    {
        var modifier = context.ObjectAccessModifier == ObjectAccessModifier.None ? "partial " : $"{context.ObjectAccessModifier.ToString().ToLower()} partial";
        builder.AppendLine($$"""
		using System;

		#pragma warning disable CS8604, CS8625
		
		namespace {{context.ObjectNamespace}}
		{
		    [global::System.Text.Json.Serialization.JsonConverter(typeof({{context.ObjectName}}JsonConverter))]
		    [global::System.Diagnostics.DebuggerDisplay("{Value}")]
			{{modifier}} {{context.ObjectKind.ToString().ToLower()}} {{context.ObjectName}} :
		""");

        GenerateValueTypeInterfaces(builder, context);

        builder.AppendLine("    {");

        GenerateValueTypeConstructor(builder, context);
        GenerateValueTypeProperties(builder, context);
        GenerateValueTypeMethods(builder, context);
        GenerateValueTypeOverloads(builder, context);
        GenerateValueTypeOperators(builder, context);
        GenerateValueTypeJsonConverter(builder, context);

        builder.AppendLine("    }");
        builder.AppendLine("}");
    }

    private static void GenerateValueTypeJsonConverter(StringBuilder builder, ValueTypeContext context)
    {
        var readMethod = context.ObjectRuntimeType switch
        {
            ObjectRuntimeType.Int => "GetInt32()",
            ObjectRuntimeType.Short => "GetInt16()",
            ObjectRuntimeType.Long => "GetInt64()",
            ObjectRuntimeType.UInt => "GetUInt32()",
            ObjectRuntimeType.UShort => "GetUInt16()",
            ObjectRuntimeType.ULong => "GetUInt64()",
            ObjectRuntimeType.Double => "GetDouble()",
            ObjectRuntimeType.Decimal => "GetDecimal()",
            ObjectRuntimeType.String => "GetString()",
            ObjectRuntimeType.Guid => "GetGuid()",
            ObjectRuntimeType.Ulid => "GetString()"
        };
        var writeMethod = context.ObjectRuntimeType switch
        {
            ObjectRuntimeType.Int => "WriteNumberValue(value.Value)",
            ObjectRuntimeType.Short => "WriteNumberValue(value.Value)",
            ObjectRuntimeType.Long => "WriteNumberValue(value.Value)",
            ObjectRuntimeType.UInt => "WriteNumberValue(value.Value)",
            ObjectRuntimeType.UShort => "WriteNumberValue(value.Value)",
            ObjectRuntimeType.ULong => "WriteNumberValue(value.Value)",
            ObjectRuntimeType.Double => "WriteNumberValue(value.Value)",
            ObjectRuntimeType.Decimal => "WriteNumberValue(value.Value)",
            ObjectRuntimeType.String => "WriteStringValue(value.Value)",
            ObjectRuntimeType.Guid => "WriteStringValue(value.Value)",
            ObjectRuntimeType.Ulid => "WriteStringValue(value.Value.ToString())"
        };

        if (context.ObjectRuntimeType == ObjectRuntimeType.Ulid)
        {
            builder.AppendTabbedLine(2, $$"""
            partial class {{context.ObjectName}}JsonConverter : global::System.Text.Json.Serialization.JsonConverter<{{context.ObjectName}}>
            {
                public override {{context.ObjectName}} Read(
                    ref global::System.Text.Json.Utf8JsonReader reader, 
                    global::System.Type typeToConvert, 
                    global::System.Text.Json.JsonSerializerOptions options) => new {{context.ObjectName}}({{GetNormalizedName(context.ObjectRuntimeType)}}.Parse(reader.{{readMethod}}));
            
                public override void Write(
                    global::System.Text.Json.Utf8JsonWriter writer, 
                    {{context.ObjectName}} value, 
                    global::System.Text.Json.JsonSerializerOptions options)
                    => writer.{{writeMethod}};
            }
            """);
        }
        else
        {
            builder.AppendTabbedLine(2, $$"""
            partial class {{context.ObjectName}}JsonConverter : global::System.Text.Json.Serialization.JsonConverter<{{context.ObjectName}}>
            {
                public override {{context.ObjectName}} Read(
                    ref global::System.Text.Json.Utf8JsonReader reader, 
                    global::System.Type typeToConvert, 
                    global::System.Text.Json.JsonSerializerOptions options) => new {{context.ObjectName}}(reader.{{readMethod}});
            
                public override void Write(
                    global::System.Text.Json.Utf8JsonWriter writer, 
                    {{context.ObjectName}} value, 
                    global::System.Text.Json.JsonSerializerOptions options)
                    => writer.{{writeMethod}};
            }
            """);
        }

    }
    private static void GenerateValueTypeInterfaces(StringBuilder builder, ValueTypeContext context)
    {
        // ISpanParsable
        if (context.ObjectRuntimeType != ObjectRuntimeType.String)
        {
            builder.AppendTabbedLine(2, $$"""
			global::System.Numerics.IEqualityOperators<{{context.ObjectName}}, {{context.ObjectName}}, bool>,
			global::System.Numerics.IComparisonOperators<{{context.ObjectName}}, {{context.ObjectName}}, bool>,
			global::System.IComparable<{{context.ObjectName}}>,
			global::System.IEquatable<{{context.ObjectName}}>,
			global::System.IFormattable
			""");
        }
        else
        {
            builder.AppendTabbedLine(2, $$"""
            global::System.Numerics.IEqualityOperators<{{context.ObjectName}}, {{context.ObjectName}}, bool>,
            global::System.Numerics.IComparisonOperators<{{context.ObjectName}}, {{context.ObjectName}}, bool>,
            global::System.IComparable<{{context.ObjectName}}>,
            global::System.IEquatable<{{context.ObjectName}}>
            """);
        }
    }
    private static void GenerateValueTypeConstructor(StringBuilder builder, ValueTypeContext info)
    {
        builder.Append("        public ");
        builder.Append(info.ObjectName);
        builder.Append("(");
        builder.Append(GetNormalizedName(info.ObjectRuntimeType));
        builder.Append(" ");
        builder.AppendLine("value)");
        builder.AppendLine("        {");

        if (info.ObjectHasIsValidMethod)
        {
            builder.AppendLine("			if (!IsValid(value, out string message))");
            builder.AppendLine("			{");
            builder.AppendLine("				throw new ArgumentException(message);");
            builder.AppendLine("			}");
        }

        builder.AppendLine("			Value = value;");
        builder.AppendLine("        }");
        builder.AppendLine();
    }
    private static void GenerateValueTypeProperties(StringBuilder builder, ValueTypeContext context)
    {
        builder.AppendTabbedLine(2, $$"""
            public {{GetNormalizedName(context.ObjectRuntimeType)}} Value { get; }
            """);
    }
    private static void GenerateValueTypeOperators(StringBuilder builder, ValueTypeContext context)
    {
        var operators = new string[] { "==", "!=", ">", "<", ">=", "<=" };

        foreach (var op in operators)
        {
            var body = op switch
            {
                "==" => "a.Equals(b);",
                "!=" => "!a.Equals(b);",
                ">" => "a.CompareTo(b) > 0;",
                "<" => "a.CompareTo(b) < 0;",
                ">=" => "a.CompareTo(b) >= 0;",
                "<=" => "a.CompareTo(b) <= 0;",
            };

            builder.AppendTabbedLine(2, $$"""
                public static bool operator {{op}}({{context.ObjectName}} a, {{context.ObjectName}} b) => {{body}}
                """);
        }

        if (context.ObjectHasImplicitOperators)
        {
            builder.AppendTabbedLine(2, $$"""
                public static implicit operator {{GetNormalizedName(context.ObjectRuntimeType)}}({{context.ObjectName}} item) => item.Value;
                public static implicit operator {{context.ObjectName}}({{GetNormalizedName(context.ObjectRuntimeType)}} item) => new {{context.ObjectName}}(item);
                """);
        }
    }
    private static void GenerateValueTypeOverloads(StringBuilder builder, ValueTypeContext context)
    {
        builder.AppendTabbedLine(2, $$"""
		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => {{GetToString(context.ObjectRuntimeType)}}
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not {{context.ObjectName}} instance ? false : Equals(instance);
		""");

        string GetToString(ObjectRuntimeType? type) => type switch
        {
            ObjectRuntimeType.Guid => "Value.ToString();",
            ObjectRuntimeType.Ulid => "Value.ToString();",
            ObjectRuntimeType.String => "Value;",
            _ => "Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);"
        };

    }
    private static void GenerateValueTypeMethods(StringBuilder builder, ValueTypeContext context)
    {
        if (context.ObjectHasIsValidMethod)
        {
            builder.Append("		public partial bool IsValid(");
            builder.Append(GetNormalizedName(context.ObjectRuntimeType));
            builder.AppendLine(" value, out string message);");
        }

        // Write CompareTo
        builder.Append("		public int CompareTo(");
        builder.Append(context.ObjectName);
        builder.AppendLine(" other) => Value.CompareTo(other.Value);");

        // Write IEquality
        builder.AppendTabbedLine(2, $"public bool Equals({context.ObjectName} other) => Value.Equals(other.Value);");

        // Write IFormattable
        var toStringBody = context.ObjectRuntimeType switch
        {
            ObjectRuntimeType.String => "Value.ToString(formatProvider);",
            _ => " Value.ToString(format, formatProvider);"
        };

        builder.AppendTabbedLine(2, $$"""
            public string ToString(
                [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
                global::System.IFormatProvider? formatProvider) => {{toStringBody}}
            """);

        // IParsable<>
        if (context.ObjectRuntimeType != ObjectRuntimeType.String)
        {
            builder.AppendTabbedLine(2, $$"""
			public static {{context.ObjectName}} Parse(string value) =>  new {{context.ObjectName}}({{GetNormalizedName(context.ObjectRuntimeType)}}.Parse(value));
			public static {{context.ObjectName}} Parse(string value, global::System.IFormatProvider provider) => Parse(value.AsSpan());
			public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, global::System.IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {{context.ObjectName}} result) => TryParse(value.AsSpan(), provider, out result);
			public static {{context.ObjectName}} Parse(global::System.ReadOnlySpan<char> span) => Parse(span, null);
			public static {{context.ObjectName}} Parse(global::System.ReadOnlySpan<char> span, global::System.IFormatProvider provider) => new {{context.ObjectName}}({{GetNormalizedName(context.ObjectRuntimeType)}}.Parse(span, provider));
			public static bool TryParse(global::System.ReadOnlySpan<char> span, global::System.IFormatProvider provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {{context.ObjectName}} result)
			{
				result = default;
				
				if ({{GetNormalizedName(context.ObjectRuntimeType)}}.TryParse(span, provider, out {{GetNormalizedName(context.ObjectRuntimeType)}} value))
				{
					result = new {{context.ObjectName}}(value);
					return true;
				}
				
				return false;
			}
			""");
        }

        if (context.ObjectRuntimeType == ObjectRuntimeType.Guid)
        {
            // Generate a static NewId method for Guid types
            builder.AppendTabbedLine(2, $$"""
                public static {{context.ObjectName}}  New() => new {{context.ObjectName}} ({{GetNormalizedName(context.ObjectRuntimeType)}}.NewGuid());
                public static readonly {{context.ObjectName}}  Empty = new {{context.ObjectName}} ({{GetNormalizedName(context.ObjectRuntimeType)}}.Empty);
                """);

        }

        if (context.ObjectRuntimeType == ObjectRuntimeType.Ulid)
        {
            // Generate a static NewId method for Guid types
            builder.AppendTabbedLine(2, $$"""
                public static {{context.ObjectName}}  New() => new {{context.ObjectName}} ({{GetNormalizedName(context.ObjectRuntimeType)}}.NewUlid());
                public static readonly {{context.ObjectName}}  Empty = new {{context.ObjectName}} ({{GetNormalizedName(context.ObjectRuntimeType)}}.Empty);
                """);

        }

        builder.AppendLine();
    }
    private static string GetNormalizedName(ObjectRuntimeType? underlyingType)
    {
        return underlyingType!.Value switch
        {
            ObjectRuntimeType.Guid => "global::System.Guid",
            ObjectRuntimeType.Ulid => "global::System.Ulid",
            _ => underlyingType.Value.ToString().ToLower()
        };
    }
}


