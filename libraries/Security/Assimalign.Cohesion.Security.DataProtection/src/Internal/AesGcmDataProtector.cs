using System;
using System.Security.Cryptography;

namespace Assimalign.Cohesion.Security.DataProtection;

/// <summary>
/// The default <see cref="IDataProtector"/>: AES-256-GCM over a per-purpose subkey derived
/// with HKDF-SHA256 from the key ring's active key.
/// </summary>
/// <remarks>
/// <para>
/// Payload layout (all lengths fixed except the ciphertext):
/// <c>[version:1][keyId:16][nonce:12][ciphertext:n][tag:16]</c>. The version+keyId header is
/// passed as the GCM associated data, so neither the format version nor the key id can be
/// altered without failing authentication. The key id lets the ring select the producing key
/// on unprotect, which is what makes rotation transparent to callers.
/// </para>
/// <para>
/// A fresh random 96-bit nonce is drawn per <see cref="Protect(ReadOnlySpan{byte})"/>. Because
/// the subkey is unique per purpose chain and per ring key, the nonce space is scoped to a
/// single (key, purpose) pair, keeping the random-nonce collision bound comfortable for token
/// workloads. Derived subkeys are zeroed immediately after use.
/// </para>
/// </remarks>
internal sealed class AesGcmDataProtector : IDataProtector
{
    private const byte Version = 0x01;
    private const int VersionSize = 1;
    private const int KeyIdSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int SubkeySize = 32;
    private const int HeaderSize = VersionSize + KeyIdSize;
    private const int Overhead = HeaderSize + NonceSize + TagSize;

    private readonly KeyRing _ring;
    private readonly string[] _purposes;
    private readonly byte[] _info;

    public AesGcmDataProtector(KeyRing ring, string[] purposes)
    {
        _ring = ring;
        _purposes = purposes;
        _info = PurposeChain.BuildInfo(purposes);
    }

    /// <inheritdoc />
    public IDataProtector CreateProtector(string purpose)
    {
        ArgumentNullException.ThrowIfNull(purpose);

        string[] next = new string[_purposes.Length + 1];
        Array.Copy(_purposes, next, _purposes.Length);
        next[^1] = purpose;
        return new AesGcmDataProtector(_ring, next);
    }

    /// <inheritdoc />
    public byte[] Protect(ReadOnlySpan<byte> plaintext)
    {
        ManagedKey key = _ring.GetActiveKey();

        byte[] result = new byte[Overhead + plaintext.Length];
        result[0] = Version;
        key.KeyId.TryWriteBytes(result.AsSpan(VersionSize, KeyIdSize));

        Span<byte> nonce = result.AsSpan(HeaderSize, NonceSize);
        RandomNumberGenerator.Fill(nonce);

        Span<byte> ciphertext = result.AsSpan(HeaderSize + NonceSize, plaintext.Length);
        Span<byte> tag = result.AsSpan(result.Length - TagSize, TagSize);
        ReadOnlySpan<byte> associatedData = result.AsSpan(0, HeaderSize);

        Span<byte> subkey = stackalloc byte[SubkeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Master, subkey, salt: default, info: _info);
        try
        {
            using AesGcm aes = new(subkey, TagSize);
            aes.Encrypt(nonce, plaintext, ciphertext, tag, associatedData);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }

        return result;
    }

    /// <inheritdoc />
    public byte[] Unprotect(ReadOnlySpan<byte> protectedData)
    {
        if (protectedData.Length < Overhead || protectedData[0] != Version)
        {
            throw new DataProtectionException("The protected payload is malformed.");
        }

        Guid keyId = new(protectedData.Slice(VersionSize, KeyIdSize));
        ManagedKey key = _ring.ResolveForUnprotect(keyId);

        ReadOnlySpan<byte> associatedData = protectedData.Slice(0, HeaderSize);
        ReadOnlySpan<byte> nonce = protectedData.Slice(HeaderSize, NonceSize);
        int ciphertextLength = protectedData.Length - Overhead;
        ReadOnlySpan<byte> ciphertext = protectedData.Slice(HeaderSize + NonceSize, ciphertextLength);
        ReadOnlySpan<byte> tag = protectedData.Slice(protectedData.Length - TagSize, TagSize);

        byte[] plaintext = new byte[ciphertextLength];
        Span<byte> subkey = stackalloc byte[SubkeySize];
        HKDF.DeriveKey(HashAlgorithmName.SHA256, key.Master, subkey, salt: default, info: _info);
        try
        {
            using AesGcm aes = new(subkey, TagSize);
            aes.Decrypt(nonce, ciphertext, tag, plaintext, associatedData);
        }
        catch (CryptographicException ex)
        {
            // Authentication failure on untrusted input — surface as the area-scoped type
            // without leaking which byte failed.
            throw new DataProtectionException("The protected payload could not be verified.", ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(subkey);
        }

        return plaintext;
    }
}
