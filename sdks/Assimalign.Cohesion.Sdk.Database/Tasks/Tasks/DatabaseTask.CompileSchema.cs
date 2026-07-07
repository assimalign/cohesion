using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;

using Microsoft.Build.Framework;

namespace Assimalign.Cohesion.Sdk.Database.Tasks;

/// <summary>
/// Compiles a database project's declarative schema sources into a schema model
/// artifact: a JSON manifest listing every schema source with a content hash.
/// The migration tooling diffs schema model artifacts to generate migrations.
/// </summary>
public sealed class CompileDatabaseSchemaTask : DatabaseTask
{
    /// <summary>
    /// The declarative schema source files to compile.
    /// </summary>
    [Required]
    public ITaskItem[] SchemaFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>
    /// The database model the project targets (Sql, Documents, Graph, ...).
    /// </summary>
    [Required]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The path the schema model artifact is written to.
    /// </summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <inheritdoc />
    public override bool Execute()
    {
        // Scaffold scope: model the sources (path + content hash) so migration
        // diffing has a stable input. Statement-level schema parsing/validation
        // is the L03.02.06 tooling work; it slots in here.
        var builder = new StringBuilder();
        builder.AppendLine("{");
        builder.AppendLine($"  \"model\": \"{Model}\",");
        builder.AppendLine("  \"sources\": [");

        for (var i = 0; i < SchemaFiles.Length; i++)
        {
            var path = SchemaFiles[i].ItemSpec;
            if (!File.Exists(path))
            {
                Log.LogError($"Database schema source '{path}' does not exist.");
                continue;
            }
            string hash;
            using (var sha = SHA256.Create())
            using (var stream = File.OpenRead(path))
            {
                hash = Convert.ToBase64String(sha.ComputeHash(stream));
            }
            var separator = i < SchemaFiles.Length - 1 ? "," : string.Empty;
            builder.AppendLine($"    {{ \"path\": \"{path.Replace("\\", "\\\\")}\", \"sha256\": \"{hash}\" }}{separator}");
        }

        builder.AppendLine("  ]");
        builder.AppendLine("}");

        if (Log.HasLoggedErrors)
        {
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(OutputPath))!);
        File.WriteAllText(OutputPath, builder.ToString());
        Log.LogMessage(MessageImportance.Normal, $"Compiled {SchemaFiles.Length} schema source(s) into '{OutputPath}'.");
        return true;
    }
}
