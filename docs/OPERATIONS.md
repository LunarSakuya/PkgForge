# Operations, trust, and recovery

This is the detailed operating document. `README.md` is the quick-start entry
point; this file is the source of truth for portable-directory trust, normal
requirements, state layout, and dirty-state recovery.

## 1. Normal operating requirements

- Windows x64 is the tested release target; Linux/macOS code paths exist but were
  not runtime-tested in this environment.
- The Windows release is NativeAOT and needs no installed .NET runtime.
- For the default portable layout, the directory containing `pkgforge.exe` must be
  writable by the process. `data/`, `pkgforge.config.json`, manifests, instances,
  cache, and locks are all under that program root.
- If the program root is read-only or unavailable, the command fails with `E5001`
  naming the exact path. There is no user-directory fallback.
- No user profile, `~/.mcpkg`, LocalAppData instance root, or user manifest root is
  used or created.
- Network access is needed for index updates and game-file downloads. HTTP and
  HTTPS URLs are accepted; redirects are followed; proxy settings follow
  `README.md`. Source and index are the trust root; no domain allow-list exists.
- `--no-update` can use an existing verified index offline, but downloads still
  require the network.
- Free disk space is checked before an index update, an install, and each
  individual download; a safety margin is included. Files are reserved with
  `SetLength` and held with `FileShare.None` while being written.
- A failed reservation/transfer is deleted and retried within the bounded network
  limits; it does not become a cache object, shard, staged file, or installation.
- Network operations have a 30 s connect timeout and a generous bounded operation
  timeout/retry budget; lock waits have explicit timeouts.
- A normal user does not need elevation for the portable layout.
- Do not run the program from a directory writable by other users if you rely on
  local cache/index integrity.

## 2. Portable trust boundary

The default global layout is:

```text
<program root>\
  pkgforge.exe
  pkgforge.config.json   local source configuration
  data\
    cache\       verified content objects and temporary downloads
    db\          verified SQLite shard bundles
    index\       active index pointer
    manifests\   managed relative installation manifests
    instances\   default managed relative installation folders
    locks\       cross-process locks
    tmp\         index-update staging
```

`data/` is intentionally next to the executable so the entire portable folder can
be moved with its index, cache, manifests, and managed instances. That also means:

- the executable, `e_sqlite3.dll`, `pkgforge.config.json`, and `data/` are in the
  same local trust boundary;
- anyone who can write that directory can replace the executable, native DLL, or
  source configuration, not merely alter `data/`;
- content files are still re-verified for size/SHA-1 when cached, but local
  metadata can be altered by a process with write access to the program root;
- extract the release into a user-owned folder, not a shared/other-user-writable
  directory;
- no code signing is currently applied, so verify the SHA-256 of the ZIP through a
  separate trusted channel before use.

Managed manifests are `data/manifests/<install-id>.json`; default managed installs
are `data/instances/<install-id>`. Both are relative to the program root and are
not user-profile state. `--dir` for a managed install must be a relative path under
`data/instances`; only explicit `--unmanaged`/`--download-only` can target an
external location, and that target must be outside the program root and pkgforge
application state.

## 3. Selecting a different portable root

Explicit override:

```powershell
$env:MCPKG_HOME = (Resolve-Path ./portable-root).Path
pkgforge update
```

`MCPKG_HOME` must be an absolute path. It becomes the program root; index,
database, cache, manifests, locks, and instances all stay underneath it. It is not
a per-user state location and there is no fallback if it is unavailable.

Priority:

1. `MCPKG_HOME` (explicit absolute portable root)
2. the directory containing `pkgforge.exe`
3. otherwise the exact path in the error must be made writable/replaced

## 4. Dirty states

The program may leave controlled transient state after an interrupted or failed
operation. Do not delete a whole managed directory to "clean" it.

| State | Typical path | Meaning |
| --- | --- | --- |
| Index staging | `<program>\data\tmp\index-update-*` | Partial shard download; older than 24 h is cleaned automatically |
| Cache temporary | `<program>\data\cache\tmp\*.part` | Partial content download; older than 24 h is cleaned automatically |
| Install staging | `<program>\data\instances\.mcpkg-staging\*` | Staged replacement; older than 6 h may be cleaned automatically |
| Install backup | `<program>\data\instances\.mcpkg-backup\*` | Previous folder retained after a failed replacement |
| Removal trash | `<program>\data\instances\.mcpkg-trash\*` | Folder retained after a failed removal |
| Unreferenced bundle | `<program>\data\db\<sha256>` | Bundle not referenced by `index\current.json`; no automatic GC |
| Stale lock | `<program>\data\locks\*.lock` | Lock from a process that did not exit cleanly; dead-owner locks are reclaimed by PID |

Run normal `pkgforge update` / `install` / `remove` first: the application cleans
the age-based temporary states it recognizes. It intentionally does not guess
about arbitrary directories.

## 5. Conservative reset procedure

If normal cleanup is insufficient:

1. Close every running `pkgforge` process.
2. Copy the relevant `pkgforge` console output and the exact paths.
3. **Move**, do not delete, the specific transient directory to a quarantine
   folder outside the managed tree, for example:
   ```text
   data.quarantine-20261025\
   ```
4. Never move/delete `index\current.json`, `db\<active-bundle>`, or
   `data\manifests\*.json` as a "cleanup" step.
5. Re-run:
   ```powershell
   pkgforge update
   pkgforge installed
   ```
6. If only the portable state is corrupt, move the entire `data\` directory aside
   (do not delete it), then run `pkgforge update` again. The index/cache is rebuilt;
   manifests and managed instances are part of the same program root and must be
   reviewed/recovered deliberately, not silently regenerated.
7. If an installation target itself is damaged, use the normal removal flow. Use
   `--force` only when the target still contains a valid pkgforge marker and the
   user intends to remove the entire folder. Do not manually remove an
   unrecognized target as a shortcut.

There is deliberately no automatic `clean --apply` command. Destructive cleanup
is a user decision; the program's own deletion paths remain marker-, parent-, and
path-guarded.

## 6. Permission and path errors

Every failure has:

- an `[ERROR]` line with the exact path/value when useful;
- a `[HINT]` line with general recovery guidance.

`E5001` means the directory named in the error is not writable by this account. It
does not necessarily mean the whole program is unusable:

- for the program root, move the release to a writable portable folder or set
  `MCPKG_HOME` to a writable root;
- for a managed install target, use a relative path under `data/instances`;
- explicit unmanaged targets must be writable and are not managed by pkgforge.

`E5003` means the destination failed the free-space preflight or could not reserve
the requested file bytes. The safety margin is 64 MiB or 5% of the request,
whichever is larger. No partial object is kept; free space and retry.

Paths under Windows, System32, Program Files, and ProgramData are intentionally
rejected as managed/unmanaged targets, including descendants.

## 7. What is intentionally not implemented

- no automatic deletion of unknown directories;
- no privileged elevation;
- no silent fallback from an invalid explicit path;
- no full re-hash of all index shards on every command;
- no automatic code signing or index signing;
- no installer (Inno Setup) in this release.

See `SECURITY.md` and `TEST-MATRIX.md` for the security model and test coverage.
