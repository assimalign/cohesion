using System.Diagnostics;

namespace Assimalign.Cohesion.Web.WebSockets.Tests.TestObjects;

internal sealed class DiagnosticRequestIdFeature : IWebRequestIdFeature
{
    public ActivityTraceId Id { get; } = ActivityTraceId.CreateRandom();

    public int ReadCount { get; private set; }

    public string Name => "DiagnosticRequestId";

    public ActivityTraceId RequestId
    {
        get
        {
            ReadCount++;
            return Id;
        }
    }
}
