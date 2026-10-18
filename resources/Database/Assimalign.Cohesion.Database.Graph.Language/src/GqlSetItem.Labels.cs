using System.Collections.Generic;
using Assimalign.Cohesion.Database.Language;

namespace Assimalign.Cohesion.Database.Graph.Language;

/// <summary>Adds labels to a node.</summary>
/// <param name="Variable">The node variable.</param><param name="Labels">The labels.</param>
/// <param name="IsColon">Whether colon spelling was used.</param><param name="Location">The source span.</param>
public sealed record GqlSetLabelsItem(string Variable, IReadOnlyList<string> Labels, bool IsColon = true, Location? Location = null) : GqlSetItem(Variable, Location);
