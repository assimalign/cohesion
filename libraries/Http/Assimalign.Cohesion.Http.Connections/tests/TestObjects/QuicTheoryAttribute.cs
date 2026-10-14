using System;
using System.Net.Quic;

using Xunit;

namespace Assimalign.Cohesion.Http.Connections.Tests.TestObjects;

/// <summary>Reports QUIC interoperability tests as skipped when the native provider is unavailable.</summary>
[AttributeUsage(AttributeTargets.Method)]
internal sealed class QuicTheoryAttribute : TheoryAttribute
{
    public QuicTheoryAttribute()
    {
        if (!(OperatingSystem.IsWindows() || OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            || !QuicListener.IsSupported)
        {
            Skip = "System.Net.Quic is unavailable on this platform (including missing msquic).";
        }
    }
}
