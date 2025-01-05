﻿using System;
using System.Diagnostics.CodeAnalysis;

namespace Assimalign.Cohesion.Hosting.Internal;

internal static class ThrowHelper
{
    [DoesNotReturn]
    internal static void ThrowInvalidOperationException(string message) =>
        throw new InvalidOperationException(message);

    [DoesNotReturn]
    internal static void ThrowArgumentNullException(string paramName) =>
        throw new ArgumentNullException(paramName);
}
