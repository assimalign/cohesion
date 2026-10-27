using System.Globalization;

using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

public sealed partial class GqlQueryParser
{
    private GqlPathSelector ParsePathSelector(out GqlPathMode? mode)
    {
        var start = Current;
        bool shortest = Take("SHORTEST");
        bool all = !shortest && Take("ALL");
        if (!shortest && !all) { Expect("ANY"); }
        bool qualified = !shortest && Take("SHORTEST");
        long count = 1;
        if (shortest)
        {
            var number = Current;
            if (number.Type != TokenType.Integer || !long.TryParse(number.Text, NumberStyles.None, CultureInfo.InvariantCulture, out count) || count <= 0)
            { Error("CG00011", ErrorMessages.SelectorCountInvalid, number); }
            else { Advance(); }
        }
        else if (!all && !qualified && Current.Type is TokenType.Integer or TokenType.Parameter)
        {
            // Counted ANY is outside #1734's approved selector surface.
            TryPathSearchPrefix(_position - 1, out string construct, out int last);
            UnsupportedConstruct(construct, start, _tokens[last]);
        }
        mode = null;
        if (!Failed && TryPathModeAt(_position, out var explicitMode)) { mode = explicitMode; Advance(); }
        if (!Failed && (Is("PATH") || Is("PATHS"))) { Advance(); }
        bool groups = !Failed && shortest && (Take("GROUP") || Take("GROUPS"));
        var kind = shortest ? groups ? GqlPathSelectorKind.ShortestGroups : GqlPathSelectorKind.Shortest
            : qualified ? all ? GqlPathSelectorKind.AllShortest : GqlPathSelectorKind.AnyShortest
            : all ? GqlPathSelectorKind.All : GqlPathSelectorKind.Any;
        return new GqlPathSelector(kind, count, Span(start, Previous));
    }
}
