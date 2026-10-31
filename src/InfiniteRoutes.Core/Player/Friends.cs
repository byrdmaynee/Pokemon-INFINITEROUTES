namespace InfiniteRoutes.Core.Player;

public enum FriendState { Online, Away, Offline, RequestSent, RequestReceived }

/// <summary>Someone on the friends list, with where they are (BUILD_SPEC §7.6: online state, region, channel).</summary>
public sealed record Friend(string Name, FriendState State, string Region = "", int Channel = 0, string Location = "",
    DateTime? LastSeen = null, string Note = "")
{
    public bool IsFriend => State is FriendState.Online or FriendState.Away or FriendState.Offline;
}

/// <summary>
/// Friends and the ignore list. Requests and presence come from the server (M4); offline, requests stay pending.
/// Names follow the character-name rules: 3–16 letters, digits, spaces, '-' or '_'.
/// </summary>
public sealed class FriendList
{
    public const int MaxFriends = 100;

    readonly List<Friend> _friends = new();
    readonly SortedSet<string> _ignored = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Online first, then away, offline and pending; by name within each.</summary>
    public IEnumerable<Friend> Friends => _friends.OrderBy(f => f.State).ThenBy(f => f.Name, StringComparer.OrdinalIgnoreCase);
    public IReadOnlyCollection<string> Ignored => _ignored;
    public int OnlineCount => _friends.Count(f => f.State is FriendState.Online or FriendState.Away);

    public static bool ValidName(string name) =>
        name.Length is >= 3 and <= 16 && name.All(c => char.IsLetterOrDigit(c) || c is ' ' or '-' or '_') && name.Trim() == name;

    Friend? Find(string name) => _friends.FirstOrDefault(f => string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Adds a friend outright (the server's view of an accepted request, or offline sample data).</summary>
    public void Set(Friend friend)
    {
        if (Find(friend.Name) is { } old)
            _friends.Remove(old);
        _friends.Add(friend);
    }

    /// <summary>Sends a friend request. Returns why it can't be sent, or null when it was.</summary>
    public string? Request(string name, string self)
    {
        name = name.Trim();
        if (!ValidName(name))
            return "Names are 3–16 letters, digits, spaces, '-' or '_'.";
        if (string.Equals(name, self, StringComparison.OrdinalIgnoreCase))
            return "You can't add yourself.";
        if (Find(name) is { } existing)
            return existing.IsFriend ? $"{existing.Name} is already your friend." : $"A request with {existing.Name} is already pending.";
        if (_friends.Count >= MaxFriends)
            return $"Your friends list is full ({MaxFriends}).";
        _ignored.Remove(name);
        _friends.Add(new Friend(name, FriendState.RequestSent));
        return null;
    }

    public bool Accept(string name)
    {
        if (Find(name) is not { State: FriendState.RequestReceived } f)
            return false;
        _friends.Remove(f);
        _friends.Add(f with { State = FriendState.Offline });
        return true;
    }

    public bool Remove(string name) => Find(name) is { } f && _friends.Remove(f);

    /// <summary>Ignores someone: removes them as a friend and hides their whispers, mail and requests.</summary>
    public void Ignore(string name)
    {
        Remove(name);
        _ignored.Add(name.Trim());
    }

    public bool Unignore(string name) => _ignored.Remove(name);
    public bool IsIgnored(string name) => _ignored.Contains(name);
}
