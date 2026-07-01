using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;
using Xunit;

using Assimalign.Cohesion.ApplicationModel;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.Tests;

public class LocalGatewayTests
{
    [Fact]
    public void Name_IsLocal()
    {
        new LocalGateway().Name.ShouldBe((ResourceName)"local");
    }

    [Fact]
    public void UseLocalGateway_SelectsLocalGatewayAndBuilds()
    {
        IApplicationBuilder builder = Application.CreateBuilder().UseLocalGateway();
        builder.AddResource(new TestExecutableResource("svc", "does-not-matter"));

        IApplication app = builder.Build();

        app.ShouldNotBeNull();
    }

    [Fact]
    public async Task RunAsync_WhenArtifactCannotBeResolved_Throws()
    {
        string emptyDirectory = Directory.CreateTempSubdirectory("cohesion-local-gateway-test").FullName;

        try
        {
            IApplicationBuilder builder = Application.CreateBuilder()
                .UseLocalGateway(options => options.BaseDirectory = emptyDirectory);
            builder.AddResource(new TestExecutableResource("svc", "Assimalign.Cohesion.NoSuchApp"));
            IApplication app = builder.Build();

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await Should.ThrowAsync<FileNotFoundException>(async () => await app.RunAsync(cancellation.Token));
        }
        finally
        {
            Directory.Delete(emptyDirectory, recursive: true);
        }
    }
}
