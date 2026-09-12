using System;

namespace Assimalign.Cohesion.Cli;

internal sealed class CliException(string message) : Exception(message);
