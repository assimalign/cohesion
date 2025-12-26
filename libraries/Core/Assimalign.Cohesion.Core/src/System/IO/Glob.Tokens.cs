using System.Diagnostics;
using System.Globalization;
using System.Reflection.Metadata.Ecma335;
using System.Text;
using static System.IO.Glob;

namespace System.IO;

public sealed partial class Glob
{
    [DebuggerDisplay("{Kind} - {ToString()}")]
    internal abstract class TokenBase : Token
    {
        public abstract int ConsumesMinLength { get; }
        public virtual bool ConsumesVariableLength { get; }
        public abstract bool Test(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position,
            out int next);

        public override string ToString()
        {
            var builder = new StringBuilder();

            Format(builder, this);

            return builder.ToString();
        }
    }
    internal abstract class NegatableGlobToken : TokenBase
    {
        public abstract bool IsNegated { get; }
    }
    internal abstract class CompositeGlobToken : TokenBase
    {
        protected CompositeGlobToken(TokenBase[] tokens)
        {
            Tokens = tokens;

            for (int i = 0; i < tokens.Length; i++)
            {
                ConsumesAnyMinLength = ConsumesAnyMinLength + tokens[i].ConsumesMinLength;

                if (!ConsumesAnyVariableLength)
                {
                    if (tokens[i].ConsumesVariableLength)
                    {
                        ConsumesAnyVariableLength = true;
                    }
                }
            }
        }
        public TokenBase[] Tokens { get; }
        public bool ConsumesAnyVariableLength { get;  }
        public int ConsumesAnyMinLength { get; } = 0;
        public bool TestAll(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position, out
            int next)
        {
            next = position;

            if (!ConsumesAnyVariableLength)
            {
                if ((path.Length - position) != ConsumesAnyMinLength)
                {
                    // can't possibly match as tokens require a fixed length and the string length is different.
                    return false;
                }
            }
            else if ((path.Length - position) < ConsumesAnyMinLength)
            {
                // can't possibly match as tokens require a minimum length and the string is too short.
                return false;
            }

            foreach (var token in Tokens)
            {
                if (!token.Test(path, cultureInfo, ignoreCase, next, out next))
                {
                    return false;
                }
            }
            // if all tokens matched but still more text then fail!
            if (next < path.Length - 1)
            {
                return false;
            }
            // Success.
            return true;
        }
    }
    internal class PathSeparatorToken : TokenBase
    {
        public override string Value { get; } = "/";
        public override TokenKind Kind { get; } = TokenKind.PathSeparator;
        public override int ConsumesMinLength => 1;
        public override bool Test(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase, int position, out int next)
        {
            var currentChar = path[position];

            next = position + 1;

            return Lexer.IsPathSeparator(currentChar);
        }
    }
    internal class AnyCharacterToken : TokenBase
    {
        public override string Value { get; } = "?";
        public override TokenKind Kind { get; } = TokenKind.Any;
        public override int ConsumesMinLength => 1;
        public override bool Test(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position, out
            int next)
        {
            next = position + 1;

            var currentChar = path[position];

            if (Lexer.IsPathSeparator(currentChar))
            {
                return false;
            }

            return true;
        }
    }
    internal class CharacterSetToken : NegatableGlobToken
    {
        public CharacterSetToken(char[] characters, bool isNegated)
        {
            Characters = characters;
            IsNegated = isNegated;
            Value = string.Create(characters.Length + (isNegated ? 3 : 2), characters, (span, array) =>
            {
                int start = isNegated ? 2 : 1;

                span[0] = '[';

                if (isNegated)
                {
                    span[1] = '!';
                }

                for (int i = start; i < span.Length - 1; i++)
                {
                    span[i] = array[i - start];
                }

                span[span.Length - 1] = ']';
            });
        }

        public char[] Characters { get; }
        public override string Value { get; }
        public override bool IsNegated { get; }
        public override int ConsumesMinLength => 1;
        public override TokenKind Kind { get; } = TokenKind.CharacterSet;
        public override bool Test(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position, out
            int next)
        {
            var text = cultureInfo.TextInfo;
            var value = path[position];

            next = position + 1;

            bool contains = ContainsMatch(value);

            if (ignoreCase)
            {
                for (int i = 0; i < Characters.Length; i++)
                {
                    if (text.ToUpper(Characters[i]).Equals(text.ToUpper(value)))
                    {
                        contains = true;
                    }
                }
            }
            else
            {
                for (int i = 0; i < Characters.Length; i++)
                {
                    if (Characters[i].Equals(value))
                    {
                        contains = true;
                    }
                }
            }

            if (IsNegated)
            {
                return !contains;
            }
            else
            {
                return contains;
            }
        }

        private bool ContainsMatch(char containsChar)
        {
            for (int i = 0; i < Characters.Length; i++)
            {
                if (Characters[i].Equals(containsChar))
                {
                    return true;
                }
            }

            return false;
        }
    }
    internal class RangeToken : NegatableGlobToken
    {
        public RangeToken(char start, char end, bool isNegated)
        {
            Start = start;
            End = end;
            IsNegated = isNegated;
            Value = string.Create(5 + (isNegated ? 1 : 0), (Start, End), (span, tuple) =>
            {
                span[0] = '[';

                if (isNegated)
                {
                    span[1] = '!';
                    span[2] = tuple.Start;
                    span[3] = '-';
                    span[4] = tuple.End;
                    span[5] = ']';
                }
                else
                {
                    span[1] = tuple.Start;
                    span[2] = '-';
                    span[3] = tuple.End;
                    span[4] = ']';
                }
            });
        }
        public override string Value { get; }
        public char Start { get; }
        public char End { get; }
        public override bool IsNegated { get; }
        public override int ConsumesMinLength => 1;
        public override TokenKind Kind { get; } = TokenKind.Range;
        public override bool Test(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position, out
            int next)
        {
            if (char.IsDigit(Start) && char.IsDigit(End))
            {
                return TestNumberRange(path, cultureInfo, ignoreCase, position, out next);
            }
            else if (ignoreCase)
            {
                return TestCaseInSensitiveLetterRange(path, cultureInfo, position, out next);
            }
            else
            {
                return TestCaseSensitiveLetterRange(path, cultureInfo, position, out next);
            }
        }

        private bool TestNumberRange(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position,
            out int next)
        {
            var currentChar = path[position];

            next = position + 1;

            if (currentChar >= Start && currentChar <= End)
            {
                if (IsNegated)
                {
                    return false;
                }
            }
            else if (!IsNegated)
            {
                return false;
            }

            return true;
        }
        private bool TestCaseSensitiveLetterRange(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            int position,
            out int next)
        {
            next = position + 1;
            char currentChar;
            currentChar = path[position];

            bool isMatch = currentChar >= Start && currentChar <= End;

            if (IsNegated)
            {
                return !isMatch;
            }
            else
            {
                return isMatch;
            }
        }
        private bool TestCaseInSensitiveLetterRange(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            int position,
            out int next)
        {
            next = position + 1;

            TextInfo text = cultureInfo.TextInfo;
            char currentChar;

            currentChar = path[position];

            var lowerStart = text.ToLower(Start);
            var lowerEnd = text.ToLower(End);

            var upperStart = text.ToUpper(Start);
            var upperEnd = text.ToUpper(End);

            bool isMatch = (currentChar >= lowerStart && currentChar <= lowerEnd)
                || (currentChar >= upperStart && currentChar <= upperEnd);

            if (IsNegated)
            {
                return !isMatch;
            }
            else
            {
                return isMatch;
            }
        }
    }
    internal class WildcardToken : CompositeGlobToken
    {
        public WildcardToken(TokenBase[] tokens) : base(tokens)
        {
        }

        public override string Value { get; } = "*";
        public override TokenKind Kind { get; } = TokenKind.Wildcard;
        public override int ConsumesMinLength => ConsumesAnyMinLength;
        public override bool ConsumesVariableLength { get; } = true;
        public override bool Test(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position,
            out int next)
        {
            next = position;

            if (Tokens.Length == 0) // We are the last token in the pattern
            {
                // If we have reached the end of the string, then we match.
                if (position >= path.Length)
                {
                    return true;
                }

                // We don't match if the remaining string has separators.
                for (int i = position; i <= path.Length - 1; i++)
                {
                    var currentChar = path[i];

                    if (currentChar == '/' || currentChar == '\\')
                    {
                        return false;
                    }
                }

                // we have matched up to the new position.
                next = position + path.Length;

                return true;
            }

            // We are not the last token in the pattern, and so the _subEvaluator representing the remaining pattern tokens must also match.
            // Does the sub pattern match a fixed length string, or variable length string?
            if (!ConsumesAnyVariableLength)
            {
                // The remaining tokens match against a fixed length string, so we can infer that this wildcard **must** match
                // a fixed amount of characters in order for the subevaluator to match its fixed amount of characters from the remaining portion
                // of the string. 
                // So we must match up-to that position. We can't match separators. 
                var requiredMatchPosition = path.Length - ConsumesAnyMinLength;
                //if (requiredMatchPosition < currentPosition)
                //{
                //    return false;
                //}
                for (int i = position; i < requiredMatchPosition; i++)
                {
                    var currentChar = path[i];

                    if (currentChar == '/' || currentChar == '\\')
                    {
                        return false;
                    }
                }
                var isMatch = TestAll(path, cultureInfo, ignoreCase, requiredMatchPosition, out next);

                return isMatch;
            }

            // We can match a variable amount of characters but,
            // We can't match more characters than the amount that will take us past the min required length required by the sub evaluator tokens,
            // and as we are not a directory wildcard, we can't match past a path separator.
            var maxPos = path.Length - 1;
            if (ConsumesMinLength > 0)
            {
                maxPos = maxPos - ConsumesMinLength + 1;
            }
            // var maxPos = (allChars.Length - _subEvaluator.ConsumesMinLength);
            for (int i = position; i <= maxPos; i++)
            {

                var isMatch = TestAll(path, cultureInfo, ignoreCase, i, out next);

                if (isMatch)
                {
                    return true;
                }

                var currentChar = path[i];

                if (currentChar == '/' || currentChar == '\\')
                {
                    return false;
                }
            }

            // If subevakuators are optional match then match
            if (ConsumesMinLength == 0)
            {
                return true;
            }

            return false;
        }
    }
    internal class WildcardDirectoryToken : CompositeGlobToken
    {
        public WildcardDirectoryToken(
            PathSeparatorToken leading,
            PathSeparatorToken trailing,
            TokenBase[] tokens) : base(tokens)
        {
            Leading = leading;
            Trailing = trailing;
        }

        public override string Value
        {
            get
            {
                if (Leading is not null && Trailing is not null)
                {
                    return "/**/";
                }
                if (Leading is not null)
                {
                    return "/**";
                }
                if (Trailing is not null)
                {
                    return "**/";
                }
                else
                {
                    return "**";
                }
            }
        }
        public PathSeparatorToken Trailing { get; }
        public PathSeparatorToken Leading { get; }
        public override TokenKind Kind { get; } = TokenKind.WildcardDirectory;
        public override int ConsumesMinLength => ConsumesAnyMinLength;
        public override bool ConsumesVariableLength { get; } = true;
        public override bool Test(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position, out
            int next)
        {
            // We shortcut to success for a ** in some special cases:-
            //  1. The remaining tokens don't need to consume a minimum number of chracters in order to match.

            // We shortcut to failure for a ** in some special cases:-
            // A) The token was parsed with a leading path separator (i.e '/**' and the current charater we are matching from isn't a path separator.

            next = position;
            //  bool matchedLeadingSeperator = false;

            // A) If leading seperater then current character needs to be that seperator.
            if (path.Length <= position || position < 0)
            {
                return false;
            }

            char currentChar = path[position];

            if (Leading is not null)
            {
                if (!Lexer.IsPathSeparator(currentChar))
                {
                    // expected separator.
                    return false;
                }
                //else
                //{
                // advance current position to match the leading separator.
                //  matchedLeadingSeperator = true;
                position = position + 1;
                //}
            }
            else
            {
                // no leading seperator, in which case match an optional leading seperator in string.

                // means ** or possibly **/ used as pattern, not /**             
                //   Input string doesn't need to start with a / or \ but if it does, it will be matched.
                // i.e **/foo/bar will match foo/bar or /foo/bar.
                //     where as /**/foo/bar will not match foo/bar it will only match /foo/bar.
                // currentChar = allChars[currentPosition];
                if (Lexer.IsPathSeparator(currentChar))
                {
                    // advance current position to match the leading separator.
                    // matchedLeadingSeperator = true;
                    position = position + 1;
                }
            }

            // 1. if no more tokens require matching we match.         
            if (ConsumesMinLength == 0)
            {
                next = path.Length;
                return true;
            }

            // Because we know we have more tokens in the pattern (subevaluators) - those will require a minimum amount of characters to match (could be 0 too).
            // We can therefore calculate a "max" character position that we can match to, as if we exceed that position the remaining tokens cant possibly match.
            int maxPos = (path.Length - ConsumesMinLength);

            // Is there enough remaining characters to provide a match, if not exit early.
            if (position > maxPos)
            {
                return false;
            }

            // If all of the remaining tokens have a precise length, we can calculate the exact character that we need to macth to in the string.
            // Otherwise we have to test at multiple character positions until we find a match (less efficient)
            if (!ConsumesAnyVariableLength)
            {
                // Fixed length.
                // As we can only match full segments, make sure character before chacracter at max pos is a separator, 
                if (maxPos > 0)
                {
                    char mustMatchUntilChar = path[maxPos - 1];

                    if (!Lexer.IsPathSeparator(mustMatchUntilChar))
                    {
                        // can only match full segments.
                        return false;
                    }
                }

                // Advance position to max pos.
                position = maxPos;

                return TestAll(path, cultureInfo, ignoreCase, position, out next);
            }
            else
            {
                // Remaining tokens match a variable length of the test string.
                // We iterate each position (within acceptable range) and test at each position.
                bool isMatch;

                currentChar = path[position];
                bool matchedSeperator = false;

                // If the ** token was parsed with a trailing slash - i.e "**/" then we need to match past it before we test remainijng tokens.
                // if input string is /foo we make sure we match the /
                // special exception if **/ is at start of pattern,  then the input string need not have any path separators.
                if (Trailing != null)
                {
                    if (Lexer.IsPathSeparator(currentChar))
                    {
                        // match the separator.
                        position = position + 1;
                    }
                }

                // We may already be at max pos, if so sub evaluators need to match here in the string otherwise we fail.    
                if (position == maxPos)
                {
                    isMatch = TestAll(path, cultureInfo, ignoreCase, position, out next);

                    return isMatch;
                }

                while (position <= maxPos)
                {
                    // Test at current position which is either following a seperator, or at max pos.
                    if (position == maxPos)
                    {
                        // We must have encountered a seperator as we can only match full segments.
                        if (!matchedSeperator)
                        {
                            return false;
                        }
                    }

                    isMatch = TestAll(path, cultureInfo, ignoreCase, position, out next);

                    if (isMatch)
                    {
                        return true;
                    }

                    if (position == maxPos) // didn't match, and can't go any further.
                    {
                        return false;
                    }

                    // Iterate until we hit the next separator or maxPos.
                    matchedSeperator = false;
                    while (position < maxPos)
                    {
                        position = position + 1;
                        currentChar = path[position];

                        if (Lexer.IsPathSeparator(currentChar))
                        {
                            // match the separator.
                            matchedSeperator = true;
                            position = position + 1;
                            break;
                        }
                    }
                }
            }

            return false;
        }
    }
    internal class LiteralToken : TokenBase
    {
        public LiteralToken(string value)
        {
            Value = value;
        }

        public override string Value { get; }
        public override TokenKind Kind { get; } = TokenKind.Literal;
        public override int ConsumesMinLength => Value.Length;
        public override bool ConsumesVariableLength => false;
        public override bool Test(
            ReadOnlySpan<char> path,
            CultureInfo cultureInfo,
            bool ignoreCase,
            int position, out
            int next)
        {
            var text = cultureInfo.TextInfo;

            if (ignoreCase)
            {
                return TestIgnoreCase(path, text, position, out next);
            }
            else
            {
                return Test(path, position, out next);
            }
        }
        private bool Test(ReadOnlySpan<char> path, int position, out int next)
        {
            var counter = 0;

            next = position;

            while (next < path.Length && counter < Value.Length)
            {
                var a = path[next];
                var b = Value[counter];

                if (a != b)
                {
                    return false;
                }

                next = next + 1;
                counter = counter + 1;
            }

            if (counter < Value.Length)
            {
                return false;
            }

            return true;
        }
        private bool TestIgnoreCase(ReadOnlySpan<char> path, TextInfo text, int position, out int next)
        {
            var counter = 0;

            next = position;

            while (next < path.Length && counter < Value.Length)
            {
                var a = text.ToUpper(path[next]);
                var b = text.ToUpper(Value[counter]);

                if (a != b)
                {
                    return false;
                }

                next = next + 1;
                counter = counter + 1;
            }

            if (counter < Value.Length)
            {
                return false;
            }

            return true;
        }
    }

    internal class BraceExpansionToken : CompositeGlobToken
    {
        public BraceExpansionToken(TokenBase[] tokens) : base(tokens)
        {
        }


        public override TokenKind Kind { get; } = TokenKind.BraceGrouping;
        public override int ConsumesMinLength => ConsumesAnyMinLength;
        public override bool ConsumesVariableLength { get; } = true;

        public override string Value => throw new NotImplementedException();

        public override TokenKind Kind => throw new NotImplementedException();

        public override bool Test(ReadOnlySpan<char> path, CultureInfo cultureInfo, bool ignoreCase, int position, out int next)
        {
            throw new NotImplementedException();
        }
    }
}