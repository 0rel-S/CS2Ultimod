using Microsoft.Extensions.Logging;

namespace CS2Ultimod.Core.Database;

public static class DatabaseRegistry
{
    // Dictionary par Id : déduplique automatiquement les Register() en cas de hot-reload
    // CSS qui ne libère pas l'assembly (le static list pré-existant accumulait les doublons,
    // ce qui causait un PK conflict sur le 2e INSERT et faisait silently fail tout le loop).
    private static readonly Dictionary<string, IMigration> _migrations = new();

    public static void Register(IMigration migration) => _migrations[migration.Id] = migration;

    public static async Task RunMigrationsAsync(IDatabase db, ILogger logger)
    {
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS core_migrations (
                id      TEXT NOT NULL PRIMARY KEY,
                version INTEGER NOT NULL,
                ran_at  TEXT NOT NULL DEFAULT (datetime('now'))
            )
            """);

        var applied = (await db.QueryAsync<string>("SELECT id FROM core_migrations"))
            .ToHashSet();

        logger.LogInformation("[DB] Migration check: registered=[{Reg}] applied=[{App}]",
            string.Join(",", _migrations.Keys), string.Join(",", applied));

        var pending = _migrations.Values
            .Where(m => !applied.Contains(m.Id))
            .OrderBy(m => m.Version)
            .ToList();

        int success = 0;
        foreach (var m in pending)
        {
            logger.LogInformation("[DB] Running migration {Id} (v{Version})", m.Id, m.Version);
            try
            {
                await m.UpAsync(db);
                await db.ExecuteAsync(
                    "INSERT INTO core_migrations (id, version) VALUES (@Id, @Version)",
                    new { m.Id, m.Version });
                success++;
            }
            catch (Exception ex)
            {
                // On log mais on continue avec les suivantes — sinon une migration qui rate
                // bloque toutes les suivantes (et leur état doit être déterministe).
                logger.LogError(ex, "[DB] Migration {Id} (v{Version}) FAILED", m.Id, m.Version);
            }
        }

        if (pending.Count > 0)
            logger.LogInformation("[DB] {Success}/{Pending} migration(s) applied.", success, pending.Count);
    }
}
