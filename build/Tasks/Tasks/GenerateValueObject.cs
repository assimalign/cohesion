
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.IO;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Build.Tasks
{
    public class GenerateValueObject : Task
    {
        //[Required]
        //public string ValueObjectsOutputPath { get; set; }

        [Required]
        public ITaskItem[] ValueObjects { get; set; }

        [Output]
        public string FilePath { get; set; }

        public override bool Execute()
        {
            var contexts = new List<ValueTypeContext>();

            try
            {
                //if (string.IsNullOrEmpty(ValueObjectsOutputPath))
                //{
                //    Log.LogError("An output directory is needed to code generate value objects.");
                //    return false;
                //}

                //var directoryInfo = new DirectoryInfo(ValueObjectsOutputPath);
                //if (!directoryInfo.Exists)
                //{
                //    directoryInfo.Create();
                //}

                //foreach (var file in Directory.GetFiles(ValueObjectsOutputPath))
                //{
                //    File.Delete(file);
                //}

                foreach (var item in ValueObjects)
                {
                    string meta = string.Empty;
                    string path = item.ItemSpec;

                    if (!path.EndsWith(".generated.cs", StringComparison.OrdinalIgnoreCase))
                    {
                        path = path.Replace(".cs", ".generated.cs");
                    }

                    var context = new ValueTypeContext()
                    {
                        Path = path,
                    };

                    if (string.IsNullOrEmpty((meta = item.GetMetadata("Name"))))
                    {
                        Log.LogError("The Metadata attribute 'Name' is required for item: {0}", path);
                        return false;
                    }

                    context.Name = meta;
                    meta = null;

                    if (string.IsNullOrEmpty((meta = item.GetMetadata("Namespace"))))
                    {
                        Log.LogError("The Metadata attribute 'Namespace' is required for item: {0}", path);
                        return false;
                    }

                    context.Namespace = meta;
                    meta = null;

                    if (string.IsNullOrEmpty((meta = item.GetMetadata("RuntimeType"))))
                    {
                        Log.LogError("The Metadata attribute 'RuntimeType' is required for item: {0}", path);
                        return false;
                    }
                    else
                    {
                        context.RuntimeType = meta;
                        meta = null;
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

                    if (!string.IsNullOrEmpty((meta = item.GetMetadata("IsClass"))) &&
                       meta.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        context.IsClass = true;
                    }
                    if (!string.IsNullOrEmpty((meta = item.GetMetadata("IsStruct"))) &&
                       meta.Equals("true", StringComparison.OrdinalIgnoreCase))
                    {
                        context.IsStruct = true;
                    }

                    contexts.Add(context);
                }
            }
            catch (Exception exception)
            {
                return false;
            }


            foreach (var context in contexts)
            {
                var builder = new StringBuilder();

                GenerateValueType(builder, context);

                using var stream = File.Create(context.Path);

                var bytes = Encoding.UTF8.GetBytes(builder.ToString());

                stream.Write(bytes, 0, bytes.Length);

                FilePath = context.Path;
            }

            return true;
        }

        private bool TryGetRuntimeType(string value, out string runtimeType)
        {
            runtimeType = null;

            return false;
        }

        partial class ValueTypeContext
        {
            public string Path { get; set; }
            public string? Name { get; set; }
            public string? Namespace { get; set; }
            public string? RuntimeType { get; set; }
            public bool IsClass { get; set; }
            public bool IsStruct { get; set; }
            public bool IncludeIsValidMethod { get; set; }
            public bool IncludeImplicitOperators { get; set; }
        }
        private static void GenerateValueType(StringBuilder builder, ValueTypeContext context)
        {
            builder.AppendLine($$"""
                using System;

                namespace {{context.Namespace}}
                {
                    partial {{GetIdentifier()}} {{context.Name}} :
                """);

            GenerateValueTypeInterfaces(builder, context);

            builder.AppendLine("    {");

            GenerateValueTypeConstructor(builder, context);
            GenerateValueTypeProperties(builder, context);
            GenerateValueTypeMethods(builder, context);
            GenerateValueTypeOverloads(builder, context);
            GenerateValueTypeOperators(builder, context);

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
        private static void GenerateValueTypeInterfaces(StringBuilder builder, ValueTypeContext context)
        {
            builder.AppendTabbedLine(2, $$"""
            global::System.Numerics.IEqualityOperators<{{context.Name}}, {{context.Name}}, bool>
            ,global::System.Numerics.IComparisonOperators<{{context.Name}}, {{context.Name}}, bool>
            ,global::System.IComparable<{{context.Name}}>
            ,global::System.IEquatable<{{context.Name}}>
            """);

            // ISpanParsable
            if (!context.RuntimeType.Equals("string", StringComparison.InvariantCultureIgnoreCase))
            {
                builder.AppendTabbedLine(2, $$"""
                ,global::System.IFormattable
                """);
            }
        }
        private static void GenerateValueTypeConstructor(StringBuilder builder, ValueTypeContext info)
        {
            builder.Append("        public ");
            builder.Append(info.Name);
            builder.Append("(");
            builder.Append(GetNormalizedName(info.RuntimeType));
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
            public {{GetNormalizedName(context.RuntimeType)}} Value { get; }
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
                public static implicit operator {{GetNormalizedName(context.RuntimeType)}}({{context.Name}} item) => item.Value;
                public static implicit operator {{context.Name}}({{GetNormalizedName(context.RuntimeType)}} item) => new {{context.Name}}(item);
                """);
            }
        }
        private static void GenerateValueTypeOverloads(StringBuilder builder, ValueTypeContext context)
        {
            builder.AppendTabbedLine(2, $$"""
		public override int GetHashCode() => Value.GetHashCode();
		public override string ToString() => {{GetToString(context.RuntimeType)}}
		public override bool Equals(object? obj) => ReferenceEquals(null, obj) || obj is not {{context.Name}} instance ? false : Equals(instance);
		""");

            string GetToString(string? type)
            {
                if (type.Equals("guid", StringComparison.InvariantCultureIgnoreCase))
                {
                    return "Value.ToString();";
                }
                if (type.Equals("ulid", StringComparison.InvariantCultureIgnoreCase))
                {
                    return "Value.ToString();";
                }
                if (type.Equals("string", StringComparison.InvariantCultureIgnoreCase))
                {
                    return "Value;";
                }
                return "Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);";
            }
        }
        private static void GenerateValueTypeMethods(StringBuilder builder, ValueTypeContext context)
        {
            if (context.IncludeIsValidMethod)
            {
                builder.Append("		public partial bool IsValid(");
                builder.Append(GetNormalizedName(context.RuntimeType));
                builder.AppendLine(" value, out string message);");
            }

            // Write CompareTo
            builder.Append("		public int CompareTo(");
            builder.Append(context.Name);
            builder.AppendLine(" other) => Value.CompareTo(other.Value);");

            // Write IEquality
            builder.AppendTabbedLine(2, $"public bool Equals({context.Name} other) => Value.Equals(other.Value);");

            // Write IFormattable
            var toStringBody = "";


            if (context.RuntimeType.Equals("string", StringComparison.InvariantCultureIgnoreCase))
            {
                toStringBody = "Value.ToString(formatProvider);";
            }
            else
            {
                toStringBody = " Value.ToString(format, formatProvider);";
            }

            builder.AppendTabbedLine(2, $$"""
            public string ToString(
                [global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)] string? format,
                global::System.IFormatProvider? formatProvider) => {{toStringBody}}
            """);

            // IParsable<>
            if (!context.RuntimeType.Equals("string", StringComparison.InvariantCultureIgnoreCase))
            {
                builder.AppendTabbedLine(2, $$"""
			public static {{context.Name}} Parse(string value) => {{GetNormalizedName(context.RuntimeType)}}.Parse(value);
			public static {{context.Name}} Parse(string value, IFormatProvider? provider) => Parse(value.AsSpan());
			public static bool TryParse([global::System.Diagnostics.CodeAnalysis.NotNullWhen(true)] string? value, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {{context.Name}} result) => TryParse(value.AsSpan(), provider, out result);
			public static {{context.Name}} Parse(ReadOnlySpan<char> span) => Parse(span, null);
			public static {{context.Name}} Parse(ReadOnlySpan<char> span, IFormatProvider? provider) => new {{context.Name}}({{GetNormalizedName(context.RuntimeType)}}.Parse(span, provider));
			public static bool TryParse(ReadOnlySpan<char> span, IFormatProvider? provider, [global::System.Diagnostics.CodeAnalysis.MaybeNullWhen(false)] out {{context.Name}} result)
			{
				result = default;
				
				if ({{GetNormalizedName(context.RuntimeType)}}.TryParse(span, provider, out {{GetNormalizedName(context.RuntimeType)}} value))
				{
					result = new {{context.Name}}(value);
					return true;
				}
				
				return false;
			}
			""");
            }
            if (context.RuntimeType.Equals("ulid", StringComparison.InvariantCultureIgnoreCase))
            {
                builder.AppendTabbedLine(2, $$"""
                    public static {{context.Name}} New() => new {{context.Name}}({{GetNormalizedName(context.RuntimeType)}}.NewUlid());
                    """);
            }

            builder.AppendLine();
        }
        private static string GetNormalizedName(string? underlyingType)
        {
            if (underlyingType.Equals("guid", StringComparison.OrdinalIgnoreCase))
            {
                return "Guid";
            }
            if (underlyingType.Equals("ulid", StringComparison.OrdinalIgnoreCase))
            {
                return "Ulid";
            }

            return underlyingType.ToLower();
        }
    }
}


