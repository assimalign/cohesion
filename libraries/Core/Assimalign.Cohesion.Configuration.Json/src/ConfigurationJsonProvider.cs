using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Configuration;

internal class ConfigurationJsonProvider : ConfigurationProvider<ConfigurationJsonEntry>
{
    private readonly Stream stream;

    public ConfigurationJsonProvider(Stream stream)
    {
        this.stream = stream;
    }

    public override string Name => throw new NotImplementedException();
}
