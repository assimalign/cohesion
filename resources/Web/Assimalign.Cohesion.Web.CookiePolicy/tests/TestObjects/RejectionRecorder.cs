using System.Collections.Concurrent;
using System.Linq;

namespace Assimalign.Cohesion.Web.CookiePolicy.Tests.TestObjects;

/// <summary>
/// Collects what <see cref="CookiePolicyOptions.OnRejected"/> reports. The hook runs on the server's
/// exchange flow, so the recorder is safe to read from the test after the response arrives.
/// </summary>
internal sealed class RejectionRecorder
{
    private readonly ConcurrentQueue<(string Name, CookiePolicyRejectionReason Reason)> _rejections = new();

    /// <summary>Gets the rejections reported so far, in order.</summary>
    public (string Name, CookiePolicyRejectionReason Reason)[] Rejections => _rejections.ToArray();

    /// <summary>Records one rejection; assign this method to <see cref="CookiePolicyOptions.OnRejected"/>.</summary>
    public void Record(CookiePolicyRejectionContext rejection)
        => _rejections.Enqueue((rejection.Cookie.Name, rejection.Reason));

    /// <summary>Gets whether a cookie with the name was rejected for the reason.</summary>
    public bool Contains(string name, CookiePolicyRejectionReason reason)
        => _rejections.Any(rejection => rejection.Name == name && rejection.Reason == reason);
}
