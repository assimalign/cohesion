using System;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;

using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Hosting.Management;

namespace Assimalign.Cohesion.Web.Management.AotGuard;

internal static class Program
{
    public static async Task<int> Main()
    {
        // Exercises the trimmed paths a published resource runs: schema export from source-generated
        // metadata, record loading, RFC 7396 merging and binding through the section's JsonTypeInfo.
        CorsPolicyStore cors = CorsPolicyStore.Create(new CorsConfiguration
        {
            DefaultPolicy = new CorsPolicyConfiguration { Origins = ["https://app.example"] },
        });
        ResourceControlPlane plane = ResourceControlPlane.Create();
        plane.AddConfiguration(cors.AsResourceConfiguration());

        using (JsonDocument surface = JsonDocument.Parse(plane.Surface.Export()))
        {
            JsonElement section = surface.RootElement.GetProperty("configuration")[0];
            if (section.GetProperty("path").GetString() != "Web:Cors" ||
                !section.GetProperty("schema").GetProperty("properties").TryGetProperty("defaultPolicy", out _))
            {
                return 1;
            }
        }

        string directory = Path.Combine(Path.GetTempPath(), "cohesion-aot-guard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            File.WriteAllText(
                Path.Combine(directory, ResourceConfigurationRecord.OverrideLayerFileName),
                """{"Web:Cors":{"defaultPolicy":{"origins":["https://portal.example"]}}}""",
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            ResourceConfigurationStatus status = await plane.ApplyConfigurationAsync(
                ResourceConfigurationRecord.Load(directory),
                ResourceConfigurationPhase.Startup);
            if (status.Sections[0].State != ResourceConfigurationState.Applied)
            {
                return 2;
            }
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }

        if (cors.GetConfiguration().DefaultPolicy!.Origins[0] != "https://portal.example")
        {
            return 3;
        }

        Console.WriteLine("Runtime surface, deployment record and CORS section passed.");
        return 0;
    }
}
