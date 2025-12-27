using System;
using System.Linq;
using Xunit;

namespace System.IO.Tests;

using static Glob;

public class GlobTests
{
    [Theory]
    [InlineData("literal", "fliteral", "foo/literal", "literals", "literals/foo")]
    [InlineData("path/hats*nd", "path/hatsblahn", "path/hatsblahndt")]
    [InlineData("path/?atstand", "path/moatstand", "path/batstands")]
    [InlineData("/**/file.csv", "/file.txt")]
    [InlineData("/*file.txt", "/folder")]
    [InlineData("Shock* 12", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Adobe\\Shockwave 12")]
    [InlineData("*Shock* 12", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Adobe\\Shockwave 12")]
    [InlineData("*ave*2", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Adobe\\Shockwave 12")]
    [InlineData("*ave 12", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Adobe\\Shockwave 12")]
    ////[InlineData("*ave 12", "wave 12/")] // This doesn't works as FileSystemPath trims trailing separators
    [InlineData("C:\\THIS_IS_A_DIR\\**\\somefile.txt", "C:\\THIS_IS_A_DIR\\awesomefile.txt")]
    [InlineData("C:\\name\\**", "C:\\name.ext", "C:\\name_longer.ext")]
    [InlineData("Bumpy/**/AssemblyInfo.cs", "Bumpy.Test/Properties/AssemblyInfo.cs")]     
    [InlineData("C:\\sources\\x-y 1\\BIN\\DEBUG\\COMPILE\\**\\MSVC*120.DLL", "C:\\sources\\x-y 1\\BIN\\DEBUG\\COMPILE\\ANTLR3.RUNTIME.DLL")]     
    [InlineData("literal1", "LITERAL1")] 
    [InlineData("*ral*", "LITERAL1")] 
    [InlineData("[list]s", "LS", "iS", "Is")] 
    [InlineData("range/[a-b][C-D]", "range/ac", "range/Ad", "range/BD")] 
    [InlineData(@"abc/**", @"abcd")]
    [InlineData(@"**\segment1\**\segment2\**", @"C:\test\segment1\src\segment2")]
    [InlineData(@"**/.*", "foobar.")]
    [InlineData(@"**/~*", "/")]
    public void TestIsNotMatching(string pattern, params string[] testStrings)
    {
        var glob = Glob.Parse(pattern);

        foreach (var testString in testStrings)
        {
            var match = glob.IsMatch(testString);

            Assert.False(match);
        }
    }

    [Theory]
    [InlineData("literal", "literal")]
    [InlineData("a/literal", "a/literal")]
    [InlineData("path/*atstand", "path/fooatstand")]
    [InlineData("path/hats*nd", "path/hatsforstand")]
    [InlineData("path/?atstand", "path/hatstand")]
    [InlineData("path/?atstand?", "path/hatstands")]
    [InlineData("p?th/*a[bcd]", "pAth/fooooac")]
    [InlineData("p?th/*a[bcd]b[e-g]a[1-4]", "pAth/fooooacbfa2")]
    [InlineData("p?th/*a[bcd]b[e-g]a[1-4][!wxyz]", "pAth/fooooacbfa2v")]
    [InlineData("p?th/*a[bcd]b[e-g]a[1-4][!wxyz][!a-c][!1-3].*", "pAth/fooooacbfa2vd4.txt")]
    [InlineData("path/**/somefile.txt", "path/foo/bar/baz/somefile.txt")]
    [InlineData("p?th/*a[bcd]b[e-g]a[1-4][!wxyz][!a-c][!1-3].*", "pGth/yGKNY6acbea3rm8.")]
    [InlineData("/**/file.*", "/folder/file.csv")]
    [InlineData("/**/file.*", "/file.txt")]
    [InlineData("**/file.*", "/file.txt")]
    [InlineData("/*file.txt", "/file.txt")]
    [InlineData("C:/**/*.txt", "C:/folder/file.txt")]
    [InlineData("C:\\THIS_IS_A_DIR\\*", "C:\\THIS_IS_A_DIR\\somefile")]
    [InlineData("/DIR1/*/*", "/DIR1/DIR2/file.txt")]
    [InlineData("~/*~3", "~/abc123~3")]
    [InlineData("**\\Shock* 12", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Adobe\\Shockwave 12")]
    [InlineData("**\\*ave*2", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Adobe\\Shockwave 12")]
    [InlineData("**", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Adobe\\Shockwave 12")]
    [InlineData("**", "HKEY_LOCAL_MACHINE\\SOFTWARE\\Adobe\\Shockwave 12.txt")]
    [InlineData("Stuff, *", "Stuff, x")]
    [InlineData("\"Stuff*", "\"Stuff")]
    [InlineData("path/**/somefile.txt", "path//somefile.txt")]
    [InlineData("**/app*.js", "dist/app.js", "dist/app.a72ka8234.js")]
    [InlineData("**/y", "y")]
    [InlineData("**/gfx/*.gfx", "HKEY_LOCAL_MACHINE\\gfx\\foo.gfx", "HKEY_LOCAL_MACHINE/gfx/foo.gfx")]
    [InlineData("**/gfx/**/*.gfx", "a_b\\gfx\\bar\\foo.gfx", "a_b/gfx/bar/foo.gfx")]
    [InlineData("**\\gfx\\**\\*.gfx", "a_b\\gfx\\bar\\foo.gfx", "a_b/gfx/bar/foo.gfx")]
    [InlineData(@"/foo/bar!.baz", @"/foo/bar!.baz")] // match a ! after bar
    [InlineData(@"/foo/bar[!!].baz", @"/foo/bar7.baz")] // anything except an exclaimation mark after bar
    [InlineData(@"/foo/bar[!]].baz", @"/foo/bar9.baz")] // anything except an ] after bar
    [InlineData(@"/foo/bar[!?].baz", @"/foo/bar7.baz")] // anything except an ? after bar
    [InlineData(@"/foo/bar[![].baz", @"/foo/bar7.baz")] // anything except an [ after bar
    [InlineData(@"C:\myergen\[[]a]tor", @"C:\myergen\[a]tor")]
    [InlineData(@"C:\myergen\[[]ator", @"C:\myergen\[ator")]
    [InlineData(@"C:\myergen\[[][]]ator", @"C:\myergen\[]ator")]
    [InlineData(@"C:\myergen[*]ator", @"C:\myergen*ator")]
    [InlineData(@"C:\myergen[*][]]ator", @"C:\myergen*]ator")]
    [InlineData(@"C:\myergen[*]]ator", @"C:\myergen*ator", @"C:\myergen]ator")]
    [InlineData(@"C:\myergen[?]ator", @"C:\myergen?ator")]
    [InlineData(@"/path[\]hatstand", @"/path\hatstand")]
    [InlineData(@"**\[#!]*\**", @"#test3", @"#test3\", @"\#test3\foo", @"\#test3")]
    [InlineData(@"**\[#!]*", @"#test3", "#this is a comment", @"\#test3")]
    [InlineData(@"[#!]*\**", "#this is a comment")]
    [InlineData(@"[#!]*", @"#test3", "#this is a comment")]
    [InlineData(@"abc/**", @"abc/def/hij.txt")]
    [InlineData(@"a/**/b", "a/b")]
    [InlineData(@"abc/**", "abc/def")]
    [InlineData(@"/some/path/**/some.file*.exe", "/some/path/some.file.exe")]
    [InlineData(@"**/some/path/some.file*.exe", "/some/path/some.file.exe")]
    public void TestIsMatching(string pattern, params string[] testStrings)
    {
        var glob = Glob.Parse(pattern);

        foreach (var testString in testStrings)
        {
            ReadOnlySpan<char> path = testString;

            var match = glob.IsMatch(path);
            
            Assert.True(match, $"glob {pattern} failed to match test string: {testString}");
        }
    }

    
    [Theory]
    [InlineData("literal1", "LITERAL1", "literal1")]
    [InlineData("*ral*", "LITERAL1", "literal1")]
    [InlineData("[list]s", "LS", "ls", "iS", "Is")]
    [InlineData("range/[a-b][C-D]", "range/ac", "range/Ad", "range/bC", "range/BD")]
    public void TestIsMatchCaseSensitive(string pattern, params string[] testStrings)
    {
        var glob = Glob.Parse(pattern);

        foreach (var testString in testStrings)
        {
            var match = glob.IsMatch(testString, true);

            Assert.True(match);
        }
    }

    [Fact]
    public void TestToString()
    {
        const string pattern = "p?th/*a[bcd]b[e-g]/**/a[1-4][!wxyz][!a-c][!1-3].*";
        var glob = Glob.Parse(pattern);
        var resultPattern = glob.ToString();
        Assert.Equal(pattern, resultPattern);
    }

    //[Theory]

    //public void EscapeCharactersTest(string pattern, string expectedFormatted)
    //{
    //    var glob = Globbing.Glob.Parse(pattern);
    //    Assert.Equal(expectedFormatted, glob.ToString());
    //}



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
    public void TestTokenParsing(string testString, params Type[] expectedTokens)
    {
        // Arrange
        var glob = Glob.Parse(testString);
        var tokens = glob.Tokens;

        int count = 0;

        for (int i = 0; i < tokens.Length; i++)
        {
            var token = tokens[i];
            var type = expectedTokens[i];

            Assert.IsType(type, token);

            count++;

            if (token is CompositeGlobToken composite)
            {
                AssertComposite(composite, expectedTokens.Skip(i + 1).ToArray(), ref count);
            }
        }

        Assert.True(count == expectedTokens.Length);

        void AssertComposite(CompositeGlobToken composite, Type[] remaining, ref int count)
        {
            var tokens = composite.Tokens;

            for (int i = 0; i < tokens.Length; i++)
            {
                var token = tokens[i];
                var type = remaining[i];

                Assert.IsType(type, token);

                count++;

                if (token is CompositeGlobToken child)
                {
                    AssertComposite(child, remaining.Skip(i + 1).ToArray(), ref count);
                }
            }
        }
    }

    //[Fact]
    void GlobPatternBuilderTests()
    {
        //// 
        //// build the following glob pattern using glob builder:
        ////       /foo?\\*[abc][!1-3]/**/*.txt
        //var tokens = new GlobBuilder()
        //    .PathSeparator()
        //    .Literal("foo")
        //    .AnyCharacter()
        //    .PathSeparator(PathSeparatorKind.BackwardSlash)
        //    .Wildcard()
        //    .OneOf('a', 'b', 'c')
        //    .NumberNotInRange('1', '3')
        //    .DirectoryWildcard(PathSeparatorKind.ForwardSlash, PathSeparatorKind.ForwardSlash)
        //    .Wildcard()
        //    .Literal(".txt")
        //    .Tokens;

        //Assert.Equal(10, tokens.Count);
        //Assert.True(tokens[0] is PathSeparatorToken);
        //Assert.True(tokens[1] is LiteralToken);
        //Assert.True(tokens[2] is AnyCharacterToken);
        //Assert.True(tokens[3] is PathSeparatorToken);
        //Assert.True(tokens[4] is WildcardToken);
        //Assert.True(tokens[5] is CharacterListToken);
        //Assert.True(tokens[6] is NumberRangeToken);
        //Assert.True(tokens[7] is WildcardDirectoryToken);
        //Assert.True(tokens[8] is WildcardToken);
        //Assert.True(tokens[9] is LiteralToken);
    }

    //[Theory]
    // Identifier tests
    [InlineData("$tf/", @"$tf/", "xtf")]

    // Wildcard tests
    [InlineData("*.txt", @"c:\windows\file.txt", "file.zip")]
    [InlineData("*.txt", "file.txt")]
    [InlineData("/some/dir/folder/foo.*", "/some/dir/folder/foo.txt")]
    [InlineData("/some/dir/folder/foo.*", "/some/dir/folder/foo.csv")]
    [InlineData("a_*file.txt", "a_bigfile.txt", "another_file.txt")]
    [InlineData("a_*file.txt", "a_file.txt")]
    [InlineData("*file.txt", "bigfile.txt")]
    [InlineData("*file.txt", "smallfile.txt")]
    [InlineData("a/*", "a/", "a")]
    [InlineData("*", "a")]
    [InlineData("*", "folder1/a")]
    [InlineData("~$*", "~$ ~$a ~$aa")]

    // Character Range tests
    [InlineData("[]-]", "] -")]
    [InlineData("[/]", null, "/ a b")]
    [InlineData("[!]-]", "a b c", "] -")]
    [InlineData("[a-]", "a -")]
    [InlineData("[!a-]", "b", "a -")]
    [InlineData("[-a]", "a -")]
    [InlineData("[!-a]", "b", "a -")]
    [InlineData("[--0]", "- . 0", "/")]
    [InlineData("[!--0]", "a b c", "- . 0 /")]
    [InlineData("[!]a-]", "b c d", "] a -")]
    [InlineData(@"[[?*]", @"[ ? *", "a b c")]
    [InlineData("*fil[e-z].txt", "bigfile.txt", "smallfila.txt")]
    [InlineData("*fil[e-z].txt", "smallfilf.txt", "smallfilez.txt")]
    [InlineData("*file[1-9].txt", "bigfile1.txt", "smallfile0.txt")]
    [InlineData("*file[1-9].txt", "smallfile8.txt", "smallfilea.txt")]

    // CharacterList tests
    [InlineData("*file[abc].txt", "bigfilea.txt", "smallfiled.txt")]
    [InlineData("file[]].txt", "file].txt")]
    [InlineData("*file[abc].txt", "smallfileb.txt", "smallfileaa.txt")]
    [InlineData("*file[!abc].txt", "smallfiled.txt", "bigfilea.txt")]
    [InlineData("*file[!abc].txt", "smallfile-.txt", "smallfileaa.txt")]
    [InlineData("*file[!abc].txt", null, "smallfileb.txt")]

    // LiteralSet tests
    [InlineData("a{b,c}d", "abd", "acd")]
    [InlineData("a{,c}d", "acd ad")]
    [InlineData("a{b,}d", "abd ad")]

    // Root tests
    [InlineData("/**/*.sln", "/mnt/e/code/csharp-glob/Glob.sln", "/mnt/e/code/csharp-glob/Glob.Tests/Glob.Tests.csproj")]
    [InlineData(@"C:/**/*.txt", @"C:\Users\Kevin\Desktop\notes.txt", @"C:\Users\Kevin\Downloads\yarn-0.17.6.msi")]

    // Double wildcard tests
    [InlineData("a**/*.cs", "ab/c.cs", "a/b/c.cs")]
    [InlineData("a**/*.cs", "a/c.cs")]
    [InlineData("**a/*.cs", "a/c.cs", "b/a/a.cs")]
    [InlineData("**a/*.cs", "ba/c.cs")]
    [InlineData("b**a/*.cs", "bccca/c.cs bda/a.cs")]
    [InlineData("**", "ba/c.cs")]
    [InlineData("**", "a")]
    [InlineData("**", "a/b")]
    [InlineData("a/**", "a/b/c")]
    [InlineData("a/**", "a/", "a")]
    [InlineData("/**/somefile", "/somefile")]

    // Escape sequences
    [InlineData(@"\[a-d\]", "[a-d]", @"b c \ [ ]")]
    [InlineData(@"\{ab,bc\}", "{ab,bc}", @"ab bc")]
    [InlineData(@"hat\?", "hat?", "hata hatb")]
    [InlineData(@"hat\*", "hat*", "hata hatb hat hat/taco hata/taco")]
    void ParseTest2(string pattern, string? positiveMatch, string? negativeMatch = null)
    {
        var glob = Glob.Parse(pattern);
    }

    [Theory]
    [InlineData("a{b,c}d", "abd", "acd")]
    [InlineData("file.{cs,txt}", "file.cs", "file.txt")]
    [InlineData("{foo,bar}/test", "foo/test", "bar/test")]
    [InlineData("**/file.{cs,ts,js}", "path/to/file.cs", "path/to/file.ts", "path/to/file.js")]
    [InlineData("config.{json,xml,yaml}", "config.json", "config.xml", "config.yaml")]
    [InlineData("test{1,2,3}.txt", "test1.txt", "test2.txt", "test3.txt")]
    public void TestBraceGroupingMatching(string pattern, params string[] testStrings)
    {
        var glob = Glob.Parse(pattern);

        foreach (var testString in testStrings)
        {
            var match = glob.IsMatch(testString);

            Assert.True(match, $"glob {pattern} failed to match test string: {testString}");
        }
    }

    [Theory]
    [InlineData("a{b,c}d", "abd")]
    [InlineData("a{b,c}d", "acd")]
    // TODO: Need to implement more error handling in parser to bubble up bad argument exception.
    //[InlineData("a{,c}d", "acd", "ad")]
    //[InlineData("a{b,}d", "abd", "ad")]
    [InlineData("file.{cs,txt}", "file.cs")]
    [InlineData("file.{cs,txt}", "file.txt")]
    public void TestBraceGroupingIndividualMatches(string pattern, params string[] testStrings)
    {
        var glob = Glob.Parse(pattern);

        foreach (var testString in testStrings)
        {
            var match = glob.IsMatch(testString);

            Assert.True(match, $"glob {pattern} failed to match test string: {testString}");
        }
    }

    [Theory]
    [InlineData("a{b,c}d", "a", "ab", "ac", "ad", "abcd", "aed")]
    [InlineData("file.{cs,txt}", "file.js", "file.cs.txt", "file")]
    [InlineData("{foo,bar}/test", "baz/test", "foobar/test")]
    public void TestBraceGroupingNotMatching(string pattern, params string[] testStrings)
    {
        var glob = Glob.Parse(pattern);

        foreach (var testString in testStrings)
        {
            var match = glob.IsMatch(testString);

            Assert.False(match, $"glob {pattern} incorrectly matched test string: {testString}");
        }
    }

    [Theory]
    [InlineData("a{b,c}d", typeof(LiteralToken), typeof(BraceGroupingToken), typeof(LiteralToken))]
    [InlineData("file.{cs,txt}", typeof(LiteralToken), typeof(BraceGroupingToken))]
    public void TestBraceGroupingTokenParsing(string testString, params Type[] expectedTokens)
    {
        var glob = Glob.Parse(testString);
        var tokens = glob.Tokens;

        Assert.Equal(expectedTokens.Length, tokens.Length);

        for (int i = 0; i < tokens.Length; i++)
        {
            Assert.IsType(expectedTokens[i], tokens[i]);
        }
    }
}
