using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LogSpace;
using Assimalign.Cohesion.LogSpace.Hosting;

// Owns authenticated OTLP ingestion, durable retention, and the platform query surface.
ILogSpaceApplicationBuilder builder = LogSpaceApplication.CreateBuilder(args);

await builder.Build().RunAsync();
