using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Xunit;

namespace Assimalign.Cohesion.Hosting.Tests;

public class HostBuilderTests
{
    public const string DisplayPrefix = $"Cohesion Test [Hosting] - HostBuilder: ";


    [Fact(DisplayName = DisplayPrefix + "Host already built exception.")]
    public void TestDuplicateBuildError()
    {
        var builder = HostBuilder.Create()
            .AddService(new TestService(c => Task.CompletedTask));

        builder.Build();
        Assert.Throws<InvalidOperationException>(() => builder.Build());
    }


    [Fact(DisplayName = DisplayPrefix + "Resolve constructor parameters")]
    public void TestServiceProviderCreate()
    {
        var builder = HostBuilder.Create();

        builder.AddServiceProvider(new TestServiceProvider());
        builder.AddService<TestService>();

        var host = builder.Build();
    }


    [Fact]
    public async Task Test1()
    {
        var timer = new System.Timers.Timer();
        using var host = HostBuilder.Create(options =>
        {
            options.ServiceStartupTimeout = TimeSpan.FromSeconds(2);
        })
            .AddService(context =>
            {
                return new TestService(async token =>
                {
                    while (true)
                    {
                        if (token.IsCancellationRequested)
                        {
                            break;
                        }
                    }
                });

            }).Build();


        Assert.Single(host.Context.HostedServices);



        host.Context.Shutdown();


        await host.RunAsync();
    }
}
