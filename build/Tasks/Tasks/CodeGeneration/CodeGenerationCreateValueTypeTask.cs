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
        var contexts = new List<ValueTypeContext>();

        try
        {
            if (ValueTypes is null || ValueTypes.Length == 0)
            {
                Log.LogError("No ValueTypes were specified for code generation.");
                return false;
            }

            foreach (var item in ValueTypes)
            {
                string meta = string.Empty;

                Log.LogMessage(
                    MessageImportance.High,
                    "Generating Value Type Context for '{0}'.",
                    item.ItemSpec);

                // Set File System information
                var context = new ValueTypeContext()
                {
                    Name = Path.GetFileNameWithoutExtension(item.ItemSpec),
                    FileDirectory = Path.GetDirectoryName(item.ItemSpec)
                };

                context.FileName = context.FileName;

                if (string.IsNullOrEmpty(context.FileDirectory))
                {
                    context.FilePath = Path.Combine(CodeGenOutputPath!, $"{context.Name!}.cs");
                }
                else
                {
                    context.FilePath = Path.Combine(CodeGenOutputPath!, context.FileDirectory, $"{context.Name!}.cs");
                }

                // Set metadata information
                if (string.IsNullOrEmpty((meta = item.GetMetadata("ObjectNamespace"))))
                {
                    Log.LogError("The Metadata attribute 'Namespace' is required for item: {0}", item.ItemSpec);
                    return false;
                }

                context.Namespace = meta;
                meta = null!;

                if (!string.IsNullOrEmpty((meta = item.GetMetadata("ObjectAccessModifier"))))
                {
                    context.AccessModifier = meta;
                }

                meta = null!;

                if (string.IsNullOrEmpty((meta = item.GetMetadata("ObjectType"))) || !Enum.TryParse<UnderlyingType>(meta, out var runtimeType))
                {
                    Log.LogError("The Metadata attribute 'RuntimeType' is required for item: {0}", item.ItemSpec);
                    return false;
                }
                else
                {
                    context.Type = runtimeType;
                    meta = null!;
                }

                if (!string.IsNullOrEmpty((meta = item.GetMetadata("IncludeIsValidMethod"))) &&
                    meta.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    context.IncludeIsValidMethod = true;
                }

                if (!string.IsNullOrEmpty((meta = item.GetMetadata("IncludeIsValidMethod"))) &&
                    meta.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    context.IncludeIsValidMethod = true;
                }

                if (!string.IsNullOrEmpty((meta = item.GetMetadata("IncludeImplicitOperators"))) &&
                    meta.Equals("true", StringComparison.OrdinalIgnoreCase))
                {
                    context.IncludeImplicitOperators = true;
                }

                if (!string.IsNullOrEmpty((meta = item.GetMetadata("ObjectKind"))) &&
                   meta.Equals("class", StringComparison.OrdinalIgnoreCase))
                {
                    context.IsClass = true;
                    context.IsStruct = false;
                }

                context.Item = item;

                contexts.Add(context);
            }


            Log.LogMessage(MessageImportance.High, "Removing old code generated files.");
            RemoveOldGeneratedFiles(contexts);

            var created = new List<ITaskItem>();

            foreach (var context in contexts)
            {
                Log.LogMessage(MessageImportance.High,
                    "Starting Code Generation for Value Type: '{0}'.",
                    context.Name);

                var builder = new StringBuilder();

                GenerateValueType(builder, context);

                var directory = new DirectoryInfo(Path.GetDirectoryName(context.FilePath)!);

                Log.LogMessage(MessageImportance.High,
                    "Ensuring Directory Exists: '{0}'.",
                    directory.FullName);

                if (!directory.Exists)
                {
                    directory.Create();
                }


                Log.LogMessage(MessageImportance.High,
                    "Creating Code Generation File: '{0}'.",
                    context.FilePath);

                using var stream = File.Open(context.FilePath!, FileMode.Create, FileAccess.ReadWrite);

                byte[] bytes = Encoding.UTF8.GetBytes(builder.ToString());

                stream.Write(bytes, 0, bytes.Length);
                stream.Flush();

                var taskItem = new TaskItem(context.Item?.ItemSpec);
                taskItem.SetMetadata("ObjectType", context.Type.ToString());
                taskItem.SetMetadata("ObjectName", context.Name);
                taskItem.SetMetadata("ObjectNamespace", context.Namespace);
                taskItem.SetMetadata("ObjectFilePath", context.FilePath);
                created.Add(context!.Item!);
            }

            ValueTypesCreated = created.ToArray();
        }
        catch (Exception exception)
        {
            Log.LogError("An unexpected error occurred. Error Messafe: '{0}'.", exception.GetBaseException().Message);
            return false;
        }

        return true;
    }

    partial class ValueTypeContext
    {
        public string? FileName { get; set; }
        public string? FileDirectory { get; set; }
        public string? FilePath { get; set; }
        public string? Name { get; set; }
        public string? Namespace { get; set; }
        public bool IsClass { get; set; }
        public bool IsStruct { get; set; } = true;
        public bool IncludeIsValidMethod { get; set; }
        public bool IncludeImplicitOperators { get; set; }
        public string? AccessModifier { get; set; }
        public UnderlyingType? Type { get; set; }
        public ITaskItem? Item { get; set; }
    }
    enum UnderlyingType
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

    private void RemoveOldGeneratedFiles(List<ValueTypeContext> contexts)
    {
        // Get the base Directory for all code generation output
        DirectoryInfo basePath = new DirectoryInfo(CodeGenOutputPath!);


        IEnumerable<FileInfo> files = basePath.EnumerateFiles("*.cs", new EnumerationOptions()
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
        });


        foreach (var file in files)
        {
            // If there is no file matching the exiting Value Types items then remove.
            if (!contexts.Any(context => context.FilePath ==  file.FullName))
            {
                file.Delete();
            }
        }
    }

    private static void GenerateValueType(StringBuilder builder, ValueTypeContext context)
    {
        var modifier = string.IsNullOrEmpty(context.AccessModifier) ? "partial " : $"{context.AccessModifier} partial";
        builder.AppendLine($$"""
		using System;

		#pragma warning disable CS8604, CS8625
		
		namespace {{context.Namespace}}
		{
		    [global::System.Text.Json.Serialization.JsonConverter(typeof({{context.Name}}JsonConverter))]
			{{modifier}} {{GetIdentifier()}} {{context.Name}} :
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

        string GetIdentifier()
        {
            if (context.IsClass)
            {
                return "class";
            }
            if (context.IsStruct)
            {
                return "struct";
            }
            return string.Empty;
        }
    }

    private static void GenerateValueTypeJsonConverter(StringBuilder builder, ValueTypeContext context)
    {
        var readMethod = context.Type switch
        {
            UnderlyingType.Int => "GetInt32()",
            UnderlyingType.Short => "GetInt16()",
            UnderlyingType.Long => "GetInt64()",
            UnderlyingType.UInt => "GetUInt32()",
            UnderlyingType.UShort => "GetUInt16()",
            UnderlyingType.ULong => "GetUInt64()",
            UnderlyingType.Double => "GetDouble()",
            UnderlyingType.Decimal => "GetDecimal()",
            UnderlyingType.String => "GetString()",
            UnderlyingType.Guid => "GetGuid()",
            UnderlyingType.Ulid => "GetString()"
        };
        var writeMethod = context.Type switch
        {
            UnderlyingType.Int => "WriteNumberValue(value.Value)",
            UnderlyingType.Short => "WriteNumberValue(value.Value)",
            UnderlyingType.Long => "WriteNumberValue(value.Value)",
            UnderlyingType.UInt => "WriteNumberValue(value.Value)",
            UnderlyingType.UShort => "WriteNumberValue(value.Value)",
            UnderlyingType.ULong => "WriteNumberValue(value.Value)",
            UnderlyingType.Double => "WriteNumberValue(value.Value)",
            UnderlyingType.Decimal => "WriteNumberValue(value.Value)",
            UnderlyingType.String => "WriteStringValue(value.Value)",
            UnderlyingType.Guid => "WriteStringValue(value.Value)",
            UnderlyingType.Ulid => "WriteStringValue(value.Value.ToString())"
        };

        if (context.Type == UnderlyingType.Ulid)
        {
            builder.AppendTabbedLine(2, $$"""
            partial class {{context.Name}}JsonConverter : global::System.Text.Json.Serialization.JsonConverter<{{context.Name}}>
            {
                public override {{context.Name}} Read(
                    ref global::System.Text.Json.Utf8JsonReader reader, 
                    global::System.Type typeToConvert, 
                    global::System.Text.Json.JsonSerializerOptions options) => new {{context.Name}}({{GetNormalizedName(context.Type)}}.Parse(reader.{{readMethod}}));
            
                public override void Write(
                    global::System.Text.Json.Utf8JsonWriter writer, 
                    {{context.Name}} value, 
                    global::System.Text.Json.JsonSerializerOptions options)
                    => writer.{{writeMethod}};
            }
            """);
        }
        else
        {
            builder.AppendTabbedLine(2, $$"""
            partial class {{context.Name}}JsonConverter : global::System.Text.Json.Serialization.JsonConverter<{{context.Name}}>
            {
                public override {{context.Name}} Read(
                    ref global::System.Text.Json.Utf8JsonReader reader, 
                    global::System.Type typeToConvert, 
                    global::System.Text.Json.JsonSerializerOptions options) => new {{context.Name}}(reader.{{readMethod}});
            
                public override void Write(
                    global::System.Text.Json.Utf8JsonWriter writer, 
                    {{context.Name}} value, 
                    global::System.Text.Json.JsonSerializerOptions options)
                    => writer.{{writeMethod}};
            }
            """);
        }

    }
    private static void GenerateValueTypeInterfaces(StringBuilder builder, ValueTypeContext context)
    {
        // ISpanParsable
        if (context.Type != UnderlyingType.String)
        {
            builder.AppendTabbedLine(2, $$"""
			global::System.Numerics.IEqualityOperators<{{context.Name}}, {{context.Name}}, bool>,
			global::System.Numerics.IComparisonOperators<{{context.Name}}, {{context.Name}}, bool>,
			global::System.IComparable<{{context.Name}}>,
			global::System.IEquatable<{{context.Name}}>,
			global::System.IFormattable
			""");
        }
        else
        {
            builder.AppendTabbedLine(2, $$"""
            global::System.Numerics.IEqualityOperators<{{context.Name}}, {{context.Name}}, bool>,
            global::System.Numerics.IComparisonOperators<{{context.Name}}, {{context.Name}}, bool>,
            global::System.IComparable<{{context.Name}}>,
            global::System.IEquatable<{{context.Name}}>
            """);
        }
    }
    private static void GenerateValueTypeConstructor(StringBuilder builder, ValueTypeContext info)
    {
        builder.Append("        public ");
        builder.Append(info.Name);
        builder.Append("(");
        builder.Append(GetNormalizedName(info.Type));
        builder.Append(" ");
        builder.AppendLine("value)");
        builder.AppendLine("        {");

        if (info.IncludeIsValidMethod)
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
            public {{GetNormalizedName(context.Type)}} Value { get; }
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
                public static bool operator {{op}}({{context.Name}} a, {{context.Name}} b) => {{body}}
                """);
        }

        if (context.IncludeImplicitOperators)
        {
            builder.AppendTabbedLine(2, $$"""
                public static implicit operator {{GetNormalizedName(context.Type)}}({{context.Name}} item) => item.Value;
                public static implicit operator {{context.Name}}({{GetNormalizedName(context.Type)}} item) => new {{context.Name}}(item);
                """);
        }
    }
    private static void GenerateValueTypeOverloads(StringBuilder builder, ValueTypeContext context)
    {
        builder.AppendTabbedLine(2, $$"""
		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => {{GetToString(context.Type)}}
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not {{context.Name}} instance ? false : Equals(instance);
		""");

        string GetToString(UnderlyingType? type) => type switch
        {
            UnderlyingType.Guid => "Value.ToString();",
            UnderlyingType.Ulid => "Value.ToString();",
            UnderlyingType.String => "Value;",
            _ => "Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);"
        };

    }
    private static void GenerateValueTypeMethods(StringBuilder builder, ValueTypeContext context)
    {
        if (context.IncludeIsValidMethod)
        {
            builder.Append("		public partial bool IsValid(");
            builder.Append(GetNormalizedName(context.Type));
            builder.AppendLine(" value, out string message);");
        }

        // Write CompareTo
        builder.Append("		public int CompareTo(");
        builder.Append(context.Name);
        builder.AppendLine(" other) => Value.CompareTo(other.Value);");

        // Write IEquality
        builder.AppendTabbedLine(2, $"public bool Equals({context.Name} other) => Value.Equals(other.Value);");

        // Write IFormattable
        var toStringBody = context.Type switch
        {
            UnderlyingType.String => "Value.ToString(formatProvider);",
            _ => " Value.ToString(format, formatProvider);"
        };

        builder.AppendTabbedLine(2, $$"""
            public string ToString(
                [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
                global::System.IFormatProvider? formatProvider) => {{toStringBody}}
            """);

        // IParsable<>
        if (context.Type != UnderlyingType.String)
        {
            builder.AppendTabbedLine(2, $$"""
			public static {{context.Name}} Parse(string value) =>  new {{context.Name}}({{GetNormalizedName(context.Type)}}.Parse(value));
			public static {{context.Name}} Parse(string value, global::System.IFormatProvider provider) => Parse(value.AsSpan());
			public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, global::System.IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {{context.Name}} result) => TryParse(value.AsSpan(), provider, out result);
			public static {{context.Name}} Parse(global::System.ReadOnlySpan<char> span) => Parse(span, null);
			public static {{context.Name}} Parse(global::System.ReadOnlySpan<char> span, global::System.IFormatProvider provider) => new {{context.Name}}({{GetNormalizedName(context.Type)}}.Parse(span, provider));
			public static bool TryParse(global::System.ReadOnlySpan<char> span, global::System.IFormatProvider provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {{context.Name}} result)
			{
				result = default;
				
				if ({{GetNormalizedName(context.Type)}}.TryParse(span, provider, out {{GetNormalizedName(context.Type)}} value))
				{
					result = new {{context.Name}}(value);
					return true;
				}
				
				return false;
			}
			""");
        }

        if (context.Type == UnderlyingType.Guid)
        {
            // Generate a static NewId method for Guid types
            builder.AppendTabbedLine(2, $$"""
                public static {{context.Name}}  New() => new {{context.Name}} ({{GetNormalizedName(context.Type)}}.NewGuid());
                public static readonly {{context.Name}}  Empty = new {{context.Name}} ({{GetNormalizedName(context.Type)}}.Empty);
                """);

        }

        if (context.Type == UnderlyingType.Ulid)
        {
            // Generate a static NewId method for Guid types
            builder.AppendTabbedLine(2, $$"""
                public static {{context.Name}}  New() => new {{context.Name}} ({{GetNormalizedName(context.Type)}}.NewUlid());
                public static readonly {{context.Name}}  Empty = new {{context.Name}} ({{GetNormalizedName(context.Type)}}.Empty);
                """);

        }

        builder.AppendLine();
    }
    private static string GetNormalizedName(UnderlyingType? underlyingType)
    {
        return underlyingType!.Value switch
        {
            UnderlyingType.Guid => "global::System.Guid",
            UnderlyingType.Ulid => "global::System.Ulid",
            _ => underlyingType.Value.ToString().ToLower()
        };
    }
}


