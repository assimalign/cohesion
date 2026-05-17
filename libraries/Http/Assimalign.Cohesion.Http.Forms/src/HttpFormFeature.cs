using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpFormFeature"/> implementation installed by the
/// form extension surface when no other feature is attached.
/// </summary>
/// <remarks>
/// <para>
/// Scaffold implementation: <see cref="ReadFormAsync"/> currently produces an
/// empty <see cref="HttpFormCollection"/> and caches it on first call. The
/// full multipart / urlencoded body parser is being ported from the ASP.NET
/// Core <c>FormFeature</c> in a separate follow-up; until that lands,
/// consumers that already have parsed form data should attach it directly.
/// </para>
/// </remarks>
internal sealed class HttpFormFeature : IHttpFormFeature
{
    private IHttpFormCollection? _form;

    public HttpFormFeature()
    {
    }

    public HttpFormFeature(IHttpFormCollection form)
    {
        _form = form;
    }

    /// <inheritdoc />
    public string Name => nameof(IHttpFormFeature);

    /// <inheritdoc />
    public IHttpFormCollection? Form => _form;

    /// <inheritdoc />
    public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_form is not null)
        {
            return Task.FromResult(_form);
        }

        _form = new HttpFormCollection();
        return Task.FromResult(_form);
    }
}
