using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Benchmarks.Utilities;

internal class RandomGlobGenerator
{
    private readonly Glob _glob;
    private readonly StringBuilder _builder;
    private readonly Random _random;

    public RandomGlobGenerator(Glob glob)
    {
        _glob = glob;
        _builder = new StringBuilder();
        _random = new Random();
    }

    private void Clear() => _builder.Clear();

    public string GetRandomMatch()
    {
        Clear();
        AppendMatchingComposite();
        return _builder.ToString();
    }

    public string GetRandomNoneMatch()
    {
        Clear();
        return _builder.ToString();
    }

    private void AppendMatchingComposite()
    {
        foreach (var token in _glob.Tokens)
        {
            switch(token.Kind)
            {

            }
        }
    }



    private void AppendAnyCharacterToken()
    {
        if (_random.Next(0, 1) == 0)
        {
            AppendRandomLiteralChar();
        }
        else
        {
            _builder.Append('\\');
        }
    }

    private void AppendRandomLiteralChar()
    {
        switch (_random.Next(0, 3))
        {
            case 0:
                AppendRandomCharacterBetween('a', 'z');
                break;
            case 1:
                AppendRandomCharacterBetween('A', 'Z');
                break;
            case 2:
                AppendRandomCharacterBetween('0', '9');
                break;
            case 3:
                AppendRandomCharacterBetween('-', '.');
                break;
            default:
                throw new InvalidOperationException();
                // break;
        }
    }

    public void AppendRandomCharacterBetween(char start, char end)
    {
        _builder.Append((char)_random.Next((int)start, (int)end));
    }
}
