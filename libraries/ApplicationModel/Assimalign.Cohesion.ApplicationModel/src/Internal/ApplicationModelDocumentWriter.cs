using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.ApplicationModel;

internal static class ApplicationModelDocumentWriter
{
    public static Task WriteAsync(IApplicationModel model, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        ApplicationModelDocument document = ApplicationModelDocument.Create(model);
        string json = JsonSerializer.Serialize(
            document,
            ApplicationModelDocumentJsonContext.Default.ApplicationModelDocument);

        return Console.Out.WriteLineAsync(json);
    }
}
