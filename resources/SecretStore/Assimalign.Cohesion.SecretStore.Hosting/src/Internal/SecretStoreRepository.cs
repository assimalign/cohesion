using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.SecretStore.Hosting.Internal;

internal sealed class SecretStoreRepository
{
    private const byte FormatVersion = 1;

    private readonly string _directoryPath;
    private readonly IDataProtector _protector;
    private readonly IReadOnlyDictionary<string, ReadOnlyMemory<byte>> _seeds;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private bool _initialized;

    internal SecretStoreRepository(
        string dataPath,
        IDataProtector protector,
        IReadOnlyDictionary<string, ReadOnlyMemory<byte>> seeds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataPath);
        ArgumentNullException.ThrowIfNull(protector);
        ArgumentNullException.ThrowIfNull(seeds);

        DataPath = Path.GetFullPath(dataPath);
        _directoryPath = Path.Combine(DataPath, "secrets");
        _protector = protector;
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

            Directory.CreateDirectory(_directoryPath);
            foreach ((string path, ReadOnlyMemory<byte> value) in _seeds)
            {
                string filePath = GetPath(path);
                if (!File.Exists(filePath))
                {
                    byte[] payload = Encode(path, value.Span);
                    try
                    {
                        await ProtectedFileStore.WriteAsync(
                                filePath,
                                _protector,
                                payload,
                                cancellationToken)
                            .ConfigureAwait(false);
                    }
                    finally
                    {
                        CryptographicOperations.ZeroMemory(payload);
                    }
                }
            }

            _initialized = true;
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<ReadOnlyMemory<byte>?> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            byte[]? payload = await ProtectedFileStore.ReadAsync(
                    GetPath(path),
                    _protector,
                    cancellationToken)
                .ConfigureAwait(false);
            if (payload is null)
            {
                return null;
            }

            try
            {
                return Decode(path, payload);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(payload);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private string GetPath(string secretPath)
    {
        byte[] encodedPath = Encoding.UTF8.GetBytes(secretPath);
        byte[] hash = SHA256.HashData(encodedPath);
        try
        {
            return Path.Combine(_directoryPath, Convert.ToHexStringLower(hash) + ".secret");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedPath);
            CryptographicOperations.ZeroMemory(hash);
        }
    }

    internal async Task StoreCommandSecretAsync(ResourceCommand command, string source, ReadOnlyMemory<byte> value,
        CancellationToken cancellationToken = default)
    {
        if (command.Key is "trusted-issuers.json" || command.Key.StartsWith("certs/", StringComparison.Ordinal))
        {
            throw new ResourceCommandRejectedException($"secretstore.add-secret path '{command.Key}' is reserved for the store protocol.");
        }
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            string path = GetPath(command.Key);
            byte[]? durable = await ProtectedFileStore.ReadAsync(path, _protector, cancellationToken).ConfigureAwait(false);
            try
            {
                if (durable is not null)
                {
                    SecretCommandDocument? previous = ReadCommandDocument(command.Key, durable);
                    try
                    {
                        if (previous is null || previous.Owner != command.Owner)
                        {
                            throw new ResourceCommandRejectedException($"secretstore.add-secret path '{command.Key}' belongs to '{previous?.Owner ?? "the resource"}'; owner '{command.Owner}' cannot overwrite it.");
                        }
                        if (previous.Source != source || !previous.Value.AsSpan().SequenceEqual(value.Span))
                        {
                            throw new ResourceCommandRejectedException($"secretstore.add-secret path '{command.Key}' conflicts with its stored declaration; delete it before changing its source or value.");
                        }
                    }
                    finally
                    {
                        if (previous is not null) { CryptographicOperations.ZeroMemory(previous.Value); }
                    }
                }
                byte[] valueCopy = value.ToArray();
                byte[] metadata;
                try
                {
                    metadata = JsonSerializer.SerializeToUtf8Bytes(
                        new SecretCommandDocument(command.Id, command.Owner, command.Key, source, valueCopy),
                        SecretRepositoryJsonContext.Default.SecretCommandDocument);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(valueCopy);
                }
                byte[] encoded = Encode(command.Key, metadata);
                encoded[0] = 2;
                try
                {
                    await ProtectedFileStore.WriteAsync(path, _protector, encoded, cancellationToken).ConfigureAwait(false);
                }
                finally
                {
                    CryptographicOperations.ZeroMemory(metadata);
                    CryptographicOperations.ZeroMemory(encoded);
                }
            }
            finally
            {
                if (durable is not null) { CryptographicOperations.ZeroMemory(durable); }
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task DeleteCommandSecretAsync(ResourceCommand command, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureInitialized();
            string path = GetPath(command.Key);
            byte[]? durable = await ProtectedFileStore.ReadAsync(path, _protector, cancellationToken).ConfigureAwait(false);
            if (durable is null) { return; }
            try
            {
                SecretCommandDocument? previous = ReadCommandDocument(command.Key, durable);
                if (previous is null || previous.Owner != command.Owner)
                {
                    throw new ResourceCommandRejectedException($"secretstore.add-secret owner '{command.Owner}' cannot delete path '{command.Key}' owned by '{previous?.Owner ?? "the resource"}'.");
                }
                CryptographicOperations.ZeroMemory(previous.Value);
                File.Delete(path);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(durable);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static SecretCommandDocument? ReadCommandDocument(string path, byte[] payload)
    {
        if (payload[0] != 2) { return null; }
        byte[] metadata = DecodeFrame(path, payload);
        try
        {
            return JsonSerializer.Deserialize(metadata, SecretRepositoryJsonContext.Default.SecretCommandDocument)
                ?? throw new InvalidDataException("A declared secret document cannot be null.");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(metadata);
        }
    }

    private static byte[] Encode(string path, ReadOnlySpan<byte> value)
    {
        byte[] encodedPath = Encoding.UTF8.GetBytes(path);
        try
        {
            byte[] payload = new byte[1 + sizeof(int) + encodedPath.Length + value.Length];
            payload[0] = FormatVersion;
            BinaryPrimitives.WriteInt32BigEndian(payload.AsSpan(1, sizeof(int)), encodedPath.Length);
            encodedPath.CopyTo(payload.AsSpan(1 + sizeof(int)));
            value.CopyTo(payload.AsSpan(1 + sizeof(int) + encodedPath.Length));
            return payload;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(encodedPath);
        }
    }

    private static byte[] Decode(string expectedPath, ReadOnlySpan<byte> payload)
    {
        byte[] value = DecodeFrame(expectedPath, payload);
        if (payload[0] != 2) { return value; }
        try
        {
            return (JsonSerializer.Deserialize(value, SecretRepositoryJsonContext.Default.SecretCommandDocument)
                ?? throw new InvalidDataException("A declared secret document cannot be null.")).Value;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }

    private static byte[] DecodeFrame(string expectedPath, ReadOnlySpan<byte> payload)
    {
        if (payload.Length < 1 + sizeof(int) || payload[0] is not (FormatVersion or 2))
        {
            throw new InvalidDataException("A protected secret document has an unsupported format.");
        }

        int pathLength = BinaryPrimitives.ReadInt32BigEndian(payload.Slice(1, sizeof(int)));
        if (pathLength <= 0 || pathLength > payload.Length - 1 - sizeof(int))
        {
            throw new InvalidDataException("A protected secret document has an invalid path length.");
        }

        string actualPath = Encoding.UTF8.GetString(payload.Slice(1 + sizeof(int), pathLength));
        if (!string.Equals(actualPath, expectedPath, StringComparison.Ordinal))
        {
            throw new InvalidDataException("A protected secret document does not match its requested path.");
        }

        return payload[(1 + sizeof(int) + pathLength)..].ToArray();
    }

    private void EnsureInitialized()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The secret store repository has not been initialized.");
        }
    }
}

internal sealed record SecretCommandDocument(string Id, string Owner, string Path, string Source, byte[] Value);

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(SecretCommandDocument))]
internal sealed partial class SecretRepositoryJsonContext : JsonSerializerContext;
