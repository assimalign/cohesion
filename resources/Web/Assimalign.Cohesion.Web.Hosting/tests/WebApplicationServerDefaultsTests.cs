using Assimalign.Cohesion.Http;
using Assimalign.Cohesion.Http.Connections;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Web.Hosting.Tests;

public class WebApplicationServerDefaultsTests
{
    [Fact(DisplayName = "Cohesion Test [Web.Hosting] - Server defaults: Should install the max-request-body-size interceptor first")]
    public void ApplyDefaultInterceptors_ShouldInstallRequestLimitsFirst()
    {
        // The web host composes the listener options with the default interceptors ahead of any
        // user configuration, so the RequestLimits interceptor occupies slot 0 — guaranteeing
        // every request carries the typed feature and later head hooks can observe it.
        HttpConnectionListenerOptions options = new();

        WebApplicationServerBuilder.ApplyDefaultInterceptors(options);

        options.Interceptors.Count.ShouldBe(1);

        // Prove slot 0 is the RequestLimits interceptor by behavior: its head hook attaches the
        // typed feature as a write-through view over the context knob.
        HttpRequestInterceptorContext context = new()
        {
            Version = HttpVersion.Http11,
            Method = HttpMethod.Post,
            Path = new HttpPath("/upload"),
            Scheme = HttpScheme.Http,
            Host = new HttpHost("api.test"),
            Headers = new HttpHeaderCollection().AsReadOnly(),
            Features = new HttpFeatureCollection(),
            ConnectionInfo = HttpConnectionInfo.Empty,
            MaxRequestBodySize = 2048,
        };

        options.Interceptors[0].OnRequestHead(context);

        IHttpMaxRequestBodySizeFeature? feature = context.Features.Get<IHttpMaxRequestBodySizeFeature>();
        feature.ShouldNotBeNull();
        feature!.MaxRequestBodySize.ShouldBe(2048);
    }
}
