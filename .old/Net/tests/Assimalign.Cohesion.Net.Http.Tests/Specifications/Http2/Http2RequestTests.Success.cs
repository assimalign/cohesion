using Assimalign.Cohesion.Net.Hosting;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Net.Http.Tests;

public partial class Http2RequestTests
{

    [Fact]
    public async Task InvokeAsync()
    {
        var serverTask = Task.Run(async () =>
        {
            await HostBuilder.Create()
                .AddHttpServer(server =>
                {
                    
                })
                .Build()
                .RunAsync();
        });
        var clientTask = Task.Run(async () =>
        {
            using var client = new HttpClient();
            using var request = new HttpRequestMessage()
            {
                Version = new Version("2.0.0"),
                VersionPolicy = HttpVersionPolicy.RequestVersionExact
            };
        });
    }
}
