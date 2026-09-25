namespace Assimalign.Cohesion.Cli.Internal;

internal static class Help
{
    internal const string Text = """
        cohesion — thin wrappers for Cohesion applications
          new <template> [args...] | new --list
          run [--project <csproj|dir>] [--gateway <name>] [-- args...]
          deploy [--project <csproj|dir>] [--gateway <name>] [-- args...]
          publish [<project>] [args...]
          trust issue --developer <name> [--project <csproj|dir>]
          trust add <peer> --from <export-url|file> [--project <csproj|dir>]
          parameter set <name> [<value> | --stdin] | list | remove <name>
          status [--live [--token <jwt>]]
          login --issuer <url> --client-id <id> [--client-secret <s>]
                [--audience <a>] [--scope <s>] [--print]
          --help | --version

        run/deploy/trust discover one Sdk.Gateway project below the current directory.
        Explicit gateway names are passed through; omission preserves gateway defaults.
        run selects environment Local for an unspecified, local or inprocess gateway
        only when no explicit environment argument or shell environment is supplied.
        Explicit shell environments bypass launch profiles. Development uses deployed security.
        deploy adds --mode apply; use deploy -- --mode teardown for another mode.
        parameter/status accept --project, --app and --state-root (default: beside gateway).
        parameter list prints names only; --stdin preserves the full input, including newlines.
        status reports declared endpoints, allocated ports and local process liveness.
        --live needs this application's trust issue --developer token, or COHESION_TOKEN.
        trust add stores the grant in the application's own SecretStore first;
        trusted-issuers.json is a Local-only fallback when that store is unreachable.
        login uses IdentityHub device flow (Local + loopback at HEAD).
        Login tokens are stored for a later IdentityHub bridge; gateways do not accept them yet.
        --print emits only the login token to stdout; approval instructions go to stderr.
        publish --in-container forwards to CohesionPublishImage; the SDK reports route availability.
        trust add --against/--allow waits for item 31's substantive half (#982, O25).
        Resource command dispatch is not available with a developer token.
        --topology single|federated is valid only for cohesion-landing-zone.
        Unknown child arguments and arguments after -- retain their order and spelling.
        """;
}
