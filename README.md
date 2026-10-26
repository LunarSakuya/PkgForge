# pkgforge

`pkgforge` is a non-interactive package provisioner for Minecraft-style
packages. It reads a published SQLite-shard index, verifies declared sizes and
hashes, materializes one package plan into an isolated directory, and records
the result in a portable program-root manifest.

It does not launch the game, manage accounts, or perform version upgrades. Game
files are retrieved only from endpoints referenced by the configured index.

## Status

- Current release: see the repository Releases page
- Version source: [`VERSION`](VERSION)
- Prebuilt target: Windows x64, NativeAOT
- Source: .NET 10, C#
- Regression suite: 83 tests

The source uses portable .NET APIs, but the published binary and the release
scripts are validated on Windows. Linux and macOS builds are not part of this
release.

## Requirements

### Binary

- Windows x64
- A writable directory for the portable program root
- A `pkgforge.config.json` beside `pkgforge.exe`

### Source

- .NET SDK 10.0.100 or newer
- PowerShell 7 for the helper scripts
- No sibling repository is required; schema fixtures are vendored under
  `schemas/`.

## Configuration

The only source configuration is `pkgforge.config.json` beside the executable.
Start from the provided example:

```powershell
Copy-Item .\pkgforge.config.example.json .\pkgforge.config.json
```

Example:

```json
{
  "schema": 1,
  "indexSource": "https://metadata.example.org/"
}
```

Replace the example endpoint with the index root you intend to trust. There is
no `source` command, no `--source` option, no environment-based source override,
and no per-user fallback. A missing or malformed configuration fails closed.

The configured source is trusted for metadata. Index shard bytes are verified by
size and SHA-256. Game files are verified by the size and SHA-1 values published
in the index. HTTP and HTTPS endpoints, including redirects, are supported; host
names are not restricted by the client.

## Commands

```text
pkgforge update  [--proxy URL | --no-proxy]
pkgforge list    [--kind all|client|server] [--platform TOKEN]
                 [--type all|release|snapshot|unknown] [--limit N]
                 [--no-update] [--proxy URL | --no-proxy]
pkgforge search  <keywords...> [--kind ...] [--platform ...]
                 [--type all|release|snapshot|unknown] [--limit N]
                 [--no-update] [--proxy URL | --no-proxy]
pkgforge install <package> [--dir DIR] [--threads 1..32] [-y|--yes] [--force]
                 [--no-update] [--unmanaged|--download-only]
                 [--proxy URL | --no-proxy]
pkgforge remove  <install-id|package-id> [-y|--yes] [--force] [--no-update]
                 [--proxy URL | --no-proxy]
pkgforge installed [--kind all|client|server] [--platform TOKEN]
                    [--type all|release|snapshot|unknown]
```

`update`, `list`, `search`, `install`, and `remove` refresh the index first
unless `--no-update` is used. `installed` reads only the local manifest
directory and never contacts the network.

### Package selectors

```text
1.20.1                       client plan for the current platform
1.20.1@client                client plan for the current platform
1.20.1@client:windows-x64    client plan with an explicit platform
1.20.1@server                server plan
```

### Filters

`--kind` accepts `all`, `client`, or `server`.

`--platform` accepts an index platform token or `unknown`. Server plans have no
platform and are classified as `unknown`. A specific platform filter does not
select server plans.

`--type` accepts `all`, `release`, `snapshot`, `unknown`, or any other type
present in the active index. Empty and legacy type values are normalized to
`unknown`.

## Program root

The directory containing the executable is the program root by default. All
state lives under it:

```text
data/
  index/        active state file
  db/           verified SQLite shard bundles
  cache/        content cache and cache locks
  manifests/    managed installation records
  instances/    managed installations
  locks/        process locks
  tmp/          staging and transient files
```

`MCPKG_HOME` may point to an absolute directory to select a different portable
program root for tests or prepared deployments. It is not a user-profile state
location, and no hidden per-user directory is used.

Managed installations are recorded in `data/manifests/<install-id>.json` and
default to `data/instances/<install-id>`. Client installs also receive a
launcher-readable `versions/<version>/<version>.json` built from the active
index. A managed `--dir` must be a relative path below `data/instances`. An
existing non-empty target requires `--force` and a matching in-folder marker.

`--unmanaged` and `--download-only` are an explicit export boundary. They require
a target outside the program root and outside pkgforge state, create no marker or
manifest, do not appear in `installed`, and are never removed by pkgforge.

## Safety model

- Every index and manifest path is validated before use.
- Managed targets must remain below the controlled instances root.
- Reparse points, path traversal, reserved device names, and nested project
  markers are rejected on managed paths.
- Staging, promotion, backup, and removal use controlled directories and
  exclusive locks.
- Manifest and target changes are rolled back where the filesystem permits it.
- Installation is never a background service and never executes downloaded
  files.
- Network operations use bounded retries and explicit proxy configuration.
- Credentials in proxy URLs and source URLs are not written to logs or persisted
  metadata.

The full security model, residual risks, and verification scope are in
`SECURITY.md` and `docs/ARCHITECTURE.md`.

## Build

```powershell
pwsh ./scripts/build.ps1
pwsh ./scripts/test.ps1
```

AOT publish:

```powershell
pwsh ./scripts/build.ps1 -Aot
```

The AOT output is written under `.artifacts/aot/`. The release archive is built
separately because it also assembles legal notices and license texts.

## Release artifacts

The workflow and required GitHub permissions are documented in
[docs/CI.md](docs/CI.md).

A published GitHub release contains:

- `pkgforge-<version>-win-x64.zip` - executable, native SQLite library, example
  configuration, and runtime notices/licenses
- `release-manifest.json`
- `SHA256SUMS.txt`

The source code is the tagged commit in the repository; GitHub also exposes the
standard auto-generated source archives for that tag. Source archives are not
uploaded as release assets.

The release workflow removes previous releases and `v*` tags before creating the
new release, so the release page always shows only the current build. The
workflow is in `.github/workflows/release.yml`; build outputs, local state, test
artifacts, editor files, and old release archives are excluded from assets.

## Verification

```powershell
pwsh ./scripts/test.ps1
dotnet format PkgForge.slnx --verify-no-changes
```

The regression suite uses a temporary portable program root, a loopback HTTP
fixture server, generated SQLite shards, and the built CLI as a child process.
It also covers AOT execution when the published executable is supplied through
`PKGFORGE_CLI_PATH`.

## License and notices

The source and binary distribution are licensed under AGPL-3.0-or-later. See
`LICENSE`, `NOTICE.md`, `THIRD-PARTY-NOTICES.md`, and `licenses/`.

`LEGAL.md` contains the independent-project and third-party terms statement.
`PRIVACY.md` describes local data handling. `SECURITY.md` describes the security
model and reporting expectations.

This project is not an official Minecraft product and is not affiliated with,
authorized by, sponsored by, endorsed by, or approved by Mojang AB or Microsoft
Corporation. No game files, account credentials, authentication bypass code, or
piracy mechanism are included.
