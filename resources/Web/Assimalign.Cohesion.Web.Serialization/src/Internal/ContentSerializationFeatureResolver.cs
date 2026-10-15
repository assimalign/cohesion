using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// Shared lookup used by the call-site extensions: resolves the registry feature from an
/// exchange, faulting when the application composed none.
/// </summary>
internal static class ContentSerializationFeatureResolver
{
    internal static IContentSerializationFeature GetRequired(IHttpContext context)
    {
        return context.Features.Get<IContentSerializationFeature>()
            ?? throw new HttpContentSerializationException(
                ErrorMessages.ContentSerializationNotRegistered);
    }
}
