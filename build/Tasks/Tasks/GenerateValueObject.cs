
using System;
using System.Collections.Generic;
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
            var contexts = new List<ValueObjectContext>();

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

                    var context = new ValueObjectContext()
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

                    contexts.Add(context);
                }
            }
            catch (Exception exception)
            {
                return false;
            }


            foreach (var context in contexts)
            {
                var code = GenerateCode(context);

                using var stream = File.Create(context.Path);

                var bytes = Encoding.UTF8.GetBytes(code);

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

        partial class ValueObjectContext
        {
            public string Path { get; set; }
            public string Name { get; set; }
            public string Namespace { get; set; }
            public string RuntimeType { get; set; }
            public bool IncludeIsValidMethod { get; set; }
            public bool IncludeImplicitOperators { get; set; }
        }


        private static string GenerateCode(ValueObjectContext info)
        {
            var builder = new StringBuilder();

            builder.AppendLine("using System;");
            builder.AppendLine();

            builder.Append("namespace ");
            builder.AppendLine(info.Namespace);
            builder.AppendLine("{");
            builder.Append("    partial struct ");
            builder.Append(info.Name);
            builder.AppendLine(" : ");

            WriteInterfaces(builder, info);

            builder.AppendLine("    {");

            WriteConstructor(builder, info);
            WriteProperties(builder, info);
            WriteMethods(builder, info);
            WriteOverloads(builder, info);
            WriteOperators(builder, info);

            builder.AppendLine("    }");
            builder.AppendLine("}");

            return builder.ToString();
        }
        private static void WriteInterfaces(StringBuilder builder, ValueObjectContext info)
        {
            builder.AppendLine("	#if NET7_0_OR_GREATER");

            // IEqualityOperators<,,>
            builder.Append("		global::System.Numerics.IEqualityOperators<");
            builder.Append(info.Name);
            builder.Append(", ");
            builder.Append(info.Name);
            builder.AppendLine(", bool>,");

            // IComparisonOperators<,,>
            builder.Append("		global::System.Numerics.IComparisonOperators<");
            builder.Append(info.Name);
            builder.Append(", ");
            builder.Append(info.Name);
            builder.AppendLine(", bool>,");

            builder.AppendLine("	#endif");

            // IComparable<>
            builder.Append("		global::System.IComparable<");
            builder.Append(info.Name);
            builder.AppendLine(">,");

            // IEquatable<>
            builder.Append("		global::System.IEquatable<");
            builder.Append(info.Name);
            builder.AppendLine(">,");

            // IFormattable
            builder.AppendLine("		global::System.IFormattable");
        }
        private static void WriteConstructor(StringBuilder builder, ValueObjectContext info)
        {
            builder.Append("        public ");
            builder.Append(info.Name);
            builder.Append("(");
            builder.Append(info.RuntimeType);
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
        private static void WriteProperties(StringBuilder builder, ValueObjectContext info)
        {
            builder.Append("		public ");
            builder.Append(info.RuntimeType);
            builder.AppendLine(" Value { get; }");
            builder.AppendLine();
        }
        private static void WriteOperators(StringBuilder builder, ValueObjectContext info)
        {
            var operators = new string[] { "==", "!=", ">", "<", ">=", "<=" };

            foreach (var op in operators)
            {
                builder.Append("		public static bool operator ");
                builder.Append(op);
                builder.Append("(");
                builder.Append(info.Name);
                builder.Append(" a, ");
                builder.Append(info.Name);
                builder.Append(" b) => ");
                builder.AppendLine(op switch
                {
                    "==" => "a.Equals(b);",
                    "!=" => "!a.Equals(b);",
                    ">" => "a.CompareTo(b) > 0;",
                    "<" => "a.CompareTo(b) < 0;",
                    ">=" => "a.CompareTo(b) >= 0;",
                    "<=" => "a.CompareTo(b) <= 0;",
                });
            }

            if (info.IncludeImplicitOperators)
            {
                builder.Append("		public static implicit operator ");
                builder.Append(info.RuntimeType);
                builder.Append("(");
                builder.Append(info.Name);
                builder.AppendLine(" item) => item.Value;");

                builder.Append("		public static implicit operator ");
                builder.Append(info.Name);
                builder.Append("(");
                builder.Append(info.RuntimeType);
                builder.Append(" item) => new ");
                builder.Append(info.Name);
                builder.AppendLine("(item);");
            }
        }
        private static void WriteOverloads(StringBuilder builder, ValueObjectContext info)
        {
            builder.AppendLine("		public override int GetHashCode() => Value.GetHashCode();");
            builder.AppendLine(info.RuntimeType switch
            {
                "int" => "		public override string ToString() => Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);",
                "long" => "		public override string ToString() => Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);",
                "short" => "		public override string ToString() => Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);",
                "uint" => "		public override string ToString() => Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);",
                "ulong" => "		public override string ToString() => Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);",
                "ushort" => "		public override string ToString() => Value.ToString(global::System.Globalization.CultureInfo.InvariantCulture);",
                "string" => "		public override string ToString() => Value;",
                "Guid" => "		public override string ToString() => Value.ToString();",
                "Ulid" => "		public override string ToString() => Value.ToString();"
            });
            builder.AppendLine("		public override bool Equals(object? obj)");
            builder.AppendLine("		{");
            builder.Append("			if (ReferenceEquals(null, obj) || obj is not ");
            builder.Append(info.Name);
            builder.AppendLine(" instance)");
            builder.AppendLine("			{");
            builder.AppendLine("				return false;");
            builder.AppendLine("			}");
            builder.AppendLine("			return Equals(instance);");
            builder.AppendLine("		}");
            builder.AppendLine();
        }
        private static void WriteMethods(StringBuilder builder, ValueObjectContext info)
        {
            if (info.IncludeIsValidMethod)
            {
                builder.Append("		public partial bool IsValid(");
                builder.Append(info.RuntimeType);
                builder.AppendLine(" value, out string message);");
            }

            // Write CompareTo
            builder.Append("		public int CompareTo(");
            builder.Append(info.Name);
            builder.AppendLine(" other) => Value.CompareTo(other.Value);");

            // Write IEquality
            builder.Append("		public bool Equals(");
            builder.Append(info.Name);
            builder.AppendLine(" other) => Value.Equals(other.Value);");

            // Write IFormattable
            builder.AppendLine("		public string ToString(");
            builder.AppendLine("			#if NET7_0_OR_GREATER");
            builder.AppendLine("			[global::System.Diagnostics.CodeAnalysis.StringSyntax(global::System.Diagnostics.CodeAnalysis.StringSyntaxAttribute.NumericFormat)]");
            builder.AppendLine("			#endif");
            builder.AppendLine("			string? format,");
            builder.AppendLine("			global::System.IFormatProvider? formatProvider)");

            if (info.RuntimeType.Equals("string"))
            {
                builder.AppendLine("			=> Value.ToString(formatProvider);");
            }
            else
            {
                builder.AppendLine("			=> Value.ToString(format, formatProvider);");
            }

            if (info.RuntimeType.Equals("Guid"))
            {
                builder.AppendLine($"       public static {info.Name} New{info.Name}() => new {info.Name}(Guid.NewGuid()); ");
            }
            if (info.RuntimeType.Equals("Ulid"))
            {
                builder.AppendLine($"       public static {info.Name} New{info.Name}() => new {info.Name}(Ulid.NewUlid()); ");
            }

            builder.AppendLine();
        }


    }
}


