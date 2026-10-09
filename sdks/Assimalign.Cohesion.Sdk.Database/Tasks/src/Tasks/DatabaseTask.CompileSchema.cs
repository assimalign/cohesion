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
    /// The file, in the output directory, that lists the databases whose artifacts the task wrote,
    /// one name per line. It is the only record of which files in the directory the task owns, the
    /// model compile target's incremental output, and the source of the
    /// <c>CohesionDatabaseSchema</c> items, so a foreign file that happens to end in
    /// <see cref="SchemaFileSuffix"/> is neither deleted nor read as a database.
    /// </summary>
    internal const string ManifestFileName = "schemas.manifest";

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
    /// <c>&lt;database&gt;.schema.json</c> and <c>&lt;database&gt;.schema.sha256</c>, listed in
    /// <see cref="ManifestFileName"/>. The task removes the artifacts of a database its previous
    /// manifest listed and that is no longer declared, and touches no other file.
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
            string[] previous = ReadManifest(outputDirectory);
            var written = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var items = new List<ITaskItem>(schemas.Count);
            foreach (SqlCompiledSchema schema in schemas)
            {
                string schemaPath = Path.Combine(outputDirectory, schema.Name + SchemaFileSuffix);
                string hashPath = Path.Combine(outputDirectory, schema.Name + HashFileSuffix);
                WriteIfChanged(schemaPath, schema.CanonicalDocument);
                WriteIfChanged(hashPath, schema.Hash + "\n");
                written.Add(schema.Name);

                var item = new TaskItem(schema.Name);
                item.SetMetadata("Hash", schema.Hash);
                item.SetMetadata("SchemaPath", schemaPath);
                item.SetMetadata("HashPath", hashPath);
                items.Add(item);
                Log.LogMessage(
                    MessageImportance.High,
                    $"Compiled C# schema of database '{schema.Name}' for model '{Model}' to '{schemaPath}' ({schema.Hash}).");
            }

            RemoveUndeclared(outputDirectory, previous, written);
            WriteManifest(outputDirectory, schemas);
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

    /// <summary>
    /// Reads the databases a previous run wrote artifacts for, from the output directory's
    /// manifest; none when the directory has no manifest.
    /// </summary>
    /// <param name="outputDirectory">The artifact directory.</param>
    /// <returns>The database names, in the order the manifest lists them.</returns>
    internal static string[] ReadManifest(string outputDirectory)
    {
        string manifestPath = Path.Combine(outputDirectory, ManifestFileName);
        if (!File.Exists(manifestPath))
        {
            return [];
        }

        var names = new List<string>();
        foreach (string line in File.ReadAllLines(manifestPath, Encoding.UTF8))
        {
            string name = line.Trim();
            if (name.Length > 0)
            {
                names.Add(name);
            }
        }

        return [.. names];
    }

    // A database renamed or removed from the C# leaves artifacts no build would refresh. Only the
    // artifacts the previous manifest lists are the task's to delete: the output directory is a
    // consumer override, and may hold other files.
    private static void RemoveUndeclared(string outputDirectory, string[] previous, HashSet<string> written)
    {
        foreach (string name in previous)
        {
            if (written.Contains(name) || name.IndexOfAny(['\\', '/']) >= 0 || name is "." or "..")
            {
                continue;
            }

            File.Delete(Path.Combine(outputDirectory, name + SchemaFileSuffix));
            File.Delete(Path.Combine(outputDirectory, name + HashFileSuffix));
        }
    }

    // Written on every successful run, changed or not: it is the compile target's incremental
    // output, so its time stamp records the last successful compile.
    private static void WriteManifest(string outputDirectory, IReadOnlyList<SqlCompiledSchema> schemas)
    {
        Directory.CreateDirectory(outputDirectory);
        var content = new StringBuilder();
        foreach (SqlCompiledSchema schema in schemas)
        {
            content.Append(schema.Name).Append('\n');
        }

        File.WriteAllText(Path.Combine(outputDirectory, ManifestFileName), content.ToString(), new UTF8Encoding(false));
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
