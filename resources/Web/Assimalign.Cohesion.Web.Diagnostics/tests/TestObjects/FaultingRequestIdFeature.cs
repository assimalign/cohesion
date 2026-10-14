using System;
using System.Diagnostics;

namespace Assimalign.Cohesion.Web.Diagnostics.Tests.TestObjects;

internal sealed class FaultingRequestIdFeature : IWebRequestIdFeature
{
    private readonly Exception _exception;

    public FaultingRequestIdFeature(Exception exception)
    {
        _exception = exception;
    }

    public string Name => nameof(FaultingRequestIdFeature);

    public ActivityTraceId RequestId => throw _exception;
}
