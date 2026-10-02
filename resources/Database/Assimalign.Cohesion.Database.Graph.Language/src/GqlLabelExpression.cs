using System.Text;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>
/// An ISO/IEC 39075 label expression (16.8): a label name, the wildcard <c>%</c>, a negation
/// <c>!</c>, a conjunction <c>&amp;</c> or a disjunction <c>|</c>. The parser binds <c>!</c>
/// tighter than <c>&amp;</c> and <c>&amp;</c> tighter than <c>|</c>; both binary operators
/// associate to the left, and parentheses group without adding a node.
/// </summary>
/// <remarks>
/// A node pattern's expression is evaluated against the node's label set; a relationship
/// pattern's against the relationship's one type. The engine evaluates only
/// <see cref="GqlLabelName"/>, <see cref="GqlLabelWildcard"/>, <see cref="GqlLabelNegation"/>,
/// <see cref="GqlLabelConjunction"/> and <see cref="GqlLabelDisjunction"/>; it rejects any other
/// derived record, a null operand or name, and nesting deeper than 128 levels.
/// </remarks>
public abstract record GqlLabelExpression
{
    /// <summary>Initializes a label expression; the five derived records are the only kinds.</summary>
    private protected GqlLabelExpression() { }

    /// <summary>Renders the expression in GQL syntax, parenthesized only where precedence requires.</summary>
    /// <returns>The expression text, such as <c>(A|B)&amp;!C</c>.</returns>
    public sealed override string ToString()
    {
        var builder = new StringBuilder();
        Write(builder, this);
        return builder.ToString();
    }

    private static void Write(StringBuilder builder, GqlLabelExpression? expression)
    {
        switch (expression)
        {
            case GqlLabelName name:
                WriteName(builder, name.Name);
                break;
            case GqlLabelWildcard:
                builder.Append('%');
                break;
            case GqlLabelNegation negation:
                builder.Append('!');
                WriteOperand(builder, negation.Operand, negation.Operand is GqlLabelName or GqlLabelWildcard);
                break;
            case GqlLabelConjunction conjunction:
                // Conjunction binds tighter than disjunction and associates to the left.
                WriteOperand(builder, conjunction.Left, conjunction.Left is not GqlLabelDisjunction);
                builder.Append('&');
                WriteOperand(builder, conjunction.Right, conjunction.Right is not (GqlLabelDisjunction or GqlLabelConjunction));
                break;
            case GqlLabelDisjunction disjunction:
                Write(builder, disjunction.Left);
                builder.Append('|');
                WriteOperand(builder, disjunction.Right, disjunction.Right is not GqlLabelDisjunction);
                break;
            default:
                builder.Append("<invalid>");
                break;
        }
    }

    private static void WriteOperand(StringBuilder builder, GqlLabelExpression? operand, bool bare)
    {
        if (bare)
        {
            Write(builder, operand);
            return;
        }

        builder.Append('(');
        Write(builder, operand);
        builder.Append(')');
    }

    private static void WriteName(StringBuilder builder, string? name)
    {
        bool regular = !string.IsNullOrEmpty(name) && (char.IsLetter(name[0]) || name[0] == '_');
        for (int i = 1; regular && i < name!.Length; i++)
        {
            regular = char.IsLetterOrDigit(name[i]) || name[i] == '_';
        }
        if (regular)
        {
            builder.Append(name);
            return;
        }

        builder.Append('"').Append(name).Append('"');
    }
}
