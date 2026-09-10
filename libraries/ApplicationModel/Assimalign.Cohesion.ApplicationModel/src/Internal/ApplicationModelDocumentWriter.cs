using System;
using System.Collections.Generic;
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

    public static Task WriteAsync(
        IReadOnlyList<IApplicationModel> models,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(models);
        cancellationToken.ThrowIfCancellationRequested();

        var documents = new ApplicationModelDocument[models.Count];
        for (int index = 0; index < documents.Length; index++)
        {
            documents[index] = ApplicationModelDocument.Create(models[index]);
        }

        string json = JsonSerializer.Serialize(
            documents,
            ApplicationModelDocumentJsonContext.Default.ApplicationModelDocumentArray);

        return Console.Out.WriteLineAsync(json);
    }
}
