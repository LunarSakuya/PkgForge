namespace PkgForge;

public enum ExitCode
{
    Success = 0,
    RuntimeFailure = 1,
    Usage = 2,
    NotFound = 3,
    Verification = 4,
    Network = 5,
    Conflict = 6,
    Permission = 7,
    UnsafePath = 8,
    Cancelled = 9,
    Internal = 10,
    LockTimeout = 11,
}

public static class ErrorCodes
{
    public const string Usage = "E1001";
    public const string IndexMissing = "E1002";
    public const string PackageNotFound = "E1003";
    public const string ManifestMissing = "E1004";
    public const string MarkerMissing = "E1005";
    public const string UnsafePath = "E2001";
    public const string ProtectedPath = "E2002";
    public const string InvalidSource = "E2003";
    public const string InvalidProxy = "E2004";
    public const string Network = "E3001";
    public const string Integrity = "E3002";
    public const string InvalidIndex = "E3003";
    public const string LockTimeout = "E4001";
    public const string Conflict = "E4002";
    public const string Permission = "E5001";
    public const string FileSystem = "E5002";
    public const string DiskSpace = "E5003";
    public const string Cancelled = "E6001";
    public const string Internal = "E9001";
}

public sealed class PkgException : Exception
{
    public PkgException(string code, ExitCode exitCode, string message, Exception? inner = null)
        : base(message, inner)
    {
        Code = code;
        ExitCode = exitCode;
    }

    public string Code { get; }

    public ExitCode ExitCode { get; }

    public static PkgException Usage(string message) =>
        new(ErrorCodes.Usage, ExitCode.Usage, message);

    public static PkgException NotFound(string code, string message) =>
        new(code, ExitCode.NotFound, message);

    public static PkgException Unsafe(string message) =>
        new(ErrorCodes.UnsafePath, ExitCode.UnsafePath, message);

    public static PkgException Protected(string message) =>
        new(ErrorCodes.ProtectedPath, ExitCode.UnsafePath, message);

    public static PkgException Integrity(string message, Exception? inner = null) =>
        new(ErrorCodes.Integrity, ExitCode.Verification, message, inner);

    public static PkgException Network(string message, Exception? inner = null) =>
        new(ErrorCodes.Network, ExitCode.Network, message, inner);

    public static PkgException Conflict(string message) =>
        new(ErrorCodes.Conflict, ExitCode.Conflict, message);

    public static PkgException Lock(string message) =>
        new(ErrorCodes.LockTimeout, ExitCode.LockTimeout, message);

    public static PkgException DiskSpace(string message, Exception? inner = null) =>
        new(ErrorCodes.DiskSpace, ExitCode.RuntimeFailure, message, inner);
}
