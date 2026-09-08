using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

/// <summary>
/// Persists the gateway's ECDSA trust key beneath an application-scoped state directory.
/// </summary>
internal sealed class GatewayTrustKeyStore : IGatewayTrustKeyRepository
{
    private const string PrivateKeyFileName = "private-key.p8.protected";
    private const string ProtectorPurpose =
        "Assimalign.Cohesion.ApplicationModel.Gateway.TrustKey.v1";
    private const string DpapiEntropyPurpose =
        "Assimalign.Cohesion.ApplicationModel.Gateway.TrustKey.KeyRing.v1";

    // Trust-key ciphertext is durable state. A century-long protecting-key window and an
    // equally long unprotect grace period prevent the transient-payload defaults from aging
    // it out; explicit trust-key rotation remains independent of this ring lifetime.
    private static readonly TimeSpan DurableKeyLifetime = TimeSpan.FromDays(36500);
    private static readonly TimeSpan DurableUnprotectGracePeriod = TimeSpan.FromDays(36500);

    private const UnixFileMode PrivateDirectoryMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
    private const UnixFileMode PrivateFileMode =
        UnixFileMode.UserRead | UnixFileMode.UserWrite;

    private readonly string _stateRoot;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public GatewayTrustKeyStore(string stateRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateRoot);
        _stateRoot = Path.GetFullPath(stateRoot);
    }

    public async Task<ECDsa> LoadOrCreateAsync(
        ApplicationName application,
        ResourceName gateway,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string gatewayName = ValidateGatewayName(gateway);
            StorePaths paths = PreparePaths(application, gatewayName);
            IDataProtector protector = CreateProtector(
                application.ToString(),
                gatewayName,
                paths.KeyRingDirectory);

            if (File.Exists(paths.PrivateKeyPath))
            {
                return await LoadAsync(paths, protector, cancellationToken).ConfigureAwait(false);
            }

            ECDsa? generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                await PersistAsync(
                    paths,
                    protector,
                    generated,
                    overwrite: false,
                    cancellationToken).ConfigureAwait(false);

                ECDsa result = generated;
                generated = null;
                return result;
            }
            catch (IOException) when (File.Exists(paths.PrivateKeyPath))
            {
                // A different store instance won the atomic create. Load that complete key;
                // an unreadable winner remains an error and is never silently replaced.
                generated!.Dispose();
                generated = null;
                return await LoadAsync(paths, protector, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                generated?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ECDsa> RotateAsync(
        ApplicationName application,
        ResourceName gateway,
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            string gatewayName = ValidateGatewayName(gateway);
            StorePaths paths = PreparePaths(application, gatewayName);
            IDataProtector protector = CreateProtector(
                application.ToString(),
                gatewayName,
                paths.KeyRingDirectory);

            ECDsa? generated = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            try
            {
                await PersistAsync(
                    paths,
                    protector,
                    generated,
                    overwrite: true,
                    cancellationToken).ConfigureAwait(false);

                ECDsa result = generated;
                generated = null;
                return result;
            }
            finally
            {
                generated?.Dispose();
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private static async Task<ECDsa> LoadAsync(
        StorePaths paths,
        IDataProtector protector,
        CancellationToken cancellationToken)
    {
        byte[] protectedPkcs8 = await File.ReadAllBytesAsync(
            paths.PrivateKeyPath,
            cancellationToken).ConfigureAwait(false);
        byte[]? pkcs8 = null;
        ECDsa? privateKey = null;
        try
        {
            if (protectedPkcs8.Length == 0)
            {
                throw new InvalidDataException(
                    $"Gateway trust key '{paths.PrivateKeyPath}' is empty.");
            }

            pkcs8 = protector.Unprotect(protectedPkcs8);
            privateKey = ECDsa.Create();
            try
            {
                privateKey.ImportPkcs8PrivateKey(pkcs8, out int bytesRead);
                if (bytesRead != pkcs8.Length)
                {
                    throw new InvalidDataException(
                        $"Gateway trust key '{paths.PrivateKeyPath}' contains trailing data.");
                }

                ECDsa result = privateKey;
                privateKey = null;
                return result;
            }
            catch (CryptographicException exception)
            {
                throw new InvalidDataException(
                    $"Gateway trust key '{paths.PrivateKeyPath}' is not a valid PKCS#8 ECDSA private key.",
                    exception);
            }
        }
        finally
        {
            privateKey?.Dispose();
            CryptographicOperations.ZeroMemory(protectedPkcs8);
            if (pkcs8 is not null)
            {
                CryptographicOperations.ZeroMemory(pkcs8);
            }
        }
    }

    private static async Task PersistAsync(
        StorePaths paths,
        IDataProtector protector,
        ECDsa privateKey,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        byte[] pkcs8 = privateKey.ExportPkcs8PrivateKey();
        byte[]? protectedPkcs8 = null;
        try
        {
            protectedPkcs8 = protector.Protect(pkcs8);
            SetPrivateFileModes(paths.KeyRingDirectory);
            await WriteAtomicAsync(
                paths.PrivateKeyPath,
                protectedPkcs8,
                overwrite,
                cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(pkcs8);
            if (protectedPkcs8 is not null)
            {
                CryptographicOperations.ZeroMemory(protectedPkcs8);
            }
        }
    }

    private static IDataProtector CreateProtector(
        string applicationName,
        string gatewayName,
        string keyRingDirectory)
    {
        IKeyRepository repository = KeyRepository.CreateFileSystem(keyRingDirectory);
        if (OperatingSystem.IsWindows())
        {
            byte[] entropy = Encoding.UTF8.GetBytes(
                string.Concat(DpapiEntropyPurpose, "\0", applicationName, "\0", gatewayName));
            try
            {
                repository = new DpapiKeyRepository(repository, entropy);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(entropy);
            }
        }

        IDataProtectionProvider provider = DataProtectionProvider.Create(repository, options =>
        {
            options.ApplicationDiscriminator = string.Concat(ProtectorPurpose, "\0", applicationName);
            options.KeyLifetime = DurableKeyLifetime;
            options.UnprotectGracePeriod = DurableUnprotectGracePeriod;
        });

        return provider
            .CreateProtector(ProtectorPurpose)
            .CreateProtector(gatewayName);
    }

    private StorePaths PreparePaths(ApplicationName application, string gatewayName)
    {
        string applicationName = application.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(applicationName);

        CreatePrivateDirectory(_stateRoot);
        string applicationDirectory = SafeChild(_stateRoot, applicationName, "application");
        string trustDirectory = Path.Combine(applicationDirectory, "trust");
        string gatewayDirectory = SafeChild(trustDirectory, gatewayName, "gateway");
        string keyRingDirectory = Path.Combine(gatewayDirectory, "keyring");
        string privateKeyPath = Path.Combine(gatewayDirectory, PrivateKeyFileName);

        CreatePrivateDirectory(applicationDirectory);
        CreatePrivateDirectory(trustDirectory);
        CreatePrivateDirectory(gatewayDirectory);
        CreatePrivateDirectory(keyRingDirectory);
        SetPrivateFileModes(keyRingDirectory);
        SetPrivateFileMode(privateKeyPath);

        return new StorePaths(
            keyRingDirectory,
            privateKeyPath);
    }

    private static string ValidateGatewayName(ResourceName gateway)
    {
        string gatewayName = gateway.ToString();
        ArgumentException.ThrowIfNullOrWhiteSpace(gatewayName);
        return gatewayName;
    }

    private static string SafeChild(string parent, string name, string kind)
    {
        string parentPath = Path.GetFullPath(parent);
        string childPath = Path.GetFullPath(Path.Combine(parentPath, name));
        string prefix = parentPath.EndsWith(Path.DirectorySeparatorChar)
            ? parentPath
            : parentPath + Path.DirectorySeparatorChar;
        StringComparison comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!childPath.StartsWith(prefix, comparison))
        {
            throw new InvalidDataException(
                $"The {kind} name '{name}' cannot be used as a gateway trust-key path.");
        }

        return childPath;
    }

    private static void CreatePrivateDirectory(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            Directory.CreateDirectory(path);
            return;
        }

        Directory.CreateDirectory(path, PrivateDirectoryMode);
        File.SetUnixFileMode(path, PrivateDirectoryMode);
    }

    private static void SetPrivateFileModes(string directory)
    {
        if (OperatingSystem.IsWindows() || !Directory.Exists(directory))
        {
            return;
        }

        foreach (string path in Directory.EnumerateFiles(directory))
        {
            try
            {
                File.SetUnixFileMode(path, PrivateFileMode);
            }
            catch (FileNotFoundException)
            {
                // A concurrent atomic move can retire a temporary file between enumeration
                // and chmod; the resulting final file is handled by the writing instance.
            }
        }
    }

    private static void SetPrivateFileMode(string path)
    {
        if (!OperatingSystem.IsWindows() && File.Exists(path))
        {
            File.SetUnixFileMode(path, PrivateFileMode);
        }
    }

    private static async Task WriteAtomicAsync(
        string path,
        ReadOnlyMemory<byte> content,
        bool overwrite,
        CancellationToken cancellationToken)
    {
        string temporaryPath = string.Concat(path, ".", Guid.NewGuid().ToString("N"), ".tmp");
        try
        {
            var options = new FileStreamOptions
            {
                Mode = FileMode.CreateNew,
                Access = FileAccess.Write,
                Share = FileShare.None,
                Options = FileOptions.Asynchronous | FileOptions.SequentialScan,
            };

            if (!OperatingSystem.IsWindows())
            {
                options.UnixCreateMode = PrivateFileMode;
            }

            await using (var stream = new FileStream(temporaryPath, options))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            File.Move(temporaryPath, path, overwrite);
            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(path, PrivateFileMode);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private readonly record struct StorePaths(
        string KeyRingDirectory,
        string PrivateKeyPath);
}
