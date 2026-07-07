using System;
using System.IO;
using System.Text;
using System.Threading.Tasks;

using Shouldly;

using Xunit;

namespace Assimalign.Cohesion.Http.DigestFields.Tests;

using Assimalign.Cohesion.Http;

public class HttpContentDigesterTests
{
    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Digester: Incremental appends equal a one-shot digest")]
    public void Append_InChunks_EqualsOneShot()
    {
        byte[] content = Encoding.UTF8.GetBytes("the quick brown fox jumps over the lazy dog");

        using var digester = new HttpContentDigester(HttpDigestAlgorithm.Sha256, HttpDigestAlgorithm.Sha512);
        digester.Append(content.AsSpan(0, 10));
        digester.Append(content.AsSpan(10, 20));
        digester.Append(content.AsSpan(30));
        HttpDigestField incremental = digester.ToField();

        HttpDigestField oneShot = HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256, HttpDigestAlgorithm.Sha512);
        incremental.Serialize().ShouldBe(oneShot.Serialize());
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Digester: ToField can be called and still allow further appends")]
    public void ToField_DoesNotReset()
    {
        byte[] first = Encoding.UTF8.GetBytes("abc");
        byte[] second = Encoding.UTF8.GetBytes("def");

        using var digester = new HttpContentDigester(HttpDigestAlgorithm.Sha256);
        digester.Append(first);
        HttpDigestField afterFirst = digester.ToField();
        digester.Append(second);
        HttpDigestField afterBoth = digester.ToField();

        afterFirst.Serialize().ShouldBe(HttpDigestField.ForContent(first, HttpDigestAlgorithm.Sha256).Serialize());
        afterBoth.Serialize().ShouldBe(HttpDigestField.ForContent(Encoding.UTF8.GetBytes("abcdef"), HttpDigestAlgorithm.Sha256).Serialize());
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Digester: Rejects an unsupported algorithm")]
    public void Constructor_UnsupportedAlgorithm_Throws()
    {
        Should.Throw<ArgumentException>(() => new HttpContentDigester(HttpDigestAlgorithm.Sha256, HttpDigestAlgorithm.Md5));
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Digester: Use after dispose throws")]
    public void Append_AfterDispose_Throws()
    {
        var digester = new HttpContentDigester(HttpDigestAlgorithm.Sha256);
        digester.Dispose();

        Should.Throw<ObjectDisposedException>(() => digester.Append(new byte[] { 1 }));
    }

    [Fact(DisplayName = "Cohesion Test [Http.DigestFields] - Digester: ForContentAsync over a stream equals the buffered digest")]
    public async Task ForContentAsync_Stream_EqualsBuffered()
    {
        byte[] content = Encoding.UTF8.GetBytes("streamed representation data");
        using var stream = new MemoryStream(content);

        HttpDigestField streamed = await HttpDigestField.ForContentAsync(
            stream, new[] { HttpDigestAlgorithm.Sha256 });

        streamed.Serialize().ShouldBe(HttpDigestField.ForContent(content, HttpDigestAlgorithm.Sha256).Serialize());
    }
}
