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

        run and publish forward to dotnet run and dotnet publish for the named project, or for the
        project in the current directory when none is named.
        Explicit shell environments bypass launch profiles. Development uses deployed security.
        login uses IdentityHub device flow (Local + loopback at HEAD).
        Login tokens are stored for a later IdentityHub bridge; no service accepts them yet.
        --print emits only the login token to stdout; approval instructions go to stderr.
        Unknown child arguments and arguments after -- retain their order and spelling.
        """;
}
