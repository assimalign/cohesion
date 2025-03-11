using Assimalign.Cohesion.FileSystem.Globbing;
using Assimalign.Cohesion.FileSystem.Globbing.Tokens;
using Xunit;

namespace Assimalign.Cohesion.FileSystem.Globbing.Tests;

public class FormatterTests
{

    //[Fact]
    //public void Can_Format_Glob_Pattern()
    //{
    //    // we could use the tokeniser here, but deciding to directly
    //    // build the following glob pattern using tokens:
    //    //       /foo?\\*[abc][!1-3].txt

    //    var glob = new Glob([
    //        new PathSeparatorToken(),
    //        new LiteralToken("foo"),
    //        new AnyCharacterToken(),
    //        new PathSeparatorToken(),
    //        new WildcardToken(),
    //        new CharacterSetToken(new char[] { 'a', 'b', 'c' }, false),
    //        new WildcardDirectoryToken(new PathSeparatorToken(), new PathSeparatorToken()),
    //        new RangeToken('1', '3', true),
    //        new LiteralToken(".txt")]);

    //    // now format the glob.
    //    //var sut = new GlobTokenFormatter();
    //    //var globString = sut.Format(glob.Tokens);
    //    Assert.Equal("/foo?\\*[abc]/**/[!1-3].txt", glob.ToString());
    //}
}