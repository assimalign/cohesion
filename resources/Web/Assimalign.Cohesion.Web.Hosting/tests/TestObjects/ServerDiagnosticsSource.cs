using System.Diagnostics.Tracing;
using System.Linq;
using System.Net.Http;
using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Testing;

namespace Assimalign.Cohesion.Web.Hosting.Tests.TestObjects;

internal static class ServerDiagnosticsSource
{
    internal static async Task<EventSource> GetAsync()
    {
        // Initialize the public server through the real hosted path. Its process-wide source
        // remains available after this warmup application has released all of its resources.
        await using WebApplicationTestFactory warmup = new();
        using HttpClient client = warmup.CreateClient();
        return EventSource.GetSources().Single(source => source.Name == "Assimalign.Cohesion.Web.Server");
    }
}
