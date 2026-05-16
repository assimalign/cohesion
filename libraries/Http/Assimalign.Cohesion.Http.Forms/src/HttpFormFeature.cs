using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Http;

/// <summary>
/// Default <see cref="IHttpFormFeature"/> implementation installed by
/// <see cref="HttpContextFormExtensions.Form"/> and <c>ReadFormAsync</c>
/// when no feature is present.
/// </summary>
/// <remarks>
/// <para>
/// PR-1 scaffold: <see cref="ReadFormAsync"/> currently installs an empty
/// <see cref="HttpFormCollection"/> when nothing has been pre-attached. A
/// follow-up PR ports the multipart / urlencoded parser into this package and
/// will read against the request body via <c>context.Request.Body</c>.
/// </para>
/// </remarks>
internal sealed class HttpFormFeature : IHttpFormFeature
{
    public IHttpFormCollection? Form { get; set; }

    public Task<IHttpFormCollection> ReadFormAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (Form is not null)
        {
            return Task.FromResult(Form);
        }

        Form = new HttpFormCollection();
        return Task.FromResult(Form);
    }
}
