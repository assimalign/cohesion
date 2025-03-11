using System;
using Assimalign.Cohesion.FileSystem.Globbing;
using Assimalign.Cohesion.FileSystem.Globbing.Tokens;
using Assimalign.Cohesion.FileSystem.Globbing.Internal;
using Xunit;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests
{
    public class TokeniserTests
    {
        [Theory]
        [InlineData("path/hatstand", 
            typeof(LiteralToken), 
            typeof(PathSeparatorToken), 
            typeof(LiteralToken))]
        [InlineData("p*th/ha?s[stu][s-z]and[1-3]/[!a-z]![1234Z]",
            typeof(LiteralToken), 
            typeof(WildcardToken), 
            typeof(LiteralToken), 
            typeof(PathSeparatorToken),
            typeof(LiteralToken), 
            typeof(AnyCharacterToken), 
            typeof(LiteralToken), 
            typeof(CharacterSetToken), 
            typeof(RangeToken), 
            typeof(LiteralToken), 
            typeof(RangeToken), 
            typeof(PathSeparatorToken),
            typeof(RangeToken), 
            typeof(LiteralToken), 
            typeof(CharacterSetToken))]
        [InlineData("p?th/*a[bcd]b[e-g]a[1-4][!wxyz][!a-c][!1-3].*",
            typeof(LiteralToken), 
            typeof(AnyCharacterToken), 
            typeof(LiteralToken), 
            typeof(PathSeparatorToken),
            typeof(WildcardToken), 
            typeof(LiteralToken), 
            typeof(CharacterSetToken),
            typeof(LiteralToken), 
            typeof(RangeToken), 
            typeof(LiteralToken),
            typeof(RangeToken), 
            typeof(CharacterSetToken), 
            typeof(RangeToken),
            typeof(RangeToken), 
            typeof(LiteralToken),
            typeof(WildcardToken))]
        [InlineData("path/**/*.*", 
            typeof(LiteralToken), typeof(WildcardDirectoryToken), typeof(WildcardToken), typeof(LiteralToken), typeof(WildcardToken))]
        [InlineData("**/gfx/*.gfx", 
            typeof(WildcardDirectoryToken), typeof(LiteralToken), typeof(PathSeparatorToken), typeof(WildcardToken), typeof(LiteralToken))] // https://github.com/dazinator/Assimalign.Cohesion.FileSystem.Globbing/issues/47
        [InlineData("**/gfx/**/*.gfx", 
            typeof(WildcardDirectoryToken), typeof(LiteralToken), typeof(WildcardDirectoryToken), typeof(WildcardToken), typeof(LiteralToken))] // https://github.com/dazinator/Assimalign.Cohesion.FileSystem.Globbing/issues/46       
        public void Can_Tokenise_Glob_Pattern(string testString, params Type[] expectedTokens)
        {
            // Arrange

            var glob = Glob.Parse(testString);

            

            Assert.True(glob.Tokens.Length == expectedTokens.Length);

            for (int i = 0; i < glob.Tokens.Length; i++)
            {
                var expectedToken = expectedTokens[i];

                Assert.IsType(expectedToken, glob.Tokens[i]);
            }
        }
    }
}
