using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LogSpace;

namespace Assimalign.Cohesion.LogSpace.Hosting;

internal sealed class LogSpaceApplicationBuilder : ILogSpaceApplicationBuilder
{
    internal LogSpaceApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public ILogSpaceApplication Build()
    {
        var options = new LogSpaceApplicationOptions();
        var context = new LogSpaceApplicationContext();

        return new LogSpaceApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
