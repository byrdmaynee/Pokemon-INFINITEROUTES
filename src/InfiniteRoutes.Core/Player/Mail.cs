using InfiniteRoutes.Core.Battle;

namespace InfiniteRoutes.Core.Player;

/// <summary>Something sent with a letter: money, items or a Pokémon (market purchases, quest rewards, gifts).</summary>
public sealed record MailAttachment(int Money = 0, int Item = 0, int Count = 0, Pokemon? Pokemon = null)
{
    public bool IsEmpty => Money <= 0 && (Item <= 0 || Count <= 0) && Pokemon is null;
}

public sealed class MailMessage
{
    public required int Id { get; init; }
    public required string From { get; init; }
    public required string Subject { get; init; }
    public required string Body { get; init; }
    public required DateTime Sent { get; init; }
    public IReadOnlyList<MailAttachment> Attachments { get; init; } = [];
    public bool Read { get; set; }
    public bool Claimed { get; set; }

    public bool HasUnclaimed => !Claimed && Attachments.Any(a => !a.IsEmpty);
}

/// <summary>
/// The in-game mailbox: system letters, quest rewards and market deliveries arrive here, and attachments are
/// collected from it. Player-to-player mail needs the server (M4); offline only the game writes letters.
/// </summary>
public sealed class Mailbox
{
    public const int MaxBodyLength = 500, MaxSubjectLength = 40;

    readonly List<MailMessage> _messages = new();
    int _nextId = 1;

    /// <summary>Newest first.</summary>
    public IReadOnlyList<MailMessage> Messages => _messages;
    public int Unread => _messages.Count(m => !m.Read);

    public MailMessage Send(string from, string subject, string body, DateTime sent, params MailAttachment[] attachments)
    {
        var message = new MailMessage
        {
            Id = _nextId++, From = from, Sent = sent,
            Subject = subject.Length > MaxSubjectLength ? subject[..MaxSubjectLength] : subject,
            Body = body.Length > MaxBodyLength ? body[..MaxBodyLength] : body,
            Attachments = attachments.Where(a => !a.IsEmpty).ToList(),
        };
        _messages.Insert(0, message);
        return message;
    }

    /// <summary>
    /// Marks the attachments collected and returns them for the caller to add to the Bag, wallet or party.
    /// Empty when there's nothing (left) to collect.
    /// </summary>
    public IReadOnlyList<MailAttachment> Claim(MailMessage message)
    {
        if (!message.HasUnclaimed || !_messages.Contains(message))
            return [];
        message.Claimed = true;
        message.Read = true;
        return message.Attachments;
    }

    /// <summary>Puts saved letters back (newest first, as <see cref="Messages"/> lists them).</summary>
    public void Load(IEnumerable<MailMessage> messages)
    {
        _messages.Clear();
        _messages.AddRange(messages);
        _nextId = _messages.Count == 0 ? 1 : _messages.Max(m => m.Id) + 1;
    }

    /// <summary>Deletes a letter. Letters with uncollected attachments are kept.</summary>
    public bool Delete(MailMessage message) => !message.HasUnclaimed && _messages.Remove(message);
}
