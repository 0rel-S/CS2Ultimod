namespace CS2Ultimod.Features.Admin.Models;

public sealed class CommRecord
{
    public int Id { get; set; }
    public string SteamId { get; set; } = "";
    public string AdminId { get; set; } = "";
    public string Type { get; set; } = "";   // 'gag', 'mute', 'silence'
    public string Reason { get; set; } = "";
    public int Duration { get; set; }
    public string CreatedAt { get; set; } = "";
    public string? ExpiresAt { get; set; }
    public int Removed { get; set; }

    // DB column aliases (snake_case)
    public string steam_id { get => SteamId; set => SteamId = value; }
    public string admin_id { get => AdminId; set => AdminId = value; }
    public string type { get => Type; set => Type = value; }
    public string reason { get => Reason; set => Reason = value; }
    public int duration { get => Duration; set => Duration = value; }
    public string created_at { get => CreatedAt; set => CreatedAt = value; }
    public string? expires_at { get => ExpiresAt; set => ExpiresAt = value; }
    public int removed { get => Removed; set => Removed = value; }
}
