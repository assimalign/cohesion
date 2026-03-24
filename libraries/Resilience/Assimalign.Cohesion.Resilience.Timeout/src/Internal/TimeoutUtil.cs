using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Resilience.Internal;

internal static class TimeoutUtil
{
    public const string TimeSpanInvalidMessage = "The '{0}' must be a positive TimeSpan (or Timeout.InfiniteTimeSpan to indicate no timeout).";

    public static bool ShouldApplyTimeout(TimeSpan timeout) => timeout > TimeSpan.Zero;
}