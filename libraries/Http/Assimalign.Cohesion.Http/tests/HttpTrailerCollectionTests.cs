using System;

using Assimalign.Cohesion.Http.Tests.TestObjects;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Tests;

public class HttpTrailerCollectionTests
{
    [Fact]
    public void Supported_ShouldAllowAddAndRoundTrip()
    {
        HttpTrailerCollection trailers = new(isSupported: true);

        trailers.IsSupported.ShouldBeTrue();
        trailers.IsReadOnly.ShouldBeFalse();

        trailers["X-Checksum"] = "abc123";
        trailers.Add("X-Trace", "t1");

        trailers["X-Checksum"].Value.ShouldBe("abc123");
        trailers["X-Trace"].Value.ShouldBe("t1");
        trailers.Count.ShouldBe(2);
    }

    [Fact]
    public void Supported_OverExistingFields_ShouldExposeThem()
    {
        HttpHeaderCollection parsed = new();
        parsed["X-Checksum"] = "deadbeef";
        HttpTrailerCollection trailers = new(parsed, isSupported: true);

        trailers.IsSupported.ShouldBeTrue();
        trailers["X-Checksum"].Value.ShouldBe("deadbeef");
    }

    [Fact]
    public void Unsupported_ShouldBeEmptyAndReadOnly()
    {
        HttpTrailerCollection trailers = new(isSupported: false);

        trailers.IsSupported.ShouldBeFalse();
        trailers.IsReadOnly.ShouldBeTrue();
        trailers.Count.ShouldBe(0);
    }

    [Fact]
    public void Unsupported_MutationShouldThrow()
    {
        HttpTrailerCollection trailers = new(isSupported: false);

        Should.Throw<InvalidOperationException>(() => trailers.Add("X-Trace", "t1"));
        Should.Throw<InvalidOperationException>(() => trailers["X-Trace"] = "t1");
        Should.Throw<InvalidOperationException>(() => trailers.Remove("X-Trace"));
        Should.Throw<InvalidOperationException>(() => trailers.Clear());
    }

    [Fact]
    public void Unsupported_ReadOperationsShouldNotThrow()
    {
        HttpTrailerCollection trailers = HttpTrailerCollection.Unsupported;

        trailers.ContainsKey("X-Trace").ShouldBeFalse();
        trailers.TryGetValue("X-Trace", out _).ShouldBeFalse();
        foreach (var _ in trailers)
        {
            // no-op — empty
        }
    }

    [Fact]
    public void UnsupportedSingleton_ShouldReportUnsupported()
    {
        HttpTrailerCollection.Unsupported.IsSupported.ShouldBeFalse();
    }

    [Fact]
    public void IsHttpHeaderCollection_ShouldBeAssignable()
    {
        // A trailer section is structurally a field section: the contract
        // extends IHttpHeaderCollection so it can be used wherever a field
        // collection is expected.
        IHttpTrailerCollection trailers = new HttpTrailerCollection(isSupported: true);

        trailers.ShouldBeAssignableTo<IHttpHeaderCollection>();
    }

    [Fact]
    public void RequestResponseDefault_ShouldBeUnsupported()
    {
        // The abstract HttpRequest/HttpResponse bases default Trailers to the
        // shared unsupported collection.
        TestHttpContext context = new(HttpVersion.Http11, new TestHttpRequest(), new TestHttpResponse());

        ((IHttpRequest)context.Request).Trailers.IsSupported.ShouldBeFalse();
        ((IHttpResponse)context.Response).Trailers.IsSupported.ShouldBeFalse();
    }
}
