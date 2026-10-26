namespace PkgForge.Tests;

internal static class AssertEx
{
    public static void True(bool condition, string message)
    {
        if (!condition)
        {
            throw new InvalidOperationException(message);
        }
    }

    public static void False(bool condition, string message) => True(!condition, message);

    public static void Equal<T>(T expected, T actual, string message)
    {
        if (!EqualityComparer<T>.Default.Equals(expected, actual))
        {
            throw new InvalidOperationException($"{message} Expected: {expected}. Actual: {actual}.");
        }
    }

    public static void Contains(string expected, string actual, string message)
    {
        if (!actual.Contains(expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{message} Missing: {expected}. Actual: {actual}");
        }
    }

    public static TException Throws<TException>(Action action, string message)
        where TException : Exception
    {
        try
        {
            action();
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{message} Expected {typeof(TException).Name}, got {exception.GetType().Name}: {exception.Message}",
                exception);
        }

        throw new InvalidOperationException($"{message} Expected {typeof(TException).Name}, but nothing was thrown.");
    }

    public static async Task<TException> ThrowsAsync<TException>(Func<Task> action, string message)
        where TException : Exception
    {
        try
        {
            await action().ConfigureAwait(false);
        }
        catch (TException exception)
        {
            return exception;
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{message} Expected {typeof(TException).Name}, got {exception.GetType().Name}: {exception.Message}",
                exception);
        }

        throw new InvalidOperationException($"{message} Expected {typeof(TException).Name}, but nothing was thrown.");
    }

    public static void FileExists(string path, string message)
    {
        if (!File.Exists(path))
        {
            throw new InvalidOperationException($"{message} Missing file: {path}");
        }
    }

    public static void FileDoesNotExist(string path, string message)
    {
        if (File.Exists(path))
        {
            throw new InvalidOperationException($"{message} Unexpected file: {path}");
        }
    }

    public static async Task AssertSha1Async(string path, string expected, string message)
    {
        var actual = await Hashing.Sha1FileAsync(path, CancellationToken.None).ConfigureAwait(false);
        Equal(expected, actual, message);
    }
}
