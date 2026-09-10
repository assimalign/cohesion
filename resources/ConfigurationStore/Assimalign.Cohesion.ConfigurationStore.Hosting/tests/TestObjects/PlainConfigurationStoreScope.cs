using System;
using System.IO;

using Assimalign.Cohesion.ConfigurationStore;
using Assimalign.Cohesion.Hosting.Resources;

namespace Assimalign.Cohesion.ConfigurationStore.Hosting.Tests;

internal sealed class PlainConfigurationStoreScope : IDisposable
{
    private readonly IDisposable _resourceScope;

    internal PlainConfigurationStoreScope()
    {
        DataPath = Path.Combine(
            Path.GetTempPath(),
            "cohesion-configuration-store-lifecycle-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataPath);
        Uri endpoint = ConfigurationStoreTestHost.GetEndpoint();
        _resourceScope = ResourceRuntime.CreateScope(new ResourceContext(
            environmentName: "test",
            contentRootPath: DataPath));
        Builder = ConfigurationStoreApplication.CreateBuilder(
            ["--endpoint", endpoint.AbsoluteUri, "--data", DataPath],
            typeof(ConfigurationStoreApplication).Assembly);
    }

    internal IConfigurationStoreApplicationBuilder Builder { get; }

    internal string DataPath { get; }

    public void Dispose()
    {
        _resourceScope.Dispose();
        Directory.Delete(DataPath, recursive: true);
    }
}
