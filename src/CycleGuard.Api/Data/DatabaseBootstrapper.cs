using Microsoft.EntityFrameworkCore;

namespace CycleGuard.Api.Data;

/// <summary>
/// Creates the schema and puts the database into WAL mode. WAL is a persistent property of
/// the database file, so it is set once at startup on a throwaway connection rather than on
/// every connection.
/// </summary>
public static class DatabaseBootstrapper
{
    public static async Task InitialiseAsync(CycleGuardDbContext dbContext, CancellationToken cancellationToken = default)
    {
        await dbContext.Database.EnsureCreatedAsync(cancellationToken);

        var connection = dbContext.Database.GetDbConnection();
        if (connection.State != System.Data.ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken);
        }

        await using var command = connection.CreateCommand();

        // Write-ahead logging lets readers run while a writer holds the write lock, which is
        // what makes 8 concurrent workers plus a polling dashboard survive on one file.
        command.CommandText = "PRAGMA journal_mode = WAL;";
        var mode = await command.ExecuteScalarAsync(cancellationToken);

        if (mode is string result && !result.Equals("wal", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Expected SQLite journal_mode 'wal' but the database reported '{result}'.");
        }
    }
}
