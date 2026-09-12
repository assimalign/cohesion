using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Assimalign.Cohesion.Cli;

internal interface IProcessRunner
{
    Task<int> RunAsync(string executable, IReadOnlyList<string> arguments, string workingDirectory,
        CancellationToken cancellationToken = default);
}
