using System.Globalization;

namespace PkgForge;

public interface ILog
{
    void Info(string message);

    void Warn(string message);

    void Error(string message);

    void Progress(string message);

    void Data(string message);
}

public sealed class ConsoleLog : ILog
{
    private readonly TextWriter _standardOutput;
    private readonly TextWriter _standardError;
    private readonly object _gate = new();

    public ConsoleLog(TextWriter? standardOutput = null, TextWriter? standardError = null)
    {
        _standardOutput = standardOutput ?? Console.Out;
        _standardError = standardError ?? Console.Error;
    }

    public void Info(string message) => Write(_standardError, "INFO", message);

    public void Warn(string message) => Write(_standardError, "WARN", message);

    public void Error(string message) => Write(_standardError, "ERROR", message);

    public void Progress(string message) => Write(_standardError, "INFO", message);

    public void Data(string message)
    {
        lock (_gate)
        {
            _standardOutput.WriteLine(SecureText.ForConsole(message, 64 * 1024));
        }
    }

    private void Write(TextWriter writer, string level, string message)
    {
        var sanitized = SecureText.ForConsole(message);
        lock (_gate)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture, $"[{level}] {sanitized}"));
        }
    }
}

public sealed class NullLog : ILog
{
    public static NullLog Instance { get; } = new();

    public void Info(string message)
    {
    }

    public void Warn(string message)
    {
    }

    public void Error(string message)
    {
    }

    public void Progress(string message)
    {
    }

    public void Data(string message)
    {
    }
}

public static class SizeFormatter
{
    private static readonly string[] Units = ["B", "KB", "MB", "GB", "TB", "PB"];

    public static string Format(long bytes)
    {
        if (bytes < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bytes), "byte count cannot be negative");
        }

        if (bytes < 1024)
        {
            return string.Create(CultureInfo.InvariantCulture, $"{bytes} B");
        }

        var value = (double)bytes;
        var unit = 0;
        while (value >= 1024 && unit < Units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return string.Create(CultureInfo.InvariantCulture, $"{value:0.##} {Units[unit]}");
    }
}
