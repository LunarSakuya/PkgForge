using Microsoft.Data.Sqlite;

namespace PkgForge;

public static class IndexDatabaseValidator
{
    public static Task ValidateShardAsync(string path, IndexShard shard, IndexCounts counts,
        CancellationToken cancellationToken) =>
        Task.Run(() => ValidateShard(path, shard, counts, cancellationToken), cancellationToken);

    public static void ValidateShard(string path, IndexShard shard, IndexCounts counts,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var context = $"index shard '{shard.Id}'";
        using var connection = SqliteSupport.OpenReadOnly(path, context);
        if (SqliteSupport.ScalarLong(connection, "PRAGMA application_id;", context) != IndexContract.ApplicationId)
        {
            throw PkgException.Integrity($"{context}: application_id does not match the Relay index contract.");
        }

        if (SqliteSupport.ScalarLong(connection, "PRAGMA user_version;", context) != IndexContract.DatabaseSchema)
        {
            throw PkgException.Integrity($"{context}: user_version does not match the manifest.");
        }

        var quickCheck = SqliteSupport.ScalarString(connection, "PRAGMA quick_check;", context);
        if (!string.Equals(quickCheck, "ok", StringComparison.Ordinal))
        {
            throw PkgException.Integrity($"{context}: quick_check failed: {quickCheck}");
        }

        if (SqliteSupport.HasRows(connection, "PRAGMA foreign_key_check;", context))
        {
            throw PkgException.Integrity($"{context}: foreign_key_check failed.");
        }

        var storedShard = SqliteSupport.ScalarString(
            connection, "SELECT value FROM meta WHERE key='shard';", context);
        if (!string.Equals(storedShard, shard.Id, StringComparison.Ordinal))
        {
            throw PkgException.Integrity($"{context}: meta.shard is '{storedShard}', expected '{shard.Id}'.");
        }

        var databaseSchema = SqliteSupport.ScalarString(
            connection, "SELECT value FROM meta WHERE key='database_schema';", context);
        if (!string.Equals(databaseSchema, IndexContract.DatabaseSchema.ToString(
                System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal))
        {
            throw PkgException.Integrity($"{context}: meta.database_schema is not {IndexContract.DatabaseSchema}.");
        }

        switch (shard.Id)
        {
            case "catalog":
                CheckCount(connection, "SELECT COUNT(*) FROM versions;", counts.Versions,
                    "versions", context);
                CheckCount(connection, "SELECT COUNT(*) FROM plans WHERE kind='client';", counts.ClientPlans,
                    "client plans", context);
                CheckCount(connection, "SELECT COUNT(*) FROM plans WHERE kind='server';", counts.ServerPlans,
                    "server plans", context);
                break;
            case "assets":
                CheckCount(connection, "SELECT COUNT(*) FROM asset_docs;", counts.AssetDocs,
                    "asset documents", context);
                break;
            case "launch":
                _ = SqliteSupport.ScalarLong(connection, "SELECT COUNT(*) FROM plan_classpath;", context);
                _ = SqliteSupport.ScalarLong(connection, "SELECT COUNT(*) FROM plan_directories;", context);
                break;
            default:
                throw PkgException.Integrity($"{context}: unexpected shard id.");
        }

        cancellationToken.ThrowIfCancellationRequested();
    }

    private static void CheckCount(SqliteConnection connection, string sql, int expected, string label,
        string context)
    {
        var actual = SqliteSupport.ScalarLong(connection, sql, context);
        if (actual != expected)
        {
            throw PkgException.Integrity(
                $"{context}: {label} count {actual} does not match manifest count {expected}.");
        }
    }
}
