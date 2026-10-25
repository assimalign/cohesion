using System;
using System.IO;
using System.Text.Json.Serialization;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Hosting;

using DescribedWeb;

// A gateway-composed resource reads its references while it composes. The describe run supplies a
// placeholder nothing listens at; the test reads the address back from this file.
Uri catalog = Resource.References.InventoryExplicitMain.Http.Url;
File.WriteAllText(Path.Combine(AppContext.BaseDirectory, "described-reference.txt"), $"{catalog.Scheme}://{catalog.Authority}");

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<ResourceConfigurationSection>(ResourceConfigurationSection.Create(
    "Described:Limits",
    DescribedJsonContext.Default.DescribedLimits,
    ResourceConfigurationMutability.Live,
    new DescribedLimits(),
    _ => null));
await builder.Build().RunAsync();

internal sealed class DescribedLimits
{
    public int MaxItems { get; set; } = 10;
}

[JsonSerializable(typeof(DescribedLimits))]
internal sealed partial class DescribedJsonContext : JsonSerializerContext
{
}
