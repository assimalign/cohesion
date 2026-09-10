using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting;

internal sealed class ConfigurationStoreRepository
{
    private readonly string _namespaceDirectory;
    private readonly IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> _seeds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, Dictionary<string, string?>> _values = new(StringComparer.Ordinal);
    private bool _initialized;

    internal ConfigurationStoreRepository(
        string dataPath,
        IReadOnlyDictionary<string, IReadOnlyDictionary<string, string?>> seeds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(seeds);

        DataPath = Path.GetFullPath(dataPath);
        _namespaceDirectory = Path.Combine(DataPath, "namespaces");
        _seeds = seeds;
    }

    internal string DataPath { get; }

    internal async Task InitializeAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (_initialized)
            {
                return;
            }

            Directory.CreateDirectory(_namespaceDirectory);
            string[] paths = Directory.GetFiles(_namespaceDirectory, "*.json", SearchOption.TopDirectoryOnly);
            Array.Sort(paths, StringComparer.Ordinal);
            for (int index = 0; index < paths.Length; index++)
            {
                (string name, Dictionary<string, string?> values) = await ReadFileAsync(
                        paths[index],
                        cancellationToken)
                    .ConfigureAwait(false);
                if (!_values.TryAdd(name, values))
                {
                    throw new InvalidDataException(
                        $"Configuration namespace '{name}' occurs in more than one durable document.");
                }
            }

            foreach ((string name, IReadOnlyDictionary<string, string?> values) in _seeds)
            {
                if (_values.ContainsKey(name))
                {
                    continue;
                }

                var seeded = new Dictionary<string, string?>(values, StringComparer.Ordinal);
                await WriteFileAsync(name, seeded, cancellationToken).ConfigureAwait(false);
                _values.Add(name, seeded);
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            string[] names = _values.Keys.OrderBy(static value => value, StringComparer.Ordinal).ToArray();
            return new ReadOnlyCollection<string>(names);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<IReadOnlyDictionary<string, string?>?> ReadAsync(
        string name,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (!_values.TryGetValue(name, out Dictionary<string, string?>? values))
            {
                return null;
            }

            return new ReadOnlyDictionary<string, string?>(
                new Dictionary<string, string?>(values, StringComparer.Ordinal));
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> SetAsync(
        string namespaceName,
        string key,
        string? value,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (!_values.TryGetValue(namespaceName, out Dictionary<string, string?>? values))
            {
                return false;
            }

            var updated = new Dictionary<string, string?>(values, StringComparer.Ordinal)
            {
                [key] = value,
            };
            await WriteFileAsync(namespaceName, updated, cancellationToken).ConfigureAwait(false);
            _values[namespaceName] = updated;
            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<bool> RemoveAsync(
        string namespaceName,
        string key,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(namespaceName);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            if (!_values.TryGetValue(namespaceName, out Dictionary<string, string?>? values))
            {
                return false;
            }

            if (values.ContainsKey(key))
            {
                var updated = new Dictionary<string, string?>(values, StringComparer.Ordinal);
                updated.Remove(key);
                await WriteFileAsync(namespaceName, updated, cancellationToken).ConfigureAwait(false);
                _values[namespaceName] = updated;
            }

            return true;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<(string Name, Dictionary<string, string?> Values)> ReadFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        byte[] content = await File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        try
        {
            using JsonDocument document = JsonDocument.Parse(content);
            JsonElement root = document.RootElement;
            if (root.ValueKind is not JsonValueKind.Object ||
                !root.TryGetProperty("name", out JsonElement nameProperty) ||
                nameProperty.ValueKind is not JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameProperty.GetString()) ||
                !root.TryGetProperty("values", out JsonElement valuesProperty) ||
                valuesProperty.ValueKind is not JsonValueKind.Object)
            {
                throw new InvalidDataException(
                    $"Configuration namespace document '{path}' must contain string 'name' and object 'values' members.");
            }

            var values = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (JsonProperty property in valuesProperty.EnumerateObject())
            {
                if (string.IsNullOrWhiteSpace(property.Name) ||
                    property.Value.ValueKind is not (JsonValueKind.String or JsonValueKind.Null) ||
                    !values.TryAdd(
                        property.Name,
                        property.Value.ValueKind is JsonValueKind.Null ? null : property.Value.GetString()))
                {
                    throw new InvalidDataException(
                        $"Configuration namespace document '{path}' contains an invalid or duplicate value.");
                }
            }

            return (nameProperty.GetString()!, values);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                $"Configuration namespace document '{path}' is not valid JSON.",
                exception);
        }
    }

    private async Task WriteFileAsync(
        string name,
        IReadOnlyDictionary<string, string?> values,
        CancellationToken cancellationToken)
    {
        string path = Path.Combine(_namespaceDirectory, GetFileName(name));
        string temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var stream = new FileStream(
                temporary,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous))
            {
                using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
                writer.WriteStartObject();
                writer.WriteString("name", name);
                writer.WritePropertyName("values");
                writer.WriteStartObject();
                foreach ((string key, string? value) in
                    values.OrderBy(static pair => pair.Key, StringComparer.Ordinal))
                {
                    if (value is null)
                    {
                        writer.WriteNull(key);
                    }
                    else
                    {
                        writer.WriteString(key, value);
                    }
                }

                writer.WriteEndObject();
                writer.WriteEndObject();
                await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
            {
                File.Delete(temporary);
            }
        }
    }

    private static string GetFileName(string name)
    {
        byte[] encoded = Encoding.UTF8.GetBytes(name);
        byte[] hash = SHA256.HashData(encoded);
        return Convert.ToHexStringLower(hash) + ".json";
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The configuration repository has not been initialized.");
        }
    }
}
