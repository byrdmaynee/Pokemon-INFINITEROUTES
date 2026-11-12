namespace InfiniteRoutes.Core.Player;

/// <summary>
/// Role beacons: a small emblem floating over the head of everyone in a role, picked by an Owner for each role in the
/// admin window. The crown is the Owners' alone; the hammer is the moderators' by default; the rest anyone can have.
/// </summary>
public static class Beacons
{
    public const string None = "", Crown = "crown", Hammer = "hammer";

    /// <summary>Every beacon (id, name): the crown, the hammer, then twenty more.</summary>
    public static readonly IReadOnlyList<(string Id, string Name)> All =
    [
        (Crown, "Crown"), (Hammer, "Hammer"), ("shield", "Shield"), ("star", "Star"), ("heart", "Heart"), ("diamond", "Diamond"),
        ("flame", "Flame"), ("bolt", "Lightning bolt"), ("leaf", "Leaf"), ("drop", "Water drop"), ("moon", "Moon"), ("sun", "Sun"),
        ("snow", "Snowflake"), ("note", "Music note"), ("sword", "Sword"), ("wrench", "Wrench"), ("book", "Book"), ("paw", "Paw print"),
        ("ball", "Poké Ball"), ("flag", "Flag"), ("wing", "Wing"), ("clover", "Clover"),
    ];

    public static bool Known(string id) => id == None || All.Any(b => b.Id == id);

    /// <summary>Only Owners may wear the crown.</summary>
    public static bool OwnerOnly(string id) => id == Crown;

    public static string NameOf(string id) => All.FirstOrDefault(b => b.Id == id).Name ?? "None";

    /// <summary>What a new server gives its built-in roles.</summary>
    public static string Default(string role) => role.ToLowerInvariant() switch
    {
        "owner" => Crown,
        "admin" => "shield",
        "gamemaster" => "star",
        "moderator" => Hammer,
        "helper" => "heart",
        _ => None,
    };
}
