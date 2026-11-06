# rdap-dotnet-cli

`rdap` is a modern replacement for `whois`. It looks up domain, IP, ASN, nameserver and entity
registration data over [RDAP](https://about.rdap.org/) (RFC 9082/9083), and finds the right
server automatically using the IANA bootstrap registries (RFC 9224).

It ships as a single native executable (.NET Native AOT) for Windows and Linux. No .NET runtime
is required.

```bash
rdap google.com
```

```text
# Source: https://rdap.verisign.com/com/v1/domain/google.com

Domain Name:          GOOGLE.COM
Handle:               2138514_DOMAIN_COM-VRSN
Status:               client delete prohibited
...
Registered:           1997-09-15 04:00:00 UTC
Expires:              2028-09-14 04:00:00 UTC
Name Server:          NS1.GOOGLE.COM
...

Registrar:
  Name:               MarkMonitor Inc.
  IANA Registrar ID:  292

# Referral: https://rdap.markmonitor.com/rdap/domain/GOOGLE.COM
...
```

## Install

Download the archive for your platform from
[Releases](https://github.com/chrismeller/rdap-dotnet-cli/releases), extract `rdap` (or
`rdap.exe`), and put it on your `PATH`.

| Platform | Archive |
| --- | --- |
| Windows x64 | `rdap-<version>-win-x64.zip` |
| Linux x64 (glibc 2.35+) | `rdap-<version>-linux-x64.tar.gz` |
| Linux arm64 (glibc 2.35+) | `rdap-<version>-linux-arm64.tar.gz` |

On Linux, HTTPS needs OpenSSL (`libssl`), which almost every distribution already includes.

## Usage

```text
rdap <query> [options]
```

The query type is detected automatically, the way `whois` does it:

| Input | Treated as |
| --- | --- |
| `example.com`, `bücher.de` | domain (IDNs are converted to punycode) |
| `8.8.8.8`, `2001:db8::1`, `192.0.2.0/24` | IP address or network |
| `AS15169`, `15169` | autonomous system number |
| `ABUSE5250-ARIN` | entity handle (the registry comes from the `-ARIN` object tag) |

| Option | Description |
| --- | --- |
| `-t, --type <type>` | Force the type: `domain`, `nameserver`, `ip`, `autnum`, `entity`. Use `nameserver` for host names such as `ns1.google.com`. |
| `-s, --server <url>` | Query this RDAP base URL instead of bootstrapping, like `whois -h`. Example: `rdap -s https://rdap.arin.net/registry/ GOGL`. |
| `-j, --json` | Print the raw RDAP JSON. When a referral is followed, two JSON documents are printed one after another; `jq` handles that fine. |
| `--no-follow` | Don't follow the registry's referral to the registrar's RDAP server. |
| `-v, --verbose` | Also print notices (terms of service, etc.) and links. |
| `--timeout <s>` | Per-request timeout in seconds (default 15). |
| `--no-color` | Disable color. Color is also off when output is redirected or `NO_COLOR` is set. |
| `--refresh-bootstrap` | Re-download the IANA bootstrap files now. |

### Exit codes

| Code | Meaning |
| --- | --- |
| 0 | Found |
| 1 | Not found (HTTP 404), or no RDAP server exists for the query |
| 2 | Usage error (bad arguments or invalid query) |
| 3 | Network, timeout, rate-limit or server error |

### Bootstrap cache

The IANA bootstrap files are cached for 24 hours in `%LOCALAPPDATA%\rdap\cache` on Windows,
and in `$XDG_CACHE_HOME/rdap` (default `~/.cache/rdap`) elsewhere. If IANA can't be reached,
`rdap` uses a stale cached copy. Failing that, it uses a snapshot embedded in the binary when it
was built.

## Development

You need the [.NET 10 SDK](https://dotnet.microsoft.com/download). Native AOT publishing also
needs the platform toolchain: on Linux, `clang` and `zlib1g-dev`; on Windows, the Visual Studio
"Desktop development with C++" workload.

```bash
dotnet build
```

```bash
dotnet test --project tests/Rdap.Tests
```

```bash
dotnet test --project tests/Rdap.IntegrationTests
```

```bash
dotnet publish src/Rdap.Cli -c Release -r linux-x64 -o out
```

| Project | Purpose |
| --- | --- |
| `src/Rdap.Core` | RDAP client library: models, jCard parsing, IANA bootstrap and caching, query classification. AOT-compatible. |
| `src/Rdap.Cli` | The `rdap` executable: argument parsing (System.CommandLine) and text rendering. |
| `tests/Rdap.Tests` | Offline unit tests against recorded-style fixtures. |
| `tests/Rdap.IntegrationTests` | **Live** tests against real registries (Verisign, PIR, Google, NIC.br, and all five RIRs). Every response must parse without falling back to raw JSON, have parseable event dates and jCards, and render as text. |

The IANA bootstrap snapshot is downloaded at build time into `src/Rdap.Core/Bootstrap/Data/`
(this folder is git-ignored). An offline build still succeeds; it just has no embedded fallback.
CI builds with `-p:RdapRequireBootstrapSnapshot=true`, so a release can never ship without the
snapshot.

### CI

`.github/workflows/ci.yml` runs on every push and pull request, and daily on the default branch:

* unit tests on Linux and Windows
* the live integration tests (transient network errors and 429/5xx responses are retried;
  parse failures are never retried)
* Native AOT publishes for `linux-x64`, `linux-arm64` and `win-x64`, each smoke-tested by running
  the real binary against live servers

The AOT build itself lives in the reusable `.github/workflows/build.yml`.

### Releases

Releases are automated with [release-please](https://github.com/googleapis/release-please)
(`.github/workflows/release.yml`), driven by [Conventional Commits](https://www.conventionalcommits.org/):

* `fix:` bumps the patch version, `feat:` the minor version, and `feat!:` or a `BREAKING CHANGE:`
  footer the major version (while below 1.0.0, breaking changes bump the minor version instead).
  `docs:`, `ci:`, `chore:`, `test:` and similar don't trigger a release on their own.
* On every push to `main`, release-please opens or updates a release PR that bumps the version in
  `Directory.Build.props` and `.release-please-manifest.json` and updates `CHANGELOG.md`.
* Merging the release PR creates the `vX.Y.Z` tag and GitHub release; the workflow then builds the
  three Native AOT archives and attaches them, together with a `SHA256SUMS` file.

To force a specific version, add a `Release-As: 1.2.3` footer to a commit message.

## License

Apache 2.0. See [LICENSE](LICENSE).
