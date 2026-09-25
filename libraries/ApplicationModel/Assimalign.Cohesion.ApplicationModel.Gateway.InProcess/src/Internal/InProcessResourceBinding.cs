using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Threading;

namespace Assimalign.Cohesion.ApplicationModel.Gateway.InProcess.Internal;

internal sealed record InProcessResourceBinding(
    Assembly EntryAssembly,
    string ContentRootPath)
{
    internal bool Matches(InProcessResourceBinding other) =>
        ReferenceEquals(EntryAssembly, other.EntryAssembly)
        && string.Equals(
            ContentRootPath,
            other.ContentRootPath,
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
}
