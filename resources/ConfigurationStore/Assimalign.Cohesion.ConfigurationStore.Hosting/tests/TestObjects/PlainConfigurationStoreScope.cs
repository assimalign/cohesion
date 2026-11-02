using System;
using System.IO;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

internal sealed class PlainConfigurationStoreScope : IDisposable
{
    internal PlainConfigurationStoreScope()
    {
        DataPath = Path.Combine(
            Path.GetTempPath(),
            "cohesion-configuration-store-lifecycle-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataPath);
        Builder = ConfigurationStoreTestHost.CreateBuilder(ConfigurationStoreTestHost.GetEndpoint(), DataPath);
    }

    internal ConfigurationStoreApplicationBuilder Builder { get; }

    internal string DataPath { get; }

    public void Dispose()
    {
        Directory.Delete(DataPath, recursive: true);
    }
}
