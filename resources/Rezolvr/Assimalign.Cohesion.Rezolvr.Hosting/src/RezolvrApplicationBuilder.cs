using System;

using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Rezolvr;

namespace Assimalign.Cohesion.Rezolvr.Hosting;

internal sealed class RezolvrApplicationBuilder : IRezolvrApplicationBuilder
{
    internal RezolvrApplicationBuilder(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
    }

    public IRezolvrApplication Build()
    {
        var options = new RezolvrApplicationOptions();
        var context = new RezolvrApplicationContext();

        return new RezolvrApplicationHost(options, context);
    }

    IHost IHostBuilder.Build() => Build();
}
