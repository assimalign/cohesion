using System;
using System.Collections.Generic;
using System.Linq;

namespace Assimalign.Cohesion.Database.Graph.Language.Internal;

internal static class GqlMergeRenderer
{
    internal static string Render(GqlMergeStage stage) => "MERGE " + stage.Path + Branch("CREATE", stage.OnCreate) + Branch("MATCH", stage.OnMatch);
    private static string Branch(string name, IReadOnlyList<GqlSetItem> items) => items.Count == 0 ? "" :
        " ON " + name + " SET " + string.Join(", ", items.Select(item => item switch
        {
            GqlSetPropertyItem property => Name(property.Variable) + "." + Name(property.Property) + " = " + GqlPathRenderer.RenderExpression(property.Value),
            GqlSetPropertiesItem map => Name(map.Variable) + (map.Merge ? " += {" : " = {") + string.Join(", ", map.Properties.Select(pair => Name(pair.Key) + ": " + GqlPathRenderer.RenderExpression(pair.Value))) + "}",
            GqlSetLabelsItem labels => Name(labels.Variable) + ":" + string.Join(":", labels.Labels.Select(Name)),
            _ => "<invalid>",
        }));
    private static string Name(string name) => "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
}
