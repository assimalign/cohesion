using System.Collections.Generic;

using Assimalign.Cohesion.Database.Documents.Language;

namespace Assimalign.Cohesion.Database.Documents.Internal;

internal sealed partial class DocumentExpressionEvaluator
{
    private static bool IsWindowBinding(DocumentVariableBinding binding)
        => binding.Name == DocumentPlanExecutor.WindowBindingName && binding.Value is Dictionary<OqlWindowExpression, object?>;

    private static object? WindowValue(OqlWindowExpression window, DocumentEvaluationContext document)
    {
        for (var binding = document.Bindings; binding is not null; binding = binding.Parent)
        {
            if (binding.Name == DocumentPlanExecutor.WindowBindingName &&
                binding.Value is Dictionary<OqlWindowExpression, object?> values && values.TryGetValue(window, out var value)) { return value; }
        }
        throw new DocumentPlanningException("CD01044", ErrorMessages.WindowPlacementInvalid, window.Location);
    }
}
