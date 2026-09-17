using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Database;

/// <summary>Reads and writes the canonical compiled-schema document.</summary>
public static class CompiledSchemaSerializer
{
    /// <summary>Serializes a compiled schema into its canonical UTF-8 JSON form.</summary>
    /// <param name="schema">The schema to serialize.</param>
    /// <returns>The canonical document.</returns>
    /// <exception cref="ArgumentNullException"><paramref name="schema"/> is null.</exception>
    /// <exception cref="DatabaseSchemaValidationException">
    /// <paramref name="schema"/> does not satisfy the compiled-schema contract.
    /// </exception>
    public static string Serialize(CompiledSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        CompiledSchemaValidator.Validate(schema);
        return JsonSerializer.Serialize(schema, CompiledSchemaJsonContext.Default.CompiledSchema);
    }

    /// <summary>Deserializes a canonical compiled-schema document.</summary>
    /// <param name="document">The canonical document.</param>
    /// <returns>The compiled schema.</returns>
    /// <exception cref="DatabaseSchemaValidationException">
    /// The JSON is malformed, contains unknown members, or does not satisfy the compiled-schema contract.
    /// </exception>
    public static CompiledSchema Deserialize(string document)
    {
        if (string.IsNullOrWhiteSpace(document))
        {
            throw CompiledSchemaValidator.InvalidDocument(
                "schema",
                "The compiled schema document cannot be empty.");
        }

        try
        {
            CompiledSchema schema = JsonSerializer.Deserialize(
                document,
                CompiledSchemaJsonContext.Default.CompiledSchema)
                ?? throw CompiledSchemaValidator.InvalidDocument(
                    "schema",
                    "The compiled schema document was empty.");
            CompiledSchemaValidator.Validate(schema);
            return schema;
        }
        catch (DatabaseSchemaValidationException)
        {
            throw;
        }
        catch (Exception exception) when (
            exception is JsonException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            string declaration = exception is JsonException { Path.Length: > 0 } jsonException
                ? $"schema{jsonException.Path[1..]}"
                : "schema";
            throw CompiledSchemaValidator.InvalidDocument(
                declaration,
                "The compiled schema document is malformed.",
                exception);
        }
    }

    /// <summary>Writes a compiled schema to a file.</summary>
    /// <param name="path">The destination path.</param>
    /// <param name="schema">The schema to write.</param>
    public static void Write(string path, CompiledSchema schema)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        File.WriteAllText(fullPath, Serialize(schema), new UTF8Encoding(false));
    }

    /// <summary>Reads a compiled schema from a file.</summary>
    /// <param name="path">The source path.</param>
    /// <returns>The compiled schema.</returns>
    public static CompiledSchema Read(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        return Deserialize(File.ReadAllText(path, Encoding.UTF8));
    }

    /// <summary>Writes a compiled schema to a file asynchronously.</summary>
    /// <param name="path">The destination path.</param>
    /// <param name="schema">The schema to write.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>A task representing the write.</returns>
    public static async ValueTask WriteAsync(
        string path,
        CompiledSchema schema,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(schema);
        string fullPath = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
        await File.WriteAllTextAsync(
            fullPath,
            Serialize(schema),
            new UTF8Encoding(false),
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Reads a compiled schema from a file asynchronously.</summary>
    /// <param name="path">The source path.</param>
    /// <param name="cancellationToken">Cancellation token for the operation.</param>
    /// <returns>The compiled schema.</returns>
    public static async ValueTask<CompiledSchema> ReadAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        string document = await File.ReadAllTextAsync(path, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        return Deserialize(document);
    }

    /// <summary>Computes the deterministic lowercase SHA-256 content hash.</summary>
    /// <param name="schema">The schema whose canonical document is hashed.</param>
    /// <returns>A 64-character lowercase hexadecimal hash.</returns>
    public static string ComputeHash(CompiledSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        byte[] bytes = Encoding.UTF8.GetBytes(Serialize(schema));
        return Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

}

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    WriteIndented = false,
    UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow)]
[JsonSerializable(typeof(CompiledSchema))]
internal sealed partial class CompiledSchemaJsonContext : JsonSerializerContext;
