using Npgsql;

namespace KJWebsite.IntegrationTests;

/// <summary>
/// Skips the test at discovery time unless KJ_TEST_POSTGRES (a PostgreSQL connection string) is set.
/// </summary>
public sealed class RequiresPostgresFactAttribute : FactAttribute
{
    public RequiresPostgresFactAttribute()
    {
        if (TestPostgres.BaseConnectionString is null)
        {
            Skip = "requires KJ_TEST_POSTGRES (e.g. Host=localhost;Username=postgres;Password=postgres)";
        }
    }
}

public static class TestPostgres
{
    public static string? BaseConnectionString => Environment.GetEnvironmentVariable("KJ_TEST_POSTGRES");

    public static string ConnectionStringFor(string database) =>
        new NpgsqlConnectionStringBuilder(BaseConnectionString ?? throw new InvalidOperationException("KJ_TEST_POSTGRES is not set."))
        {
            Database = database
        }.ConnectionString;

    /// <summary>Creates the database if it does not already exist, so a bare local PostgreSQL install works.</summary>
    public static void EnsureDatabaseExists(string database) => EnsureDatabaseExistsAsync(database).GetAwaiter().GetResult();

    /// <summary>Creates a throwaway database with a unique name, for tests that need a guaranteed-empty target.</summary>
    public static async Task<string> CreateUniqueDatabaseAsync(string prefix)
    {
        var name = $"{prefix}_{Guid.NewGuid():N}";
        await using var connection = await OpenMaintenanceConnectionAsync();
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{name}\"", connection);
        await create.ExecuteNonQueryAsync();
        return name;
    }

    public static async Task DropDatabaseAsync(string name)
    {
        NpgsqlConnection.ClearAllPools();
        await using var connection = await OpenMaintenanceConnectionAsync();
        await using var drop = new NpgsqlCommand($"DROP DATABASE IF EXISTS \"{name}\"", connection);
        await drop.ExecuteNonQueryAsync();
    }

    private static async Task EnsureDatabaseExistsAsync(string database)
    {
        await using var connection = await OpenMaintenanceConnectionAsync();
        await using var check = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        check.Parameters.AddWithValue("name", database);
        if (await check.ExecuteScalarAsync() is not null) return;

        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database}\"", connection);
        await create.ExecuteNonQueryAsync();
    }

    private static async Task<NpgsqlConnection> OpenMaintenanceConnectionAsync()
    {
        var maintenance = new NpgsqlConnectionStringBuilder(BaseConnectionString!) { Database = "postgres" };
        var connection = new NpgsqlConnection(maintenance.ConnectionString);
        await connection.OpenAsync();
        return connection;
    }
}
