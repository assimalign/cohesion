using System;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Cli;

internal static class Program
{
    private static async Task<int> Main(string[] args)
    {
        using var cancellation = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancellation.Cancel(); };
        // A redirect must never carry a bearer token or client secret to another endpoint.
        using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        var application = new CliApplication(new ProcessRunner(), http, Console.In, Console.Out,
            Console.Error, Directory.GetCurrentDirectory(),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        return await application.ExecuteAsync(args, cancellation.Token).ConfigureAwait(false);
    }
}
