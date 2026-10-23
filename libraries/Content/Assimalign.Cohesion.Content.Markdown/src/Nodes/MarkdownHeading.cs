using System;
using System.Collections.Generic;

namespace Assimalign.Cohesion.Content.Markdown;

/// <summary>
/// An ATX heading: a level from one through six and inline content.
/// </summary>
[global::System.Diagnostics.DebuggerDisplay("MarkdownHeading")]
[global::System.Diagnostics.DebuggerTypeProxy(typeof(DebugView))]
public sealed class MarkdownHeading : MarkdownBlock
{
    private int _level = 1;

    /// <summary>
    /// Initializes a new instance of the <see cref="MarkdownHeading"/> class.
    /// </summary>
    /// <param name="level">The heading level, one through six.</param>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="level"/> is outside one through six.</exception>
    public MarkdownHeading(int level)
    {
        Level = level;
    }

    /// <summary>Gets or sets the heading level, one through six.</summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown when the value is outside one through six.</exception>
    public int Level
    {
        get => _level;
        set
        {
            ArgumentOutOfRangeException.ThrowIfLessThan(value, 1);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(value, 6);
            _level = value;
        }
    }

    /// <summary>Gets the inline content of the heading, in order.</summary>
    public IList<MarkdownInline> Inlines { get; } = new List<MarkdownInline>();

    private sealed class DebugView
    {
        private readonly MarkdownHeading _target;

        public DebugView(MarkdownHeading value)
        {
            _target = value;
        }

        public int Level => _target.Level;
        public IList<MarkdownInline> Inlines => _target.Inlines;
    }
}
