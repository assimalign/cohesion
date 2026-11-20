using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace Assimalign.Cohesion.JsonSchema.Internal;

/// <summary>
/// Serves the official meta-schemas embedded in the assembly, so no registry needs the network to
/// resolve them.
/// </summary>
internal sealed class MetaSchemaResolver : IJsonSchemaResolver
{
    private static readonly Dictionary<string, string> _resources = new(StringComparer.Ordinal)
    {
        ["http://json-schema.org/draft-04/schema"] = "MetaSchemas/draft-04/schema.json",
        ["http://json-schema.org/draft-06/schema"] = "MetaSchemas/draft-06/schema.json",
        ["http://json-schema.org/draft-07/schema"] = "MetaSchemas/draft-07/schema.json",
        ["https://json-schema.org/draft/2019-09/schema"] = "MetaSchemas/draft-2019-09/schema.json",
        ["https://json-schema.org/draft/2019-09/meta/applicator"] = "MetaSchemas/draft-2019-09/meta/applicator.json",
        ["https://json-schema.org/draft/2019-09/meta/content"] = "MetaSchemas/draft-2019-09/meta/content.json",
        ["https://json-schema.org/draft/2019-09/meta/core"] = "MetaSchemas/draft-2019-09/meta/core.json",
        ["https://json-schema.org/draft/2019-09/meta/format"] = "MetaSchemas/draft-2019-09/meta/format.json",
        ["https://json-schema.org/draft/2019-09/meta/meta-data"] = "MetaSchemas/draft-2019-09/meta/meta-data.json",
        ["https://json-schema.org/draft/2019-09/meta/validation"] = "MetaSchemas/draft-2019-09/meta/validation.json",
        ["https://json-schema.org/draft/2020-12/schema"] = "MetaSchemas/draft-2020-12/schema.json",
        ["https://json-schema.org/draft/2020-12/meta/applicator"] = "MetaSchemas/draft-2020-12/meta/applicator.json",
        ["https://json-schema.org/draft/2020-12/meta/content"] = "MetaSchemas/draft-2020-12/meta/content.json",
        ["https://json-schema.org/draft/2020-12/meta/core"] = "MetaSchemas/draft-2020-12/meta/core.json",
        ["https://json-schema.org/draft/2020-12/meta/format-annotation"] = "MetaSchemas/draft-2020-12/meta/format-annotation.json",
        ["https://json-schema.org/draft/2020-12/meta/format-assertion"] = "MetaSchemas/draft-2020-12/meta/format-assertion.json",
        ["https://json-schema.org/draft/2020-12/meta/meta-data"] = "MetaSchemas/draft-2020-12/meta/meta-data.json",
        ["https://json-schema.org/draft/2020-12/meta/unevaluated"] = "MetaSchemas/draft-2020-12/meta/unevaluated.json",
        ["https://json-schema.org/draft/2020-12/meta/validation"] = "MetaSchemas/draft-2020-12/meta/validation.json",
    };

    private MetaSchemaResolver()
    {
    }

    public static MetaSchemaResolver Instance { get; } = new();

    public bool TryResolve(Uri uri, out JsonElement schema)
    {
        schema = default;
        if (!_resources.TryGetValue(uri.OriginalString, out string? name))
        {
            return false;
        }

        using Stream? stream = typeof(MetaSchemaResolver).Assembly.GetManifestResourceStream(name);
        if (stream is null)
        {
            return false;
        }

        using JsonDocument document = JsonDocument.Parse(stream);
        schema = document.RootElement.Clone();
        return true;
    }
}
