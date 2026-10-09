using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Sdk.Database.Tasks.Internal;

using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;

namespace Assimalign.Cohesion.Sdk.Database.Tasks;

/// <summary>
/// Statically compiles each database's retained C# schema declaration into the SQL schema
/// package's canonical semantic document and content hash: one artifact pair per declared database.
/// </summary>
public sealed class CompileDatabaseSchemaTask : DatabaseTask
{
    /// <summary>The file name suffix of a database's canonical schema document.</summary>
    internal const string SchemaFileSuffix = ".schema.json";

    /// <summary>The file name suffix of a database's schema hash sidecar.</summary>
    internal const string HashFileSuffix = ".schema.sha256";

    /// <summary>
    /// The consumer C# source files to analyze.
    /// </summary>
    [Required]
    public ITaskItem[] SourceFiles { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The compiler reference assemblies used to bind the schema DSL.</summary>
    public ITaskItem[] ReferencePaths { get; set; } = Array.Empty<ITaskItem>();

    /// <summary>The consumer project's preprocessor constants.</summary>
    public string? DefineConstants { get; set; }

    /// <summary>The consumer project's C# language version.</summary>
    public string? LanguageVersion { get; set; }

    /// <summary>The consumer assembly's simple name, used in portable CLR type identities.</summary>
    [Required]
    public string AssemblyName { get; set; } = string.Empty;

    /// <summary>
    /// The database model the project targets. Only <c>Sql</c> currently supplies a compiled-schema package.
    /// </summary>
    [Required]
    public string Model { get; set; } = string.Empty;

    /// <summary>
    /// The directory each declared database's artifacts are written to:
    /// <c>&lt;database&gt;.schema.json</c> and <c>&lt;database&gt;.schema.sha256</c>. The task owns the
    /// directory's artifacts and removes those of a database that is no longer declared.
    /// </summary>
    [Required]
    public string OutputDirectory { get; set; } = string.Empty;

    /// <summary>The consumer project directory used to resolve relative item paths.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>
    /// One item per declared database, named for it, with <c>Hash</c> (the lowercase SHA-256 of the
    /// canonical semantic document), <c>SchemaPath</c> and <c>HashPath</c> metadata.
    /// </summary>
    [Output]
    public ITaskItem[] Schemas { get; private set; } = Array.Empty<ITaskItem>();

    /// <inheritdoc />
    public override bool Execute()
    {
        if (!string.Equals(Model, "Sql", StringComparison.Ordinal))
        {
            Log.LogError(null, "COHDBSDK106", null, null, 0, 0, 0, 0,
                $"Database model '{Model}' has no model-specific compiled-schema package.");
            return false;
        }

        try
        {
            string projectDirectory = Path.GetFullPath(ProjectDirectory);
            string[] sourcePaths = ResolvePaths(SourceFiles, projectDirectory);
            string[] referencePaths = ResolvePaths(ReferencePaths, projectDirectory);
            var extractor = new CSharpSchemaExtractor(LogDiagnostic);
            IReadOnlyList<SchemaSourceModel>? sources = extractor.Extract(
                sourcePaths,
                referencePaths,
                AssemblyName,
                LanguageVersion,
                DefineConstants);
            if (sources is null || Log.HasLoggedErrors)
            {
                return false;
            }

            // Every declaration is compiled before any artifact is written, so a failure replaces none.
            var schemas = new List<SqlCompiledSchema>(sources.Count);
            foreach (SchemaSourceModel source in sources)
            {
                try
                {
                    schemas.Add(CompiledSchemaSourceWriter.Create(source, Model));
                }
                catch (SqlSchemaValidationException exception)
                {
                    foreach (SqlSchemaValidationError error in exception.Errors)
                    {
                        Log.LogError(
                            null,
                            "COHDBSDK106",
                            null,
                            null,
                            0,
                            0,
                            0,
                            0,
                            $"Database '{source.Name}': {error.Code}: {error.Declaration}: {error.Message}");
                    }
                }
                catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
                {
                    Log.LogError(null, "COHDBSDK106", null, null, 0, 0, 0, 0, $"Database '{source.Name}': {exception.Message}");
                }
            }

            if (Log.HasLoggedErrors)
            {
                return false;
            }

            string outputDirectory = ResolvePath(OutputDirectory, projectDirectory);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<ITaskItem>(schemas.Count);
            foreach (SqlCompiledSchema schema in schemas)
            {
                string schemaPath = Path.Combine(outputDirectory, schema.Name + SchemaFileSuffix);
                string hashPath = Path.Combine(outputDirectory, schema.Name + HashFileSuffix);
                WriteIfChanged(schemaPath, schema.CanonicalDocument);
                WriteIfChanged(hashPath, schema.Hash + "\n");
                written.Add(schemaPath);
                written.Add(hashPath);

                var item = new TaskItem(schema.Name);
                item.SetMetadata("Hash", schema.Hash);
                item.SetMetadata("SchemaPath", schemaPath);
                item.SetMetadata("HashPath", hashPath);
                items.Add(item);
                Log.LogMessage(
                    MessageImportance.High,
                    $"Compiled C# schema of database '{schema.Name}' for model '{Model}' to '{schemaPath}' ({schema.Hash}).");
            }

            RemoveUndeclared(outputDirectory, written);
            Schemas = items.ToArray();
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.LogError(null, "COHDBSDK100", null, null, 0, 0, 0, 0, exception.Message);
            return false;
        }
    }

    private static string[] ResolvePaths(IEnumerable<ITaskItem> items, string projectDirectory)
    {
        var paths = new List<string>();
        foreach (ITaskItem item in items)
        {
            string path = item.ItemSpec;
            paths.Add(Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectDirectory, path)));
        }
        return [.. paths];
    }

    private static string ResolvePath(string path, string projectDirectory)
        => Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectDirectory, path));

    private void LogDiagnostic(SchemaSourceDiagnostic diagnostic)
    {
        Log.LogError(
            null,
            diagnostic.Code,
            null,
            diagnostic.File,
            diagnostic.Line,
            diagnostic.Column,
            diagnostic.Line,
            diagnostic.Column,
            diagnostic.Message);
    }

    // A database renamed or removed from the C# leaves artifacts no build would refresh.
    private static void RemoveUndeclared(string outputDirectory, HashSet<string> written)
    {
        if (!Directory.Exists(outputDirectory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(outputDirectory))
        {
            string fullPath = Path.GetFullPath(path);
            if ((fullPath.EndsWith(SchemaFileSuffix, StringComparison.OrdinalIgnoreCase) ||
                 fullPath.EndsWith(HashFileSuffix, StringComparison.OrdinalIgnoreCase)) &&
                !written.Contains(fullPath))
            {
                File.Delete(fullPath);
            }
        }
    }

    private static void WriteIfChanged(string path, string content)
    {
        string fullPath = Path.GetFullPath(path);
        if (File.Exists(fullPath) && string.Equals(File.ReadAllText(fullPath, Encoding.UTF8), content, StringComparison.Ordinal))
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        string temporaryPath = fullPath + ".tmp";
        File.WriteAllText(temporaryPath, content, new UTF8Encoding(false));
        File.Move(temporaryPath, fullPath, overwrite: true);
    }
}
