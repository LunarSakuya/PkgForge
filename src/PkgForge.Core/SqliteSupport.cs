using Microsoft.Data.Sqlite;

namespace PkgForge;

public static class SqliteSupport
{
    public static SqliteConnection OpenReadOnly(string path, string context)
    {
        var fullPath = PathSafety.NormalizeAbsolutePath(path, context);
        if (!File.Exists(fullPath))
        {
            throw PkgException.NotFound(ErrorCodes.IndexMissing, $"{context}: database does not exist: {fullPath}");
        }

        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = fullPath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
            ForeignKeys = true,
        }.ToString();
        var connection = new SqliteConnection(connectionString);
        try
        {
            connection.Open();
            Execute(connection, "PRAGMA temp_store=MEMORY; PRAGMA trusted_schema=OFF; PRAGMA query_only=ON;");
            return connection;
        }
        catch (SqliteException exception)
        {
            connection.Dispose();
            throw PkgException.Integrity($"{context}: cannot open SQLite database: {exception.Message}", exception);
        }
    }

    public static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    public static string ScalarString(SqliteConnection connection, string sql, string context)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            var result = command.ExecuteScalar();
            return Convert.ToString(result, System.Globalization.CultureInfo.InvariantCulture) ?? string.Empty;
        }
        catch (SqliteException exception)
        {
            throw PkgException.Integrity($"{context}: SQLite query failed: {exception.Message}", exception);
        }
    }

    public static long ScalarLong(SqliteConnection connection, string sql, string context)
    {
        var value = ScalarString(connection, sql, context);
        return long.TryParse(value, System.Globalization.NumberStyles.Integer,
            System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : throw PkgException.Integrity($"{context}: SQLite value '{value}' is not an integer.");
    }

    public static bool HasRows(SqliteConnection connection, string sql, string context)
    {
        try
        {
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            using var reader = command.ExecuteReader();
            return reader.Read();
        }
        catch (SqliteException exception)
        {
            throw PkgException.Integrity($"{context}: SQLite query failed: {exception.Message}", exception);
        }
    }
}
