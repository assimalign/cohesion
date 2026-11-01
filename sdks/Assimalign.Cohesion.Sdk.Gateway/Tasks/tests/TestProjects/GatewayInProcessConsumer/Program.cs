using System;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Assimalign.Cohesion.ApplicationModel;
using Assimalign.Cohesion.ApplicationModel.Gateway;
using Assimalign.Cohesion.ApplicationModel.Gateway.InProcess;

// The gateway runs the Sdk.Web resource it references in its own process. A bare UseInProcess()
// runs every referenced resource program, which the build bound from the CohesionResource metadata,
// and the gateway owns the infrastructure it builds: disposing the gateway stops the program.
ApplicationGatewayBuilder builder = ApplicationGateway.CreateBuilder("smoke").UseInProcess();
builder.StartupTimeout = TimeSpan.FromMinutes(2);
builder.AddResource("Web", "smoke-api");

await using ApplicationGateway gateway = builder.Build();
await gateway.StartAsync();

IApplicationGatewayDeployment resource = gateway.Resources.Single();
string endpoint = resource.Outputs[OutputName.Endpoint];
string answer = await GetAsync(new Uri(endpoint));
Console.WriteLine($"gateway={gateway.Name.Value};resource={resource.Name.Value};instances={resource.Instances.Count};answer={answer}");

await gateway.StopAsync();

// An instance publishes before its server listens, so the first request can arrive early.
static async Task<string> GetAsync(Uri address)
{
    using var client = new HttpClient { BaseAddress = address };
    for (int attempt = 0; ; attempt++)
    {
        try
        {
            return await client.GetStringAsync("/");
        }
        catch (HttpRequestException) when (attempt < 200)
        {
            await Task.Delay(50);
        }
    }
}
