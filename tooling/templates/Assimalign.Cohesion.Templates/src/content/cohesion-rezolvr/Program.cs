using Assimalign.Cohesion.Hosting;
using Assimalign.Cohesion.Rezolvr;
using Assimalign.Cohesion.Rezolvr.Hosting;

IRezolvrApplicationBuilder builder = RezolvrApplication.CreateBuilder(args);

await builder.Build().RunAsync();
