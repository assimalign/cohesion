using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlCreateCollectionExpression ParseCreateCollection()
    {
        var start = Current;
        Advance(); // CREATE
        Expect("COLLECTION");
        bool ifNotExists = ParseIfExists(negated: true);
        string name = CollectionName();
        return new OqlCreateCollectionExpression(name, ifNotExists, Span(start, Previous));
    }

    private OqlDropCollectionExpression ParseDropCollection()
    {
        var start = Current;
        Advance(); // DROP
        Expect("COLLECTION");
        bool ifExists = ParseIfExists(negated: false);
        string name = CollectionName();
        return new OqlDropCollectionExpression(name, ifExists, Span(start, Previous));
    }

    // Shared by DDL families. IF is positional, leaving an unquoted name "if" usable.
    private bool ParseIfExists(bool negated)
    {
        if (!StartsExistenceModifier(_position)) { return false; }
        Advance(); // IF
        if (negated) { Expect("NOT"); }
        Expect("EXISTS");
        return true;
    }

    private bool StartsExistenceModifier(int position) => position + 1 < _tokens.Count &&
        IsWord(_tokens[position], "IF") &&
        (IsWord(_tokens[position + 1], "NOT") || IsWord(_tokens[position + 1], "EXISTS"));

    private bool IsCollectionExistenceToken(int position) =>
        (IsDdlStatement("CREATE", "COLLECTION") || IsDdlStatement("DROP", "COLLECTION")) &&
        StartsExistenceModifier(2) &&
        (position == 3 || position == 4 && IsWord(_tokens[3], "NOT"));

    private OqlCreateIndexExpression ParseCreateIndex()
    {
        var start = Current;
        Advance(); // CREATE
        Expect("INDEX");

        string indexName = Identifier();
        Expect("ON");
        string collection = CollectionName();
        Expect(TokenType.LeftParen, "'('");
        var path = ParseDocumentPath();
        Expect(TokenType.RightParen, "')'");

        return new OqlCreateIndexExpression(indexName, collection, path, Span(start, Previous));
    }

    private OqlDropIndexExpression ParseDropIndex()
    {
        var start = Current;
        Advance(); // DROP
        Expect("INDEX");

        string indexName = Identifier();
        Expect("ON");
        string collection = CollectionName();

        return new OqlDropIndexExpression(indexName, collection, Span(start, Previous));
    }
}
