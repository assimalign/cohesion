using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Net.Security;

namespace Assimalign.Cohesion.ConfigurationStore.ApplicationModel.Orchestration.Tests;

/// <summary>
/// Stands in for the provider's transport factory: records the certificate validator of every
/// read and hands back a transport over a <see cref="RecordingHttpMessageHandler"/>.
/// </summary>
internal sealed class RecordingTransportFactory
{
    private readonly Func<HttpRequestMessage, HttpResponseMessage> _respond;

    internal RecordingTransportFactory(Func<HttpRequestMessage, HttpResponseMessage> respond)
    {
        _respond = respond;
    }

    internal List<RemoteCertificateValidationCallback?> Validators { get; } = new();

    internal List<RecordingHttpMessageHandler> Handlers { get; } = new();

    internal HttpMessageInvoker Create(RemoteCertificateValidationCallback? validator)
    {
        Validators.Add(validator);
        var handler = new RecordingHttpMessageHandler(_respond);
        Handlers.Add(handler);
        return new HttpMessageInvoker(handler, disposeHandler: true);
    }
}
