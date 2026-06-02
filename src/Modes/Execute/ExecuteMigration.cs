using CS2Ultimod.Core.Database;

namespace CS2Ultimod.Modes.Execute;

/// <summary>
/// DB migration for the Execute mode.
/// Creates execute_round_stats table for tracking per-round outcomes.
/// Version range: 200–299.
/// </summary>
public sealed class ExecuteMigration : IMigration
{
    public string Id      => "execute_v1_init";
    public int    Version => 200;

    public async Task UpAsync(IDatabase db)
    {
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS execute_round_stats (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                map         TEXT    NOT NULL,
                scenario    TEXT    NOT NULL,
                winner_team INTEGER NOT NULL,  -- CsTeam int value
                played_at   TEXT    NOT NULL DEFAULT (datetime('now'))
            )
            """);

        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS execute_player_stats (
                id          INTEGER PRIMARY KEY AUTOINCREMENT,
                steam_id    TEXT    NOT NULL,
                player_name TEXT    NOT NULL,
                map         TEXT    NOT NULL,
                kills       INTEGER NOT NULL DEFAULT 0,
                deaths      INTEGER NOT NULL DEFAULT 0,
                played_at   TEXT    NOT NULL DEFAULT (datetime('now'))
            )
            """);
    }
}
