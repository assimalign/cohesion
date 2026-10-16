using System.Collections.Generic;

using Shouldly;

namespace Assimalign.Cohesion.Content.Markdown.Tests;

// Structural comparison authored for this corpus; no serialization/reflection or renderer oracle.
internal static class MarkdownModelComparison
{
    internal static void ShouldEqual(MarkdownDocument expected, MarkdownDocument actual, string context)
    {
        var pending = new Queue<(MarkdownNode Expected, MarkdownNode Actual)>();
        pending.Enqueue((expected, actual));
        while (pending.TryDequeue(out var pair))
        {
            switch (pair)
            {
                case (MarkdownDocument a, MarkdownDocument b): Add(a.Blocks, b.Blocks); break;
                case (MarkdownBlockQuote a, MarkdownBlockQuote b): Add(a.Blocks, b.Blocks); break;
                case (MarkdownListItem a, MarkdownListItem b): Add(a.Blocks, b.Blocks); break;
                case (MarkdownParagraph a, MarkdownParagraph b): Add(a.Inlines, b.Inlines); break;
                case (MarkdownHeading a, MarkdownHeading b):
                    b.Level.ShouldBe(a.Level, context); Add(a.Inlines, b.Inlines); break;
                case (MarkdownList a, MarkdownList b):
                    b.IsOrdered.ShouldBe(a.IsOrdered, context);
                    b.IsTight.ShouldBe(a.IsTight, context);
                    b.Start.ShouldBe(a.Start, context);
                    Add(a.Items, b.Items); break;
                case (MarkdownCodeBlock a, MarkdownCodeBlock b):
                    b.Info.ShouldBe(a.Info, context); b.Literal.ShouldBe(a.Literal, context); break;
                case (MarkdownThematicBreak, MarkdownThematicBreak): break;
                case (MarkdownLiteral a, MarkdownLiteral b): b.Text.ShouldBe(a.Text, context); break;
                case (MarkdownCodeSpan a, MarkdownCodeSpan b): b.Literal.ShouldBe(a.Literal, context); break;
                case (MarkdownLineBreak a, MarkdownLineBreak b): b.IsHard.ShouldBe(a.IsHard, context); break;
                case (MarkdownEmphasis a, MarkdownEmphasis b): Add(a.Inlines, b.Inlines); break;
                case (MarkdownStrong a, MarkdownStrong b): Add(a.Inlines, b.Inlines); break;
                case (MarkdownLink a, MarkdownLink b):
                    b.Destination.ShouldBe(a.Destination, context); b.Title.ShouldBe(a.Title, context);
                    Add(a.Inlines, b.Inlines); break;
                case (MarkdownImage a, MarkdownImage b):
                    b.Destination.ShouldBe(a.Destination, context); b.Title.ShouldBe(a.Title, context);
                    Add(a.Inlines, b.Inlines); break;
                default: true.ShouldBeFalse($"Node kinds differ: {context}; expected {pair.Expected}, actual {pair.Actual}"); break;
            }
        }

        void Add<T>(IList<T> first, IList<T> second) where T : MarkdownNode
        {
            second.Count.ShouldBe(first.Count, context);
            for (var index = 0; index < first.Count; index++)
            {
                pending.Enqueue((first[index], second[index]));
            }
        }
    }
}
