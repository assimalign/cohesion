using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.LogSpace;
using Assimalign.Cohesion.LogSpace.Hosting;

// Owns authenticated OTLP ingestion, durable retention, and the platform query surface.
LogSpaceApplicationBuilder builder = LogSpaceApplication.CreateBuilder(args);

await using LogSpaceApplication application = builder.Build();
await application.RunAsync();
