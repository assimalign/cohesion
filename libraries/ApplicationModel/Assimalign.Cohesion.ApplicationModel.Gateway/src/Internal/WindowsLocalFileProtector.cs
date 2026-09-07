using System;
using System.IO;
using System.Runtime.Versioning;
using System.Text;

using Assimalign.Cohesion.Security.DataProtection;

namespace Assimalign.Cohesion.ApplicationModel.Gateway;

[SupportedOSPlatform("windows")]
internal sealed class WindowsLocalFileProtector : ILocalFileProtector
{
    private readonly IDataProtector _protector;

    public WindowsLocalFileProtector(string applicationDirectory, ApplicationName application)
    {
        string keyRingDirectory = Path.Combine(applicationDirectory, ".keys");
        Directory.CreateDirectory(keyRingDirectory);

        byte[] entropy = Encoding.UTF8.GetBytes(application.ToString());
        IKeyRepository repository = new DpapiKeyRepository(
            KeyRepository.CreateFileSystem(keyRingDirectory),
            entropy);
        IDataProtectionProvider provider = DataProtectionProvider.Create(
            repository,
            options => options.ApplicationDiscriminator = application.ToString());
        _protector = provider.CreateProtector(
            "Assimalign.Cohesion.ApplicationModel.Gateway.LocalMount.v1");
    }

    public byte[] Protect(string resource, string mount, ReadOnlySpan<byte> plaintext)
    {
        IDataProtector resourceProtector = _protector.CreateProtector(resource);
        IDataProtector mountProtector = resourceProtector.CreateProtector(mount);
        return mountProtector.Protect(plaintext);
    }
}
