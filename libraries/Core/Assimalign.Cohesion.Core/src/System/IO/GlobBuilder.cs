using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace System.IO;

//using static System.IO.Glob;

//public class Test
//{
//    public Test()
//    {
//        var glob = GlobBuilder.FromAnySegment()
//            .
//    }
//}

//public sealed class GlobSegmentDescriptor
//{
     
//}

//public sealed class GlobBuilder
//{
//    private readonly StringBuilder _buffer;
//    private bool _built;

//    private GlobBuilder(StringBuilder buffer)
//    {
//        _buffer = buffer;
//    }

//    public GlobSegmentDescriptor 






//    public GlobBuilder OneOf(params char[] characters)
//    {
//        _actions.Add(new CharacterSetToken(characters, false));
//        return this;
//    }
//    public GlobBuilder Literal(string text)
//    {
//        _actions.Add(new LiteralToken(text));
//        return this;
//    }



//    public Glob Build()
//    {
//        string pattern = _buffer.ToString();

//        Glob glob = Glob.Parse(pattern);

//        return glob;
//    }


//    private void EnsureInitializeCheck()
//    {

//    }

//    /// <summary>
//    /// Creates a glob builder to begin a match at the end of a given directory.
//    /// </summary>
//    /// <param name="path">A rooted path</param>
//    /// <returns></returns>
//    public static GlobBuilder FromRootedSegment(FileSystemPath path)
//    {
//        ArgumentException.ThrowIf(!path.HasRoot(), "");

//        return new GlobBuilder(new StringBuilder(path).Append('/'));
//    }

//    /// <summary>
//    /// Creates a Glob Builder to match any string that falls after the given patter '**/{remaining patterm}'
//    /// </summary>
//    /// <returns></returns>
//    public static GlobBuilder FromAnySegment()
//    {
//        return new GlobBuilder(new StringBuilder("**").Append('/'));
//    }
//}


//public class Temp
//{
//    public Temp()
//    {
//        var builder = GlobBuilder.RecurseDirectory()
//            .ThenFindAnyChar()
//            .ThenFindOneOf('a', 'b');
//    }
//}

//internal class GlobBuilder
//{
//    private readonly List<Func<Token>> _actions;

//    public GlobBuilder()
//    {
//       // _actions = new List<Func<GlobToken>>();

//        //_actions = new List<GlobToken>();
//    }

//    public static GlobBuilder RecurseDirectory() => new();
//    public static GlobBuilder FindPath(string text) => new();



//    public GlobBuilder ThenFindAnyChar() => this;
//    public GlobBuilder ThenFindOneOf(params char[] characters) => this;
//    public GlobBuilder ThenFindText(string text) => this;





//    public GlobBuilder DirectoryWildcard()
//    {


//        //     PathSeparatorToken trailingSep = null;
//        //     PathSeparatorToken leadingSep = null;

//        //     if (trailingSeparatorKind == null)
//        //     {
//        //         trailingSep = null;
//        //     }
//        //     else
//        //     {
//        //trailingSep = new PathSeparatorToken(FileSystemPath.Separator);
//        //     }

//        //     if (leadingSeparatorKind == null)
//        //     {
//        //         leadingSep = null;
//        //     }
//        //     else
//        //     {
//        //         switch (leadingSeparatorKind)
//        //         {
//        //             case PathSeparatorKind.BackwardSlash:
//        //                 leadingSep = new PathSeparatorToken('\\');
//        //                 break;
//        //             case PathSeparatorKind.ForwardSlash:
//        //                 leadingSep = new PathSeparatorToken('/');
//        //                 break;
//        //             default:
//        //                 break;
//        //         }
//        //     }

//        //     _tokens.Add(new WildcardDirectoryToken(new , trailingSep));


//        return this;
//    }

//    public GlobBuilder AnyCharacter()
//    {
//        _actions.Add(new AnyCharacterToken());
//        return this;
//    }
//    public GlobBuilder OneOf(params char[] characters)
//    {
//        _actions.Add(new CharacterSetToken(characters, false));
//        return this;
//    }
//    public GlobBuilder Literal(string text)
//    {
//        _actions.Add(new LiteralToken(text));
//        return this;
//    }

//    public GlobBuilder NotOneOf(params char[] characters)
//    {
//        _actions.Add(new CharacterSetToken(characters, true));
//        return this;
//    }

//    public GlobBuilder PathSeparator()
//    {
//        _actions.Add(new PathSeparatorToken());
//        return this;
//    }

//    public GlobBuilder Wildcard()
//    {
//        _actions.Add(new WildcardToken());
//        return this;
//    }

//    public GlobBuilder LetterInRange(char start, char end)
//    {
//        _actions.Add(new LetterRangeToken(start, end, false));
//        return this;
//    }

//    public GlobBuilder LetterNotInRange(char start, char end)
//    {
//        _actions.Add(new LetterRangeToken(start, end, true));
//        return this;
//    }

//    public GlobBuilder NumberInRange(char start, char end)
//    {
//        _actions.Add(new NumberRangeToken(start, end, false));
//        return this;
//    }

//    public GlobBuilder NumberNotInRange(char start, char end)
//    {
//        _actions.Add(new RangeToken(start, end, true));
//        return this;
//    }

//    public Glob Build()
//    {

//        return new Glob(_actions.ToArray());
//    }
//}
