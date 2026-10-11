using System;
using System.Collections;

namespace Assimalign.Cohesion.Hosting.Tests;

internal sealed class UnwritableDataException : Exception
{
    public override IDictionary Data => throw new NotSupportedException("Diagnostic attachment is unavailable.");
}
