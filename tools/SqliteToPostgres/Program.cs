using System.Globalization;
using Microsoft.Data.Sqlite;
using Npgsql;
using NpgsqlTypes;
using SqliteToPostgres;

if (args.Length != 2 || args[0] is not ("auth" or "cta"))
{
    Console.Error.WriteLine("Usage: SqliteToPostgres <auth|cta> <sqlite-backup-path>");
    Console.Error.WriteLine("Set KJ_MIGRATION_PG to the target PostgreSQL connection string.");
    return 2;
}

var sourcePath = Path.GetFullPath(args[1]);
var connectionString = Environment.GetEnvironmentVariable("KJ_MIGRATION_PG");
if (!File.Exists(sourcePath) || string.IsNullOrWhiteSpace(connectionString))
{
    Console.Error.WriteLine("The SQLite backup and KJ_MIGRATION_PG connection string are required.");
    return 2;
}

try
{
    var tables = args[0] == "auth" ? new[] { "users", "refresh_tokens" } : new[] { "cta_submissions" };
    await SqliteDataTransfer.CopyAsync(sourcePath, connectionString, args[0], tables);
    return 0;
}
catch (Exception error)
{
    Console.Error.WriteLine($"Transfer failed: {error.Message}");
    return 1;
}

namespace SqliteToPostgres
{
    public static class SqliteDataTransfer
    {
        public static async Task CopyAsync(string sourcePath, string connectionString, string schema, string[] tables)
        {
            // The schema and table names are fixed by this tool, never taken from a connection or SQLite row.
            if (!((schema == "auth" && tables.SequenceEqual(["users", "refresh_tokens"])) ||
                (schema == "cta" && tables.SequenceEqual(["cta_submissions"]))))
            {
                throw new ArgumentException("Unsupported service or table.");
            }

            var sourceBuilder = new SqliteConnectionStringBuilder { DataSource = sourcePath, Mode = SqliteOpenMode.ReadOnly };
            await using var source = new SqliteConnection(sourceBuilder.ToString());
            await using var target = new NpgsqlConnection(connectionString);
            await source.OpenAsync();
            await target.OpenAsync();
            await using var transaction = await target.BeginTransactionAsync();
            var verifiedCounts = new List<(string Table, int Count)>();

            foreach (var table in tables)
            {
                var qualifiedTable = $"\"{schema}\".\"{table}\"";
                await using (var lockCommand = new NpgsqlCommand($"LOCK TABLE {qualifiedTable} IN ACCESS EXCLUSIVE MODE", target, transaction))
                {
                    await lockCommand.ExecuteNonQueryAsync();
                }
                await using var sourceCommand = source.CreateCommand();
                sourceCommand.CommandText = $"SELECT * FROM \"{table}\"";
                await using var sourceReader = await sourceCommand.ExecuteReaderAsync();
                var columns = Enumerable.Range(0, sourceReader.FieldCount).Select(sourceReader.GetName).ToArray();

                await using (var inspect = new NpgsqlCommand($"SELECT * FROM {qualifiedTable} LIMIT 0", target, transaction))
                await using (var targetReader = await inspect.ExecuteReaderAsync())
                {
                    var targetColumns = Enumerable.Range(0, targetReader.FieldCount).Select(targetReader.GetName).ToArray();
                    if (columns.Length != targetColumns.Length || !columns.ToHashSet(StringComparer.Ordinal).SetEquals(targetColumns))
                    {
                        throw new InvalidOperationException($"Column mismatch for {qualifiedTable}; apply the PostgreSQL baseline first.");
                    }
                }

                if (await CountAsync(target, transaction, qualifiedTable) != 0)
                {
                    throw new InvalidOperationException($"{qualifiedTable} is not empty. Use a fresh migrated target to avoid overwriting data.");
                }

                var names = string.Join(", ", columns.Select(column => $"\"{column}\""));
                var parameters = string.Join(", ", columns.Select((_, index) => $"@p{index}"));
                var copied = 0;
                while (await sourceReader.ReadAsync())
                {
                    await using var insert = new NpgsqlCommand($"INSERT INTO {qualifiedTable} ({names}) VALUES ({parameters})", target, transaction);
                    for (var index = 0; index < columns.Length; index++)
                    {
                        var column = columns[index];
                        var value = sourceReader.IsDBNull(index) ? DBNull.Value : ConvertValue(column, sourceReader.GetValue(index));
                        var parameter = new NpgsqlParameter($"p{index}", value);
                        if (column is "CreatedAt" or "SubmittedAt" or "ExpiresAt" or "RevokedAt")
                        {
                            parameter.NpgsqlDbType = NpgsqlDbType.TimestampTz;
                        }
                        insert.Parameters.Add(parameter);
                    }
                    await insert.ExecuteNonQueryAsync();
                    copied++;
                }

                if (await CountAsync(target, transaction, qualifiedTable) != copied)
                {
                    throw new InvalidOperationException($"Row-count verification failed for {qualifiedTable}.");
                }
                verifiedCounts.Add((qualifiedTable, copied));
            }

            await transaction.CommitAsync();
            foreach (var (table, count) in verifiedCounts)
            {
                Console.WriteLine($"{table}: verified {count} rows");
            }
        }

        private static object ConvertValue(string column, object value)
        {
            if (column is "CreatedAt" or "SubmittedAt" or "ExpiresAt" or "RevokedAt")
            {
                return DateTimeOffset.Parse((string)value, CultureInfo.InvariantCulture).ToUniversalTime();
            }
            return column == "IsMailingAddressSameAsPermanentAddress" ? Convert.ToInt64(value, CultureInfo.InvariantCulture) != 0 : value;
        }

        private static async Task<long> CountAsync(NpgsqlConnection connection, NpgsqlTransaction transaction, string qualifiedTable)
        {
            await using var command = new NpgsqlCommand($"SELECT COUNT(*) FROM {qualifiedTable}", connection, transaction);
            return (long)(await command.ExecuteScalarAsync())!;
        }
    }
}
