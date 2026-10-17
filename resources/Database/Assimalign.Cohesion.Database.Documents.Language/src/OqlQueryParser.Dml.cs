using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Documents.Language;

public sealed partial class OqlQueryParser
{
    private OqlInsertExpression ParseInsert()
    {
        var start = Current;
        Advance();
        Expect("INTO");
        string collection = CollectionName();
        string? alias = Take("AS") ? Identifier() : null;
        if (!Take(TokenType.LeftParen))
        {
            Error("OQL0002", "INSERT requires an explicit KEY header; document ids are not generated.", Current);
            return new OqlInsertExpression(collection, alias, [], null, null, null, Span(start, Previous));
        }
        Expect("KEY");
        string? keyName = null;
        string? valueName = null;
        bool valuesHeader = Take(TokenType.Comma);
        if (valuesHeader)
        {
            Expect("VALUE");
        }
        else
        {
            keyName = Identifier();
            if (Take(TokenType.Comma))
            {
                Expect("VALUE");
                valueName = Identifier();
            }
        }
        if (Current.Type == TokenType.Comma && _position + 1 < _tokens.Count && IsWord(_tokens[_position + 1], "OPTIONS"))
        {
            Advance();
            _diagnostics.Add(QueryDiagnostics.UnsupportedClause("OPTIONS", Profile.Language, Span(Current, Current)));
            return new OqlInsertExpression(collection, alias, [], null, keyName, valueName, Span(start, Current));
        }
        Expect(TokenType.RightParen, "')'");
        List<(OqlExpression Key, OqlExpression Value)> values = [];
        OqlSelectExpression? select = null;
        if (valuesHeader)
        {
            Expect("VALUES");
            do
            {
                Expect(TokenType.LeftParen, "'('");
                var key = ParseExpression();
                Expect(TokenType.Comma, "',' followed by a VALUE expression");
                var value = ParseExpression();
                Expect(TokenType.RightParen, "')'");
                values.Add((key, value));
            } while (!Failed && Take(TokenType.Comma));
        }
        else if (Is("SELECT"))
        {
            select = ParseSelect();
        }
        else
        {
            Error("OQL0002", "Expected SELECT after the KEY output binding.", Current);
        }
        return new OqlInsertExpression(collection, alias, values.AsReadOnly(), select, keyName, valueName, Span(start, Previous));
    }
}
