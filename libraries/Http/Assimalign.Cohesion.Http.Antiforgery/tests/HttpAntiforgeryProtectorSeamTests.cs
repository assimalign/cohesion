using System;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading.Tasks;

using Assimalign.Cohesion.Security.DataProtection;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.Antiforgery.Tests;

/// <summary>
/// Exercises the <see cref="IHttpAntiforgeryProtector"/> seam introduced for #774: antiforgery
/// delegates its cryptography to a pluggable protector, so a rotating-key-ring implementation
/// lets multiple nodes validate each other's tokens without hand-distributing raw key bytes.
/// </summary>
public class HttpAntiforgeryProtectorSeamTests
{
    private const string AntiforgeryPurpose = "Cohesion.Http.Antiforgery.v1";

    /// <summary>Adapts a data-protection <see cref="IDataProtector"/> to the antiforgery seam.</summary>
    private sealed class DataProtectionAntiforgeryProtector : IHttpAntiforgeryProtector
    {
        private readonly IDataProtector _protector;

        public DataProtectionAntiforgeryProtector(IDataProtector protector) => _protector = protector;

        public byte[] Protect(ReadOnlySpan<byte> plaintext) => _protector.Protect(plaintext);

        public bool TryUnprotect(ReadOnlySpan<byte> protectedData, [NotNullWhen(true)] out byte[]? plaintext)
        {
            try
            {
                plaintext = _protector.Unprotect(protectedData);
                return true;
            }
            catch (DataProtectionException)
            {
                plaintext = null;
                return false;
            }
        }
    }

    private sealed class TempDirectory : IDisposable
    {
        public TempDirectory()
        {
            Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "cohesion-af-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    private static IHttpAntiforgery CreateNode(string keyDirectory, string discriminator)
    {
        IDataProtector protector = DataProtectionProvider
            .Create(KeyRepository.CreateFileSystem(keyDirectory), o => o.ApplicationDiscriminator = discriminator)
            .CreateProtector(AntiforgeryPurpose);

        return HttpAntiforgery.Create(new HttpAntiforgeryOptions
        {
            Protector = new DataProtectionAntiforgeryProtector(protector),
        });
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Seam: A ring-backed protector lets a second node validate the first node's tokens")]
    public async Task IsRequestValid_AcrossNodesSharingAKeyRing_ShouldPass()
    {
        using TempDirectory keys = new();

        // Node A mints a token pair backed by the shared key ring.
        IHttpAntiforgery nodeA = CreateNode(keys.Path, "web");
        HttpAntiforgeryTokenSet tokens = nodeA.GetAndStoreTokens(new TestHttpContext(HttpMethod.Get));

        // Node B is an independent service over the same key directory — no raw key bytes copied.
        IHttpAntiforgery nodeB = CreateNode(keys.Path, "web");
        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie("__cohesion-antiforgery", tokens.CookieToken!);
        post.SetRequestHeader("X-CSRF-TOKEN", tokens.RequestToken!);

        (await nodeB.IsRequestValidAsync(post)).ShouldBeTrue();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Seam: A node with a different app discriminator rejects the tokens")]
    public async Task IsRequestValid_AcrossNodesWithDifferentDiscriminator_ShouldFail()
    {
        using TempDirectory keys = new();

        IHttpAntiforgery nodeA = CreateNode(keys.Path, "web");
        HttpAntiforgeryTokenSet tokens = nodeA.GetAndStoreTokens(new TestHttpContext(HttpMethod.Get));

        // Same key directory, different application discriminator ⇒ isolated subkeys.
        IHttpAntiforgery nodeC = CreateNode(keys.Path, "other-app");
        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie("__cohesion-antiforgery", tokens.CookieToken!);
        post.SetRequestHeader("X-CSRF-TOKEN", tokens.RequestToken!);

        (await nodeC.IsRequestValidAsync(post)).ShouldBeFalse();
    }

    [Fact(DisplayName = "Cohesion Test [Http.Antiforgery] - Seam: A configured protector supersedes the static Key")]
    public async Task Protector_WhenSet_ShouldSupersedeKey()
    {
        using TempDirectory keys = new();
        IDataProtector shared = DataProtectionProvider
            .Create(KeyRepository.CreateFileSystem(keys.Path), o => o.ApplicationDiscriminator = "web")
            .CreateProtector(AntiforgeryPurpose);

        // Two services share the protector but deliberately carry different (ignored) Key bytes.
        IHttpAntiforgery mintService = HttpAntiforgery.Create(new HttpAntiforgeryOptions
        {
            Key = new byte[] { 1, 2, 3, 4 },
            Protector = new DataProtectionAntiforgeryProtector(shared),
        });
        IHttpAntiforgery validateService = HttpAntiforgery.Create(new HttpAntiforgeryOptions
        {
            Key = new byte[] { 5, 6, 7, 8 },
            Protector = new DataProtectionAntiforgeryProtector(shared),
        });

        HttpAntiforgeryTokenSet tokens = mintService.GetAndStoreTokens(new TestHttpContext(HttpMethod.Get));
        TestHttpContext post = new(HttpMethod.Post);
        post.SetRequestCookie("__cohesion-antiforgery", tokens.CookieToken!);
        post.SetRequestHeader("X-CSRF-TOKEN", tokens.RequestToken!);

        // Validation succeeds because both use the same protector; the differing Key is ignored.
        (await validateService.IsRequestValidAsync(post)).ShouldBeTrue();
    }
}
