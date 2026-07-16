using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web.Serialization.Internal;

/// <summary>
/// Shared lookup used by the call-site extensions: resolves the registry feature from an
/// exchange, faulting when the application composed none.
/// </summary>
internal static class ContentSerializationFeatureResolver
{
    internal static IHttpContentSerializationFeature GetRequired(IHttpContext context)
    {
        return context.Features.Get<IHttpContentSerializationFeature>()
            ?? throw new HttpContentSerializationException(
                "No content-serialization registry is composed on this application. " +
                "Register one at builder time with AddJsonSerialization(...) or AddContentSerialization().");
    }
}
