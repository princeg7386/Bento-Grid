using System.Data.Common;
using Microsoft.EntityFrameworkCore.Diagnostics;

namespace CycleGuard.Api.Data;

/// <summary>
/// busy_timeout is a per-connection setting, so it has to be applied every time a
/// connection opens rather than once at startup. Without it, concurrent writers get an
/// immediate SQLITE_BUSY instead of waiting their turn. Connection-string keywords are
/// deliberately not used for this: see docs/WHAT_BROKE.md.
/// </summary>
public sealed class SqlitePragmaInterceptor(int busyTimeoutMs = 30_000) : DbConnectionInterceptor
{
    public override void ConnectionOpened(DbConnection connection, ConnectionEndEventData eventData)
    {
        Apply(connection);
        base.ConnectionOpened(connection, eventData);
    }

    public override async Task ConnectionOpenedAsync(
        DbConnection connection,
        ConnectionEndEventData eventData,
        CancellationToken cancellationToken = default)
    {
        await ApplyAsync(connection, cancellationToken);
        await base.ConnectionOpenedAsync(connection, eventData, cancellationToken);
    }

    private void Apply(DbConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        command.ExecuteNonQuery();
    }

    private async Task ApplyAsync(DbConnection connection, CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = PragmaSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private string PragmaSql =>
        $"""
        PRAGMA busy_timeout = {busyTimeoutMs};
        PRAGMA foreign_keys = ON;
        PRAGMA synchronous = NORMAL;
        """;
}
