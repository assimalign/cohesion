using System.Threading.Tasks;

using Assimalign.Cohesion.Web.Hosting;

namespace EnabledWebExplicitMain.Entry;

internal static class Startup
{
    private static async Task Main(string[] args)
    {
        WebApplication application = WebApplication.CreateBuilder(args).Build();
        await application.RunAsync();
    }
}
