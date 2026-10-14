namespace Assimalign.Cohesion.Web.Rewrite.Internal;

/// <summary>
/// One piece of a parsed rule target: literal URL text, or a reference to a capture group of the rule's
/// pattern.
/// </summary>
internal readonly struct RewriteTemplateToken
{
    private RewriteTemplateToken(string? literal, int group)
    {
        Literal = literal;
        Group = group;
    }

    /// <summary>
    /// Gets the literal URL text, or <see langword="null"/> for a capture reference.
    /// </summary>
    public string? Literal { get; }

    /// <summary>
    /// Gets the capture group number, or <c>-1</c> for literal text.
    /// </summary>
    public int Group { get; }

    /// <summary>
    /// Gets a value indicating whether the token is literal text.
    /// </summary>
    public bool IsLiteral => Literal is not null;

    /// <summary>
    /// Creates a literal token.
    /// </summary>
    public static RewriteTemplateToken ForLiteral(string literal) => new(literal, -1);

    /// <summary>
    /// Creates a capture reference.
    /// </summary>
    public static RewriteTemplateToken ForGroup(int group) => new(null, group);
}
