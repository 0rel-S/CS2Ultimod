namespace CS2Ultimod.Core.Database;

public interface IMigration
{
    string Id { get; }
    int Version { get; }
    Task UpAsync(IDatabase db);
}
