namespace Assimalign.Cohesion.Cli.Internal;

internal static class Help
{
    internal const string Text = """
        cohesion — thin wrappers for Cohesion applications
          new <template> [args...] | new --list
          run [--project <csproj|dir>] [-- args...]
          publish [<project>] [args...]
          login --issuer <url> --client-id <id> [--client-secret <s>]
                [--audience <a>] [--scope <s>] [--print]
          --help | --version

        run discovers one Sdk.Gateway project below the current directory and runs it;
        its Program.cs declares the model and the platform it runs on.
        Explicit shell environments bypass launch profiles. Development uses deployed security.
        publish forwards to dotnet publish, discovering the gateway when no project is named.
        login uses IdentityHub device flow (Local + loopback at HEAD).
        Login tokens are stored for a later IdentityHub bridge; gateways do not accept them yet.
        --print emits only the login token to stdout; approval instructions go to stderr.
        Unknown child arguments and arguments after -- retain their order and spelling.
        """;
}
