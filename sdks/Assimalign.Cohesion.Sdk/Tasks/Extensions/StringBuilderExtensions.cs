using System;
using System.Collections.Generic;
using System.Text;

namespace Assimalign.Cohesion.Sdk.Tasks;

internal static class StringBuilderExtensions
{
    extension(StringBuilder builder)
    {

        public StringBuilder AppendTabbed(int tabs, string text)
        {
            for (int i = 0; i < tabs; i++)
            {
                builder.Append("\t");
            }

            return builder.Append(text);
        }

        public StringBuilder AppendTabbedLine(int tabs, string text)
        {
            for (int i = 0; i < tabs; i++)
            {
                builder.Append("\t");
            }

            return builder.AppendLine(text);
        }
    }
}
