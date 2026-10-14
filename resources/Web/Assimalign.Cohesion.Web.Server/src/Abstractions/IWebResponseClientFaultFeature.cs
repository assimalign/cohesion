using Assimalign.Cohesion.Http;

namespace Assimalign.Cohesion.Web;

/// <summary>
/// Reports that the transport ended this response because the client abandoned it or could not
/// receive it at the configured minimum response data rate.
/// </summary>
/// <remarks>
/// The default Web server publishes this feature on every exchange, including requests without a
/// body. An exception boundary reads it to classify a failed response as a cancelled exchange even
/// when middleware wraps the transport exception. A custom server may omit the feature; absence
/// means that no response-side client fault has been reported.
/// </remarks>
public interface IWebResponseClientFaultFeature : IHttpFeature
{
    /// <summary>
    /// Gets whether the transport aborted the response because of the client. Once set, the value
    /// remains <see langword="true"/> for the rest of the exchange. Application faults and local
    /// cancellation do not set it.
    /// </summary>
    bool ResponseAbortedByClient { get; }
}
