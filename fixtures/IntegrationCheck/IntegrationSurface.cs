// This project makes sink-signature drift fail here instead of in consumer projects.
// Every side of every declared component-integration pair must be referenced here.

using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Antiforgery;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.OpenApi;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Validation;

namespace Assimalign.Cohesion.IntegrationCheck;

internal static class IntegrationSurface
{
    internal static void Configure(IServiceProviderBuilder builder)
    {
        builder.AddHttpClientFactory(clients => clients.AddClient("canary", options => { }));
        builder.AddHttpClientFactory((services, clients) => clients.AddClient("provider-canary", options => { }));
    }

    // Owner decision 34 (#1380): every builder.Services.Add<Feature> verb and overload the Web feature
    // packages declare, each registering an IHttpFeature singleton. Decision 39 (#1395): every
    // builder-template verb takes an optional callback, so each is called both bare and with one.
    // Provider callbacks (2026-10-22) also compile for every builder integration.
    // AddJsonSerialization is the one static factory left: its resolver is a required argument.
    internal static void ConfigureWeb(
        IServiceProviderBuilder builder,
        IDataProtectionProvider dataProtection,
        IJsonTypeInfoResolver resolver)
    {
        builder
            .AddAntiforgery()
            .AddAntiforgery(antiforgery => antiforgery.Options.HeaderName = "X-Canary-Token")
            .AddAntiforgery(antiforgery => antiforgery.UseDataProtection(dataProtection))
            .AddAntiforgery((services, antiforgery) => antiforgery.UseDataProtection(
                services.GetRequiredService<IDataProtectionProvider>()))
            .AddAuthentication()
            .AddAuthentication(authentication => authentication
                .UseDataProtection(dataProtection)
                .AddScheme(new AuthenticationScheme("canary", displayName: null, () => null!)))
            .AddAuthentication((services, authentication) => authentication.UseDataProtection(
                services.GetRequiredService<IDataProtectionProvider>()))
            .AddAuthorization()
            .AddAuthorization(authorization => authorization.Options.FallbackPolicy = null)
            .AddAuthorization((services, authorization) => authorization.Options.FallbackPolicy = null)
            .AddErrorHandling()
            .AddErrorHandling(errors => errors.OnError(
                (context, exception, cancellationToken) => ValueTask.FromResult(false)))
            .AddErrorHandling((services, errors) => errors.OnError(
                (context, exception, cancellationToken) => ValueTask.FromResult(false)))
            .AddOpenApi()
            .AddOpenApi(openApi => openApi.Options.Title = "Canary")
            .AddOpenApi((services, openApi) => openApi.Options.Title = "Provider canary")
            .AddRouting()
            .AddRouting(routing => { })
            .AddRouting((services, routing) => { })
            .AddContentSerialization()
            .AddContentSerialization(serialization => serialization.AddJson(resolver))
            .AddContentSerialization((services, serialization) => serialization.AddJson(resolver))
            .AddJsonSerialization(resolver)
            .AddJsonSerialization(resolver, json => json.WriteIndented = true)
            .AddValidation()
            .AddValidation(validation => validation.Options.Enabled = false)
            .AddValidation((services, validation) => validation.Options.Enabled = false);
    }
}
