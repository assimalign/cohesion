using System;
using System.IO;
using System.Text;

using Shouldly;
using Xunit;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public sealed class WindowsLocalMountCarrierTests
{
    [Fact(DisplayName = "Cohesion Test [ApplicationModel Gateway] - Windows mount carrier round-trips through ResourceMount")]
    public void WindowsMountCarrier_RoundTripsThroughResourceMount()
    {
        if (!OperatingSystem.IsWindows())
        {
            return;
        }

        string directory = Path.Combine(Path.GetTempPath(), $"cohesion-mount-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);

        try
        {
            byte[] plaintext = Encoding.UTF8.GetBytes("mount-secret");
            var protector = new WindowsLocalFileProtector(directory, new ApplicationName("test-app"));
            byte[] protectedBytes = protector.Protect("database", "credentials", plaintext);
            string path = Path.Combine(directory, "credentials.mount");
            File.WriteAllBytes(path, protectedBytes);

            new Assimalign.Cohesion.Hosting.Resources.ResourceMount(path).ReadAllBytes().ShouldBe(plaintext);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
