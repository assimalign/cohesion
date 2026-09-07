using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.ApiManager;

namespace Assimalign.Cohesion.ApiManager.Hosting;

internal sealed class ApiManagerApplicationBuilder : IApiManagerApplicationBuilder
{
    internal ApiManagerApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IApiManagerApplication Build()
    {
        var options = new ApiManagerApplicationOptions();
        var context = new ApiManagerApplicationContext();

        return new ApiManagerApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
