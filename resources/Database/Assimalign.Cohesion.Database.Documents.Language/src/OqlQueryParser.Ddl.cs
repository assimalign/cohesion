using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
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
