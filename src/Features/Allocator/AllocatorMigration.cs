using CS2Ultimod.Core.Database;

namespace CS2Ultimod.Features.Allocator;

public sealed class AllocatorMigration : IMigration
{
    public string Id => "allocator_v1_init";
    public int Version => 200;

    public async Task UpAsync(IDatabase db)
    {
        await db.ExecuteAsync("""
            CREATE TABLE IF NOT EXISTS allocator_preferences (
                steam_id   TEXT NOT NULL,
                team       INTEGER NOT NULL,
                alloc_type TEXT NOT NULL,
                weapon     TEXT NOT NULL,
                PRIMARY KEY (steam_id, team, alloc_type)
            )
            """);
    }
}
