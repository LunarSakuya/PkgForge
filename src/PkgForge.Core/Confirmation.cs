namespace PkgForge;

public interface IConfirmation
{
    Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken);
}

public sealed class AlwaysConfirm : IConfirmation
{
    public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken) => Task.FromResult(true);
}

public sealed class ConsoleConfirmation : IConfirmation
{
    private readonly TextReader _input;
    private readonly TextWriter _output;
    private readonly bool _assumeYes;

    public ConsoleConfirmation(bool assumeYes, TextReader? input = null, TextWriter? output = null)
    {
        _assumeYes = assumeYes;
        _input = input ?? Console.In;
        _output = output ?? Console.Error;
    }

    public Task<bool> ConfirmAsync(string prompt, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (_assumeYes)
        {
            return Task.FromResult(true);
        }

        if (Console.IsInputRedirected)
        {
            throw new PkgException(ErrorCodes.Cancelled, ExitCode.Cancelled,
                "Confirmation is required but stdin is not interactive. Re-run with -y/--yes after reviewing the plan.");
        }

        _output.Write(prompt + " [y/N] ");
        _output.Flush();
        var answer = _input.ReadLine();
        return Task.FromResult(answer is not null &&
                               (answer.Equals("y", StringComparison.OrdinalIgnoreCase) ||
                                answer.Equals("yes", StringComparison.OrdinalIgnoreCase)));
    }
}

public static class FileSystemErrors
{
    public static PkgException Map(Exception exception, string context)
    {
        return exception switch
        {
            PkgException pkg => pkg,
            UnauthorizedAccessException => new PkgException(ErrorCodes.Permission, ExitCode.Permission,
                $"{context}: access denied. Check directory permissions.", exception),
            PathTooLongException => new PkgException(ErrorCodes.UnsafePath, ExitCode.UnsafePath,
                $"{context}: path is too long for the filesystem.", exception),
            DirectoryNotFoundException => new PkgException(ErrorCodes.FileSystem, ExitCode.RuntimeFailure,
                $"{context}: a directory disappeared or does not exist.", exception),
            FileNotFoundException => new PkgException(ErrorCodes.FileSystem, ExitCode.RuntimeFailure,
                $"{context}: a required file was not found.", exception),
            IOException io => new PkgException(ErrorCodes.FileSystem, ExitCode.RuntimeFailure,
                $"{context}: filesystem or network-filesystem error: {io.Message}", exception),
            Microsoft.Data.Sqlite.SqliteException sqlite => PkgException.Integrity(
                $"{context}: SQLite error: {sqlite.Message}", sqlite),
            _ => new PkgException(ErrorCodes.Internal, ExitCode.Internal,
                $"{context}: unexpected error: {exception.Message}", exception),
        };
    }
}
