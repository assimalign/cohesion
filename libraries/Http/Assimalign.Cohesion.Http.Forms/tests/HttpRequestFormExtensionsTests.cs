using System;
using System.IO;
using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Forms.Tests;

public class HttpRequestFormExtensionsTests
{
    [Fact]
    public void GetForm_BeforeSetOrRead_ShouldReturnNull()
    {
        IHttpRequest request = new BareHttpRequest();

        IHttpFormCollection? form = request.GetForm();

        form.ShouldBeNull();
    }

    [Fact]
    public void SetForm_ThenGetForm_ShouldReturnInstalledCollection()
    {
        IHttpRequest request = new BareHttpRequest();
        HttpFormCollection installed = new();
        installed.Add("name", "cohesion");

        request.SetForm(installed);

        request.GetForm().ShouldBeSameAs(installed);
    }

    [Fact]
    public async Task ReadFormAsync_WithPreAttachedCollection_ShouldReturnIt()
    {
        IHttpRequest request = new BareHttpRequest();
        HttpFormCollection installed = new();
        installed.Add("name", "cohesion");
        request.SetForm(installed);

        IHttpFormCollection resolved = await request.ReadFormAsync();

        resolved.ShouldBeSameAs(installed);
        resolved["name"].Value.ShouldBe("cohesion");
    }

    [Fact]
    public async Task ReadFormAsync_NoCollectionAttached_ShouldReturnEmptyAndCache()
    {
        // PR-1 scaffold behaviour: ReadFormAsync produces an empty collection and caches it
        // for subsequent calls. A future PR ports the multipart parser into this package.
        IHttpRequest request = new BareHttpRequest();

        IHttpFormCollection first = await request.ReadFormAsync();
        IHttpFormCollection second = await request.ReadFormAsync();

        first.Count.ShouldBe(0);
        second.ShouldBeSameAs(first);
    }

    [Fact]
    public async Task ReadFormAsync_CancelledToken_ShouldThrow()
    {
        IHttpRequest request = new BareHttpRequest();
        using CancellationTokenSource cts = new();
        cts.Cancel();

        await Should.ThrowAsync<OperationCanceledException>(
            () => request.ReadFormAsync(cts.Token));
    }

    [Fact]
    public void SetForm_NullCollection_ShouldThrow()
    {
        IHttpRequest request = new BareHttpRequest();

        Should.Throw<ArgumentNullException>(() => request.SetForm(null!));
    }

    /// <summary>
    /// Bare-bones <see cref="IHttpRequest"/> stub. Only the identity of the instance matters
    /// for the extension methods (they use a conditional weak table keyed by the request).
    /// </summary>
    private sealed class BareHttpRequest : IHttpRequest
    {
        public HttpHost Host => HttpHost.Empty;
        public HttpPath Path => HttpPath.Root;
        public HttpMethod Method => HttpMethod.Get;
        public HttpScheme Scheme => HttpScheme.Http;
        public IHttpQueryCollection Query { get; } = new HttpQueryCollection();
        public IHttpHeaderCollection Headers { get; } = new HttpHeaderCollection();
        public IHttpCookieCollection Cookies { get; } = new HttpCookieCollection();
        public Stream Body => Stream.Null;
        public ClaimsPrincipal ClaimsPrincipal { get; } = new(new ClaimsIdentity());
    }
}
