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
    }

    // Owner decision 34 (#1380): every builder.Services.Add<Feature> verb and overload the Web feature
    // packages declare, each registering an IHttpFeature singleton.
    internal static void ConfigureWeb(
        IServiceProviderBuilder builder,
        IDataProtectionProvider dataProtection,
        IJsonTypeInfoResolver resolver)
    {
        builder
            .AddAntiforgery()
            .AddAntiforgery(antiforgery => antiforgery.HeaderName = "X-Canary-Token")
            .AddAntiforgery(dataProtection)
            .AddAntiforgery(dataProtection, antiforgery => antiforgery.HeaderName = "X-Canary-Token")
            .AddAuthentication(authentication => authentication
                .UseDataProtection(dataProtection)
                .AddScheme(new AuthenticationScheme("canary", displayName: null, () => null!)))
            .AddAuthorization()
            .AddAuthorization(authorization => authorization.FallbackPolicy = null)
            .AddErrorHandling(errors => errors.OnError(
                (context, exception, cancellationToken) => ValueTask.FromResult(false)))
            .AddOpenApi()
            .AddOpenApi(document => { })
            .AddRouting()
            .AddContentSerialization(serialization => serialization.AddJson(resolver))
            .AddJsonSerialization(resolver)
            .AddJsonSerialization(resolver, json => json.WriteIndented = true)
            .AddValidation(validation => validation.Enabled = false);
    }
}
