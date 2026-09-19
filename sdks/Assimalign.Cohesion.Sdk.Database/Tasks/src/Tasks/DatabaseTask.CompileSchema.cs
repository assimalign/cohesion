using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using Assimalign.Cohesion.Database.Sql.Schema;
using Assimalign.Cohesion.Sdk.Database.Tasks.Compilation;

using Microsoft.Build.Framework;

namespace Assimalign.Cohesion.Sdk.Database.Tasks;

/// <summary>
/// Statically compiles a database application's retained C# schema declaration
/// into the SQL schema package's canonical semantic document and content hash.
/// </summary>
public sealed class CompileDatabaseSchemaTask : DatabaseTask
{
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
    /// The path the schema model artifact is written to.
    /// </summary>
    [Required]
    public string OutputPath { get; set; } = string.Empty;

    /// <summary>The path the lowercase SHA-256 sidecar is written to.</summary>
    [Required]
    public string HashOutputPath { get; set; } = string.Empty;

    /// <summary>The consumer project directory used to resolve relative item paths.</summary>
    [Required]
    public string ProjectDirectory { get; set; } = string.Empty;

    /// <summary>The lowercase SHA-256 of the canonical semantic document.</summary>
    [Output]
    public string SchemaHash { get; private set; } = string.Empty;

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
            SchemaSourceModel? source = extractor.Extract(
                sourcePaths,
                referencePaths,
                AssemblyName,
                LanguageVersion,
                DefineConstants);
            if (source is null || Log.HasLoggedErrors)
            {
                return false;
            }

            SqlCompiledSchema schema;
            try
            {
                schema = CompiledSchemaSourceWriter.Create(source, Model);
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
                        $"{error.Code}: {error.Declaration}: {error.Message}");
                }
                return false;
            }
            catch (Exception exception) when (exception is ArgumentException or InvalidOperationException)
            {
                Log.LogError(null, "COHDBSDK106", null, null, 0, 0, 0, 0, exception.Message);
                return false;
            }

            string outputPath = ResolvePath(OutputPath, projectDirectory);
            string hashOutputPath = ResolvePath(HashOutputPath, projectDirectory);
            string document = SqlCompiledSchemaSerializer.Serialize(schema);
            SchemaHash = SqlCompiledSchemaSerializer.ComputeHash(schema);
            WriteIfChanged(outputPath, document);
            WriteIfChanged(hashOutputPath, SchemaHash + "\n");
            Log.LogMessage(
                MessageImportance.High,
                $"Compiled C# database schema '{schema.Name}' for model '{Model}' to '{outputPath}' ({SchemaHash}).");
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
