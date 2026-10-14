using System.Text;

namespace Assimalign.Cohesion.Build.Tasks;

internal static class StringBuilderExtensions
{
    public static StringBuilder AppendTabs(this StringBuilder builder, ushort tabs)
    {
        for (int i = 0; i < tabs; i++)
        {
            builder.Append("\t");
        }

        return builder;
    }

    public static StringBuilder AppendTabbedLine(this StringBuilder builder, ushort tabs, string value)
    {
        var lines = value.Split('\n');

        for (int i = 0; i < lines.Length; i++)
        {
            var line = lines[i].Replace("\r", string.Empty);

            builder.AppendTabs(tabs).AppendLine(line);
        }

        return builder;
    }
}
