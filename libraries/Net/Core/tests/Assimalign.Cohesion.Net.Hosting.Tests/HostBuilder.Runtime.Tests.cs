using System;

namespace Assimalign.Cohesion.Net.Hosting.Tests;

public partial class HostBuilderTests
{
    [Fact]
    public async void Test1()
    {
        await HostBuilder.Create()
            .AddServer(new TestServer())
            .AddServerStateCallback(async state =>
            {
                if (state is TestServerState test)
                {
                    Console.WriteLine(test.Status);
                }
            })
            .Build()
            .RunAsync();
    }
}