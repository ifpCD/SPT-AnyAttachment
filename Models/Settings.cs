namespace AnyAttachment.Models;

public record Settings
{
    public bool Active { get; init; } = true;
    public required CursedSettings Cursed { get; init; }
    public bool AllowAnyMag { get; init; } = true;
    public bool AllowAnyAmmo { get; init; } = true;
    public required IdSettings IDs { get; init; }
}

public record CursedSettings
{
    public bool Enable { get; init; } = true;
}

public record IdSettings
{
    public required List<string> Blacklist { get; init; }
    public required string Ammo { get; init; }
    public required string Magazine { get; init; }
    public required string Weapon { get; init; }
}

