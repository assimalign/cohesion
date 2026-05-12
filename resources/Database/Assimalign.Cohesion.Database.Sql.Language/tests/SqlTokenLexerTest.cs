using System;
using System.Collections.Generic;
using System.Text;
using Xunit;

namespace Assimalign.Cohesion.Database.Language.Tests;

public class SqlTokenLexerTest
{
    [Fact]
    public void Test()
    {
        var query = "SELECT Count(*) FROM dbo.Users";
        var lexer = new TokenLexer(query, TokenLexerOptions.Sql);


        var tokens = new List<(string Value, int Position, TokenType Type)>();

        while (lexer.MoveNext())
        {
            tokens.Add((lexer.Current.Value.ToString(), lexer.Current.Position, lexer.Current.Type));
        }

        


    }
}
