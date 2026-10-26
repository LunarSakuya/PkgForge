# pkgforge 0.4.2 - Windows x64 release

`pkgforge` is a non-interactive package provisioner. It reads a configured
SQLite-shard index, verifies declared integrity data, materializes one package
plan into an isolated directory, and records managed installations in a portable
program-root manifest. It is not a launcher and does not manage accounts.

This is an independent project. It is not an official Minecraft product and is
not affiliated with, authorized by, sponsored by, endorsed by, or approved by
Mojang AB or Microsoft Corporation. No game files, account credentials,
authentication bypass code, or piracy mechanism are included.

## Files

```text
pkgforge.exe
e_sqlite3.dll
pkgforge.config.example.json
README.md
SECURITY.md
LEGAL.md
PRIVACY.md
NOTICE.md
THIRD-PARTY-NOTICES.md
LICENSE
licenses/
SHA256SUMS.txt
release-manifest.json
```

`pkgforge.exe` is NativeAOT. The .NET runtime is not required. Debug symbols,
build intermediates, tests, and the source tree are not included in this binary
archive. The source code is available from the tagged repository commit.

## Configure

Copy the example beside the executable and replace the placeholder endpoint with
the index root you intend to trust:

```powershell
Copy-Item .\pkgforge.config.example.json .\pkgforge.config.json
```

```json
{
  "schema": 1,
  "indexSource": "https://metadata.example.org/"
}
```

There is no `source` command, `--source` option, environment variable, or
per-user fallback for the index source. A missing or malformed configuration
fails with `E2003`.

## Run

```powershell
.\pkgforge.exe --version
.\pkgforge.exe update
.\pkgforge.exe list --limit 20
.\pkgforge.exe list --kind server --type release
.\pkgforge.exe search 1.2 --kind server
.\pkgforge.exe search linux --kind client --platform linux-x64
.\pkgforge.exe installed --kind server --platform unknown --type release
.\pkgforge.exe install "1.20.1@client:windows-x64" -y
.\pkgforge.exe install "1.20.1@server" --dir "data/instances/MC 1.20.1" -y
.\pkgforge.exe install "1.20.1@server" --dir "..\exports\MC 1.20.1" --unmanaged -y
```

Quote the entire package argument. In PowerShell, `"1.14 Pre-Release
1"@server` uses splatting; write `"1.14 Pre-Release 1@server"` instead.

Client installs include a launcher version JSON at
`versions/<version>/<version>.json`. It is generated from the active index:
main class, libraries, asset index, launch arguments, logging, Java version, and
native rules. Existing client installs made with 0.4.0 or 0.4.1 may have a
missing or path-incorrect file; re-run the same install with `--force` after
upgrading to 0.4.2 to materialize it without removing the program root state.

The extracted folder is the program root. `data/` contains the index, database,
cache, manifests, locks, and managed instances. There is no per-user `.mcpkg`
directory, no LocalAppData install root, and no fallback. If the folder is
read-only, the command fails with `E5001` and names the path; use a writable
folder or set `MCPKG_HOME` to an absolute writable root.

Managed `--dir` values are relative to `data/instances`. An absolute target
requires `--unmanaged` or `--download-only`. Unmanaged targets must be outside the
program root and application state; they create no marker or manifest, do not
appear in `installed`, and cannot be removed by `pkgforge`.

## Filters

`--kind`, `--platform`, and `--type` are accepted by `list`, `search`, and
`installed`. `--type` accepts `all`, `release`, `snapshot`, `unknown`, or any
other type present in the active index. Empty platform and type values are
displayed and filtered as `unknown`; server plans have platform `unknown`.
`--platform` is not accepted by `update`, `install`, or `remove`.

`search` matches version, type, kind, platform, and asset-index text. It is not a
platform query language, so `search linux --kind server` does not mean "Linux
server".

## Proxy

Network commands accept `--proxy URL` or `--no-proxy`. Standard `HTTP_PROXY`,
`HTTPS_PROXY`, `ALL_PROXY`, and `NO_PROXY` variables are honored. Index shards
are verified by size and SHA-256; game files use the size and SHA-1 values
published by the index.

## Licensing and notices

The source and binary are licensed under AGPL-3.0-or-later. See `LICENSE`,
`LEGAL.md`, `NOTICE.md`, `PRIVACY.md`, `THIRD-PARTY-NOTICES.md`, and `licenses/`.
Verify the SHA-256 hashes in `SHA256SUMS.txt` before use.
