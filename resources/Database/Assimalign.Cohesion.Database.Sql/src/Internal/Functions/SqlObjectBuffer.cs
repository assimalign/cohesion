using System.Runtime.CompilerServices;

namespace Assimalign.Cohesion.Database.Sql.Internal;

/// <summary>
/// Four row values on the stack: the evaluated arguments of one call, held while the next argument
/// is evaluated, before they are converted to <see cref="SqlValue"/> (<see cref="SqlValueBuffer"/>).
/// </summary>
[InlineArray(Length)]
internal struct SqlObjectBuffer
{
    /// <summary>The number of slots.</summary>
    internal const int Length = 4;

    private object? _element;
}
