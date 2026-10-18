using System.Collections.Generic;
using System.Linq;

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

    private bool IsDdlExistenceToken(int position)
    {
        bool unique = IsIndexStatement("CREATE") && _tokens.Count > 2 && IsWord(_tokens[1], "UNIQUE");
        int start = unique ? 3 : 2;
        return (IsDdlStatement("CREATE", "COLLECTION") || IsDdlStatement("DROP", "COLLECTION") ||
            IsIndexStatement("CREATE") || IsIndexStatement("DROP")) && StartsExistenceModifier(start) &&
            (position == start + 1 || position == start + 2 && IsWord(_tokens[start + 1], "NOT"));
    }

    private (int Start, int End) FindIndexKeyRange()
    {
        if (!IsIndexStatement("CREATE")) { return (-1, -1); }
        int start = -1;
        int depth = 0;
        for (int i = 0; i < _tokens.Count; i++)
        {
            if (_tokens[i].Type == TokenType.LeftParen)
            {
                if (start < 0) { start = i; }
                depth++;
            }
            else if (_tokens[i].Type == TokenType.RightParen && --depth == 0)
            { return (start, i); }
        }
        return (start, _tokens.Count);
    }

    private OqlCreateIndexExpression ParseCreateIndex()
    {
        var start = Current;
        Advance(); // CREATE
        bool unique = Take("UNIQUE");
        Expect("INDEX");
        bool ifNotExists = ParseIfExists(negated: true);
        string indexName = Identifier();
        Expect("ON");
        string collection = CollectionName();
        Expect(TokenType.LeftParen, "'('");
        var paths = new List<OqlPathExpression>();
        do
        {
            var token = Current;
            if (paths.Count == 32) { Error("OQL0009", "An index supports at most 32 key paths.", token); break; }
            if (Is("ALL") || Is("DISTINCT"))
            {
                string form = Is("ALL") ? "ALL array index key" : "DISTINCT ARRAY array index key";
                _diagnostics.Add(QueryDiagnostics.UnsupportedClause(form, Profile.Language, Span(token, token)));
                break;
            }
            OqlPathExpression path;
            if (Take(TokenType.LeftBracket))
            {
                var property = Current;
                Expect(TokenType.String, "a quoted root property");
                Expect(TokenType.RightBracket, "']'");
                path = ParsePath(token, Unquote(property.Text));
            }
            else { path = ParseDocumentPath(); }
            if (Failed) { break; }
            if (paths.Any(prior => prior.Segments.SequenceEqual(path.Segments)))
            { Error("OQL0002", "Index key paths must be distinct.", token); break; }
            paths.Add(path);
        } while (Take(TokenType.Comma));
        if (!Failed) { Expect(TokenType.RightParen, "')'"); }

        return new OqlCreateIndexExpression(indexName, collection, paths, unique, ifNotExists, Span(start, Previous));
    }

    private OqlDropIndexExpression ParseDropIndex()
    {
        var start = Current;
        Advance(); // DROP
        Expect("INDEX");
        bool ifExists = ParseIfExists(negated: false);
        string indexName = Identifier();
        Expect("ON");
        string collection = CollectionName();

        return new OqlDropIndexExpression(indexName, collection, ifExists, Span(start, Previous));
    }
}
