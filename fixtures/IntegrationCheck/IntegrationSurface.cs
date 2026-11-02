// This project makes sink-signature drift fail here instead of in consumer projects.
// Every side of every declared component-integration pair must be referenced here.

using System;
using System.Text.Json.Serialization.Metadata;
using System.Threading.Tasks;

using Assimalign.Cohesion.DependencyInjection;
using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Logging;
using Assimalign.Cohesion.Security.DataProtection;
using Assimalign.Cohesion.Web.Antiforgery;
using Assimalign.Cohesion.Web.Authentication;
using Assimalign.Cohesion.Web.Authorization;
using Assimalign.Cohesion.Web.Caching;
using Assimalign.Cohesion.Web.Compression;
using Assimalign.Cohesion.Web.CookiePolicy;
using Assimalign.Cohesion.Web.Cors;
using Assimalign.Cohesion.Web.Diagnostics;
using Assimalign.Cohesion.Web.ErrorHandling;
using Assimalign.Cohesion.Web.ForwardedHeaders;
using Assimalign.Cohesion.Web.HostFiltering;
using Assimalign.Cohesion.Web.HttpsPolicy;
using Assimalign.Cohesion.Web.OpenApi;
using Assimalign.Cohesion.Web.Query;
using Assimalign.Cohesion.Web.RateLimiting;
using Assimalign.Cohesion.Web.RequestTimeouts;
using Assimalign.Cohesion.Web.Rewrite;
using Assimalign.Cohesion.Web.Routing;
using Assimalign.Cohesion.Web.SecurityHeaders;
using Assimalign.Cohesion.Web.Serialization;
using Assimalign.Cohesion.Web.Sessions;
using Assimalign.Cohesion.Web.StaticFiles;
using Assimalign.Cohesion.Web.Validation;
using Assimalign.Cohesion.Web.WebSockets;

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
        IJsonTypeInfoResolver resolver,
        ILoggerFactory loggerFactory)
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
            // CORS registers the application's cors setting (an IWebApplicationSetting), not a feature.
            .AddCors()
            .AddCors(cors => cors.Options.AddDefaultPolicy(policy => policy.WithOrigins("https://canary.example")))
            .AddCors((services, cors) => cors.Options.AddPolicy("canary", policy => policy.AllowAnyOrigin()))
            .AddErrorHandling()
            .AddErrorHandling(errors => errors.OnError(
                (context, exception, cancellationToken) => ValueTask.FromResult(false)))
            .AddErrorHandling((services, errors) => errors.OnError(
                (context, exception, cancellationToken) => ValueTask.FromResult(false)))
            .AddErrorHandling(errors => errors.Options.IncludeDeveloperDetails = true)
            // Middleware registrations (2026-10-28): each Add verb registers the configured middleware its
            // parameterless Use verb places, so the canary calls each bare, with a callback, and with a provider.
            .AddForwardedHeaders(forwarded => forwarded.Options.Headers = ForwardedHeaderNames.XForwarded)
            .AddForwardedHeaders((services, forwarded) => forwarded.Options.Headers = ForwardedHeaderNames.Forwarded)
            .AddHsts()
            .AddHsts(hsts => hsts.Options.Preload = true)
            .AddHsts((services, hsts) => hsts.Options.IncludeSubDomains = true)
            .AddHttpsRedirection()
            .AddHttpsRedirection(redirection => redirection.Options.HttpsPort = 8443)
            .AddHttpsRedirection((services, redirection) => redirection.Options.HttpsPort = 8443)
            .AddCookiePolicy()
            .AddCookiePolicy(cookies => cookies.Options.HttpOnly = CookieHttpOnlyPolicy.Always)
            .AddCookiePolicy((services, cookies) => cookies.Options.Secure = CookieSecurePolicy.Always)
            // HTTP logging has no default logger, so it is never called bare.
            .AddHttpLogging(logging => logging.UseLoggerFactory(loggerFactory))
            .AddHttpLogging((services, logging) => logging.UseLoggerFactory(services.GetRequiredService<ILoggerFactory>()))
            .AddHostFiltering(hosts => hosts.Options.AllowedHosts.Add("canary.example"))
            .AddHostFiltering((services, hosts) => hosts.Options.AllowedHosts.Add("canary.example"))
            .AddOutputCache()
            .AddOutputCache(cache => cache.Options.AddBasePolicy(policy => policy.Duration = TimeSpan.FromMinutes(1)))
            .AddOutputCache((services, cache) => cache.UseStore(new InMemoryOutputCacheStore()))
            .AddQueryConditionals(conditionals => conditionals.UseResourceValidators(
                context => ValueTask.FromResult<WebQueryResourceValidators?>(null)))
            .AddQueryConditionals((services, conditionals) => conditionals.UseResourceValidators(
                context => ValueTask.FromResult<WebQueryResourceValidators?>(null)))
            .AddQueryValidation()
            .AddQueryValidation(validation => validation.Options.AdvertiseAcceptQuery = false)
            .AddQueryValidation((services, validation) => validation.Options.AdvertiseAcceptQuery = false)
            .AddRateLimiting()
            .AddRateLimiting(rateLimiting => rateLimiting.Options.RejectionStatusCode = HttpStatusCode.ServiceUnavailable)
            .AddRateLimiting((services, rateLimiting) => rateLimiting.Options.RejectionStatusCode = HttpStatusCode.ServiceUnavailable)
            .AddRequestDecompression()
            .AddRequestDecompression(decompression => decompression.Options.MaxDecompressedSizeBytes = 1024)
            .AddRequestDecompression((services, decompression) => decompression.Options.MaxDecompressedSizeBytes = 1024)
            .AddRequestTimeouts()
            .AddRequestTimeouts(timeouts => timeouts.Options.DefaultPolicy = new RequestTimeoutPolicy { Timeout = TimeSpan.FromSeconds(30) })
            .AddRequestTimeouts((services, timeouts) => timeouts.Options.DefaultPolicy = RequestTimeoutPolicy.Disabled)
            .AddResponseCompression()
            .AddResponseCompression(compression => compression.Options.EnableForHttps = true)
            .AddResponseCompression((services, compression) => compression.Options.EnableBrotli = false)
            .AddRewrite()
            .AddRewrite(rewrite => rewrite.Options.AddRedirectToHttps())
            .AddRewrite((services, rewrite) => rewrite.Options.AddRedirectToHttps())
            .AddSecurityHeaders()
            .AddSecurityHeaders(headers => headers.Options.ReferrerPolicy = ReferrerPolicy.NoReferrer)
            .AddSecurityHeaders((services, headers) => headers.Options.CrossOriginOpenerPolicy = CrossOriginOpenerPolicy.SameOrigin)
            .AddSessions()
            .AddSessions(sessions => sessions.Options.IdleTimeout = TimeSpan.FromMinutes(30))
            .AddSessions((services, sessions) => sessions.UseStore(new InMemoryHttpSessionStore()))
            .AddStaticFiles()
            .AddStaticFiles(files => files.Options.CacheControl = "no-cache")
            .AddStaticFiles((services, files) => files.Options.RequestPath = new HttpPath("/assets"))
            .AddStatusCodePages()
            .AddStatusCodePages(pages => pages.Options.Responder = context => Task.CompletedTask)
            .AddStatusCodePages((services, pages) => pages.Options.Responder = context => Task.CompletedTask)
            .AddWebSockets()
            .AddWebSockets(webSockets => webSockets.Options.AllowedOrigins.Add("https://canary.example"))
            .AddWebSockets((services, webSockets) => webSockets.Options.AllowAnyOrigin = true)
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
