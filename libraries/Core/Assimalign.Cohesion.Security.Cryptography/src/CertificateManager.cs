using System;
using System.Linq;
using System.Security.Cryptography.X509Certificates;

namespace Assimalign.Cohesion.Net.Cryptography;

using Assimalign.Cohesion.Net.Cryptography.Internal;

public sealed class CertificateManager : ICertificateManager
{
    private readonly CertificateManagerOptions options;

    public CertificateManager(CertificateManagerOptions options)
    {
        this.options = options;
    }

    public ICertificateProvider GetMachineCertificateProvider(string storeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsCertificateProvider(storeName, StoreLocation.LocalMachine);
        }
        if (OperatingSystem.IsLinux())
        {
            return GetUnixCertificateProvider(storeName, StoreLocation.LocalMachine);
        }
        if (OperatingSystem.IsMacOS())
        {
            return GetMacOsCertificateProvider(storeName, StoreLocation.LocalMachine);
        }

        throw new PlatformNotSupportedException();
    }

    public ICertificateProvider GetUserCertificateProvider(string storeName)
    {
        if (OperatingSystem.IsWindows())
        {
            return GetWindowsCertificateProvider(storeName, StoreLocation.CurrentUser);
        }
        if (OperatingSystem.IsLinux())
        {
            return GetUnixCertificateProvider(storeName, StoreLocation.CurrentUser);
        }
        if (OperatingSystem.IsMacOS())
        {
            return GetMacOsCertificateProvider(storeName, StoreLocation.CurrentUser);
        }

        throw new PlatformNotSupportedException();
    }


    private ICertificateProvider GetWindowsCertificateProvider(string storeName, StoreLocation storeLocation)
    {
        return new WindowsCertificateProvider(Enum.TryParse<StoreName>(storeName, true, out var name) ? name.ToString() : storeName, storeLocation);
    }
    private ICertificateProvider GetUnixCertificateProvider(string storeName, StoreLocation storeLocation)
    {
        return new UnixCertificateProvider(Enum.TryParse<StoreName>(storeName, true, out var name) ? name.ToString() : storeName, storeLocation);
    }
    private ICertificateProvider GetMacOsCertificateProvider(string storeName, StoreLocation storeLocation)
    {
        return new MacOsCertificateProvider(Enum.TryParse<StoreName>(storeName, true, out var name) ? name.ToString() : storeName, storeLocation);
    }

    public static ICertificateManager Create(Action<CertificateManagerOptions> configure)
    {
        var options = new CertificateManagerOptions();

        configure.Invoke(options);

        return new CertificateManager(options);
    }
}
