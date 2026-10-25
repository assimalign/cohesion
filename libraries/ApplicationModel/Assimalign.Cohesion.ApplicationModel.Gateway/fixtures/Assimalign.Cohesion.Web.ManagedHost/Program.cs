using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Hosting.Resources;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Web;
using Assimalign.Cohesion.Web.Hosting;

namespace Assimalign.Cohesion.Web.ManagedHost;

/// <summary>Real Web.Hosting executable whose runtime surface a gateway manages in acceptance tests.</summary>
/// <remarks>
/// The surface has a live section (<c>Fixture:Greeting</c>), a restart section (<c>Fixture:Pool</c>), a claimable
/// backend section (<c>Fixture:Schema</c>) and an every-instance action (<c>echo</c>). <c>GET /greeting</c> returns the
/// greeting the live section last applied, so a test observes a management change end to end.
/// </remarks>
public static class Program
{
    /// <summary>Runs the resource until its gateway stops it.</summary>
    /// <param name="args">Application arguments.</param>
    /// <returns>The complete host lifetime.</returns>
    public static async Task Main(string[] args)
    {
        var greeting = new GreetingState();
        WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
        builder.Services.AddSingleton(ResourceConfigurationSection.Create(
            "Fixture:Greeting",
            ManagedHostJsonContext.Default.GreetingOptions,
            ResourceConfigurationMutability.Live,
            new GreetingOptions(),
            options =>
            {
                greeting.Current = options;
                return null;
            }));
        builder.Services.AddSingleton(ResourceConfigurationSection.Create(
            "Fixture:Pool",
            ManagedHostJsonContext.Default.PoolOptions,
            ResourceConfigurationMutability.Restart,
            new PoolOptions(),
            static _ => null));
        builder.Services.AddSingleton(ResourceConfigurationSection.Create(
            "Fixture:Schema",
            ManagedHostJsonContext.Default.DictionarySchemaEntry,
            ResourceConfigurationMutability.Live,
            new Dictionary<string, SchemaEntry>(),
            static _ => null,
            ResourceConfigurationScope.Backend,
            claimable: true));
        builder.Services.AddSingleton(ResourceAction.Create(
            "echo",
            ManagedHostJsonContext.Default.EchoInput,
            ManagedHostJsonContext.Default.EchoResult,
            ResourceActionTarget.EveryInstance,
            static (input, _) => ValueTask.FromResult(new EchoResult { Text = input.Text })));

        await using WebApplication application = builder.Build();
        IWebApplicationPipelineBuilder pipeline = application;
        pipeline.Use(next => context => context.Request.Path.Value == "/greeting"
            ? WriteGreetingAsync(context, greeting)
            : next.Invoke(context));
        await application.RunAsync().ConfigureAwait(false);
    }

    [ModuleInitializer]
    internal static void Register()
    {
        ResourceRuntime.RegisterEntry(typeof(Program).Assembly);
        ResourceRuntime.RegisterControlPlane(typeof(Program).Assembly, static () => ResourceControlPlane.Create());
    }

    private static async Task WriteGreetingAsync(IHttpContext context, GreetingState greeting)
    {
        context.Response.StatusCode = HttpStatusCode.Ok;
        context.Response.Headers[HttpHeaderKey.ContentType] = "application/json";
        await context.Response.Body.WriteAsync(
            JsonSerializer.SerializeToUtf8Bytes(greeting.Current, ManagedHostJsonContext.Default.GreetingOptions),
            context.RequestCancelled).ConfigureAwait(false);
    }

    private sealed class GreetingState
    {
        private GreetingOptions _current = new();

        public GreetingOptions Current
        {
            get => Volatile.Read(ref _current);
            set => Volatile.Write(ref _current, value);
        }
    }
}

/// <summary>The live section's options.</summary>
public sealed class GreetingOptions
{
    /// <summary>Gets or sets the greeting.</summary>
    public string Message { get; set; } = "baseline";

    /// <summary>Gets or sets how often the greeting repeats.</summary>
    public int Repeat { get; set; } = 1;
}

/// <summary>The restart section's options.</summary>
public sealed class PoolOptions
{
    /// <summary>Gets or sets the pool size.</summary>
    public int Size { get; set; } = 4;
}

/// <summary>One entry of the claimable backend section.</summary>
public sealed class SchemaEntry
{
    /// <summary>Gets or sets the column count.</summary>
    public int Columns { get; set; }
}

/// <summary>The echo action's input.</summary>
public sealed class EchoInput
{
    /// <summary>Gets or sets the text to echo.</summary>
    public string Text { get; set; } = string.Empty;
}

/// <summary>The echo action's result.</summary>
public sealed class EchoResult
{
    /// <summary>Gets or sets the echoed text.</summary>
    public string Text { get; set; } = string.Empty;
}

[JsonSourceGenerationOptions(PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase)]
[JsonSerializable(typeof(GreetingOptions))]
[JsonSerializable(typeof(PoolOptions))]
[JsonSerializable(typeof(Dictionary<string, SchemaEntry>), TypeInfoPropertyName = "DictionarySchemaEntry")]
[JsonSerializable(typeof(EchoInput))]
[JsonSerializable(typeof(EchoResult))]
internal sealed partial class ManagedHostJsonContext : JsonSerializerContext
{
}
