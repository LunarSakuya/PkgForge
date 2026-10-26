namespace PkgForge.Cli;

/// <summary>
/// Short, general recovery guidance printed after a failure. Hints deliberately
/// avoid one-off paths or commands; the error line itself may contain the exact
/// path or value that failed.
/// </summary>
internal static class ErrorHints
{
    public static string For(string code) => code switch
    {
        ErrorCodes.Usage =>
            "Check the command name, option spelling, required values, and mutually exclusive " +
            "switches. Quote a value containing spaces as one whole argument; use --name=VALUE " +
            "when a value begins with '-'. Try `pkgforge help` or `pkgforge <command> --help`. " +
            "Examples: `pkgforge list --kind client`; `pkgforge install \"PACKAGE\" -y`.",
        ErrorCodes.IndexMissing =>
            "No verified cached index is available. Run `pkgforge update` first; use --no-update " +
            "only after an index has been verified.",
        ErrorCodes.PackageNotFound =>
            "The package/version was not found. Use `pkgforge list` or `pkgforge search <keyword>` " +
            "to find the exact version, kind, and platform; quote a version containing spaces as " +
            "one argument.",
        ErrorCodes.ManifestMissing or ErrorCodes.MarkerMissing =>
            "No matching program-root installation record or in-folder marker was found. Use " +
            "`pkgforge installed` to inspect records; removal will not guess or force-delete an " +
            "unrecognized directory.",
        ErrorCodes.UnsafePath or ErrorCodes.ProtectedPath =>
            "For a managed install use a relative --dir below data/instances; for --unmanaged use an " +
            "absolute target outside the program root and pkgforge application state. Paths containing " +
            "traversal, symlinks, reserved names, or protected system/application roots are refused.",
        ErrorCodes.InvalidSource =>
            "Check the program-directory pkgforge.config.json. It must contain schema 1 and an " +
            "absolute HTTP(S) indexSource; there is no user-level or command-line source override.",
        ErrorCodes.InvalidProxy =>
            "Use a complete proxy URL with a supported scheme (http, https, socks4, socks4a, socks5) " +
            "and a host/port. Empty values mean unset; `--no-proxy` forces a direct connection.",
        ErrorCodes.Network =>
            "The network request failed. Check connectivity and proxy settings, and retry. For local " +
            "list/search/remove operations, a verified cached index can be used with --no-update.",
        ErrorCodes.Integrity =>
            "A size, hash, database, or manifest verification failed. Retry `pkgforge update`; if " +
            "the failure persists, remove only the affected cache/index entry named in the error.",
        ErrorCodes.InvalidIndex =>
            "The index metadata is invalid or inconsistent. Retry `pkgforge update`; if it persists, " +
            "use a trusted index source and report the source and bundle identifiers.",
        ErrorCodes.LockTimeout =>
            "Another pkgforge process may be using the same state. Wait and retry; stale locks are " +
            "reclaimed by age. Inspect the lock path named in the error if the condition persists.",
        ErrorCodes.Conflict =>
            "A target or installation conflict occurred. Inspect `pkgforge installed`; choose a new " +
            "--dir, or use --force only for a directory already managed by this tool.",
        ErrorCodes.Permission =>
            "The directory named in the error is not writable by this account. Run from a writable " +
            "portable folder (or choose a writable MCPKG_HOME) and prepare its permissions explicitly.",
        ErrorCodes.FileSystem =>
            "The filesystem operation failed. Check free space, path length, sharing/permissions, and " +
            "whether the drive or network filesystem is available.",
        ErrorCodes.DiskSpace =>
            "The destination does not have enough free space for the requested operation and its safety " +
            "margin. Free space or choose a different global/install location; no partial result is kept.",
        ErrorCodes.Cancelled =>
            "The operation was cancelled. Rerun when ready; no partial installation is committed.",
        ErrorCodes.Internal =>
            "An unexpected internal error occurred. Retry; if it persists, report the command and " +
            "the [ERROR] line without credentials or private paths.",
        _ =>
            "Review the reported message and the command's --help output. If this looks like an " +
            "internal defect, report the command and error line without secrets.",
    };
}
