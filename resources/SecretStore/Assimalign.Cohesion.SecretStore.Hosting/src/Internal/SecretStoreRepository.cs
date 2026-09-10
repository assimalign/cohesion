using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.SecretStore.Hosting;

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
        if (payload.Length < 1 + sizeof(int) || payload[0] != FormatVersion)
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
