using Godot;
using InfiniteRoutes.Core.Net;
using InfiniteRoutes.Core.Player;

namespace InfiniteRoutes.Client.World;

/// <summary>The Friends, Mail and Messages tabs.</summary>
public sealed partial class PlayerMenu
{
    // ---- Friends ----------------------------------------------------------------------------------------------------

    Control FriendsTabPage()
    {
        _friendName = new LineEdit { PlaceholderText = "Character name", CustomMinimumSize = new Vector2(240, 0) };
        _friendName.TextSubmitted += _ => AddFriend();
        _friendRows = Ui.Ui.VBox(4);
        var page = Ui.Ui.VBox(8,
            Ui.Ui.HBox(8, _friendName, Ui.Ui.Button("Add friend", AddFriend), Ui.Ui.Button("Block", () => Do("block", ("name", _friendName.Text.Trim())))),
            Scroll(_friendRows));
        page.Name = "Friends";
        return page;
    }

    void AddFriend()
    {
        if (_friendName.Text.Trim().Length > 0)
            Do("friend-add", ("name", _friendName.Text.Trim()));
        _friendName.Text = "";
    }

    public void SetFriends(FriendsList list)
    {
        FriendsShown = list;
        Ui.Ui.ClearChildren(_friendRows);
        if (list.Friends.Count == 0)
            _friendRows.AddChild(Ui.Ui.Text("No friends yet: add someone by their character name.", 15, Ui.Ui.Muted));
        foreach (var f in list.Friends)
        {
            var name = f.Name;
            var where = f.State switch
            {
                "online" => $"● online · channel {f.Channel} · {_zoneName(f.Zone)}",
                "offline" => $"offline · last seen {Ago(f.LastSeen)}",
                "sent" => "request sent",
                _ => "wants to be your friend",
            };
            var row = Ui.Ui.HBox(8, Ui.Ui.Text(name, 16, f.State == "online" ? Ui.Ui.Good : null), Wrapped(where, 14, Ui.Ui.Muted));
            if (f.State == "received")
            {
                row.AddChild(Small("Accept", () => Do("friend-accept", ("name", name))));
                row.AddChild(Small("Decline", () => Do("friend-remove", ("name", name))));
            }
            else
            {
                row.AddChild(Small("Message", () => OpenConversation(name)));
                row.AddChild(Small("Mail", () => WriteTo(name)));
                row.AddChild(Small("Trade", () => TradeWith(name)));
                row.AddChild(Small(f.State == "sent" ? "Cancel" : "Remove", () => Do("friend-remove", ("name", name))));
            }
            row.AddChild(Small("Block", () => Do("block", ("name", name))));
            _friendRows.AddChild(row);
        }
        if (list.Blocked.Count > 0)
        {
            _friendRows.AddChild(Ui.Ui.Text("Blocked (no whispers, messages, mail, requests or offers from them)", 15, Ui.Ui.Muted));
            foreach (var name in list.Blocked)
                _friendRows.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text(name, 15), Small("Unblock", () => Do("unblock", ("name", name)))));
        }
    }

    // ---- Mail -------------------------------------------------------------------------------------------------------

    TextEdit _mailBody = null!;
    LineEdit _mailSubject = null!;
    SpinBox _mailMoney = null!, _mailCount = null!;
    OptionButton _mailItem = null!;

    Control MailTabPage()
    {
        _mailList = Ui.Ui.VBox(3);
        var left = Ui.Ui.VBox(6, Ui.Ui.HBox(8, Ui.Ui.Text("Inbox", 17, Ui.Ui.Accent), Ui.Ui.Spacer(), Small("New letter", () => WriteTo(""))), Scroll(_mailList));
        left.CustomMinimumSize = new Vector2(400, 0);
        _letter = Ui.Ui.VBox(8);
        var page = Ui.Ui.HBox(16, left, Scroll(_letter));
        page.Name = "Mail";
        return page;
    }

    long _shownLetter;

    public void SetMail(MailList mail)
    {
        MailShown = mail;
        // The letter on show is redrawn as it is now (collected, read).
        if (_shownLetter != 0 && mail.Letters.FirstOrDefault(l => l.Id == _shownLetter) is { } shown)
            ShowLetter(shown);
        Ui.Ui.ClearChildren(_mailList);
        if (mail.Letters.Count == 0)
            _mailList.AddChild(Ui.Ui.Text("No mail.", 15, Ui.Ui.Muted));
        foreach (var l in mail.Letters)
        {
            var letter = l;
            var attached = !l.Claimed && (l.Money > 0 || l.Item > 0 || l.Pokemon is not null) ? "📎 " : "";
            var b = Small($"{(l.Read ? "" : "● ")}{attached}{l.Subject} — {l.From} · {Ago(l.Sent)}", () => ShowLetter(letter));
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            _mailList.AddChild(b);
        }
    }

    void ShowLetter(LetterRow l)
    {
        _shownLetter = l.Id;
        Ui.Ui.ClearChildren(_letter);
        if (!l.Read)
            Do("mail-read", ("id", l.Id.ToString()));
        _letter.AddChild(Ui.Ui.Text(l.Subject, 19, Ui.Ui.Accent));
        _letter.AddChild(Ui.Ui.Text($"From {l.From} · {l.Sent.ToLocalTime():g}", 14, Ui.Ui.Muted));
        _letter.AddChild(Wrapped(l.Body));
        var id = l.Id.ToString();
        var attached = l.Money > 0 || l.Item > 0 || l.Pokemon is not null;
        if (attached)
            _letter.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text($"Attached: {Goods(l.Money, l.Item, l.Count, l.Pokemon)}{(l.Claimed ? " (collected)" : "")}", 15),
                Small("Collect", () => Do("mail-claim", ("id", id)), !l.Claimed)));
        var buttons = Ui.Ui.HBox(8, Small("Delete", () =>
        {
            Do("mail-delete", ("id", id));
            _shownLetter = 0;
            Ui.Ui.ClearChildren(_letter);
        }, !attached || l.Claimed));
        if (l.From is not (Social.Market or Social.Trade))
            buttons.AddChild(Small("Reply", () => WriteTo(l.From, l.Subject.StartsWith("Re: ", StringComparison.Ordinal) ? l.Subject : $"Re: {l.Subject}")));
        _letter.AddChild(buttons);
    }

    /// <summary>The names the game signs its own letters with (no replying to those).</summary>
    static class Social
    {
        public const string Market = "Global Market", Trade = "Trade Desk";
    }

    /// <summary>Opens a new letter to <paramref name="to"/>.</summary>
    public void WriteTo(string to, string subject = "")
    {
        if (_tabs.CurrentTab != MailTab)
            OpenTab(MailTab);
        _shownLetter = 0;
        Ui.Ui.ClearChildren(_letter);
        var bag = _state()?.Bag ?? [];
        _mailTo = new LineEdit { PlaceholderText = "To (character name)", Text = to, CustomMinimumSize = new Vector2(240, 0) };
        _mailSubject = new LineEdit { PlaceholderText = "Subject", Text = subject, MaxLength = Mailbox.MaxSubjectLength, CustomMinimumSize = new Vector2(360, 0) };
        _mailBody = new TextEdit { CustomMinimumSize = new Vector2(560, 180), WrapMode = TextEdit.LineWrappingMode.Boundary, PlaceholderText = "Your letter (500 characters at most)" };
        _mailMoney = Number(0, Market.MaxPrice, 0, 120);
        _mailItem = new OptionButton { CustomMinimumSize = new Vector2(220, 0) };
        _mailItem.AddItem("(no item)");
        foreach (var b in bag)
            _mailItem.AddItem($"{ItemName(b.Item)} ×{b.Count}");
        _mailCount = Number(1, 999, 1, 80);
        _letter.AddChild(Ui.Ui.Text("New letter", 19, Ui.Ui.Accent));
        _letter.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text("To", 14, Ui.Ui.Muted), _mailTo));
        _letter.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text("Subject", 14, Ui.Ui.Muted), _mailSubject));
        _letter.AddChild(_mailBody);
        _letter.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text("Attach ₽", 14, Ui.Ui.Muted), _mailMoney, _mailItem, Ui.Ui.Text("×", 14), _mailCount));
        _letter.AddChild(Ui.Ui.HBox(8, Ui.Ui.Text("Attached money and items leave your Bag now.", 13, Ui.Ui.Muted), Ui.Ui.Spacer(),
            Small("Send", () =>
            {
                var args = new List<(string, string)> { ("to", _mailTo.Text.Trim()), ("subject", _mailSubject.Text), ("body", _mailBody.Text),
                    ("money", ((int)_mailMoney.Value).ToString()) };
                if (_mailItem.Selected > 0)
                    args.AddRange([("item", bag[_mailItem.Selected - 1].Item.ToString()), ("count", ((int)_mailCount.Value).ToString())]);
                Do("mail-send", [.. args]);
            })));
        (to.Length == 0 ? _mailTo : subject.Length == 0 ? _mailSubject : (Control)_mailBody).GrabFocus();
    }

    // ---- Messages ---------------------------------------------------------------------------------------------------

    Control MessagesTabPage()
    {
        _threads = Ui.Ui.VBox(3);
        _dmTo = new LineEdit { PlaceholderText = "Message someone…", CustomMinimumSize = new Vector2(220, 0) };
        _dmTo.TextSubmitted += name => OpenConversation(name.Trim());
        var left = Ui.Ui.VBox(6, Ui.Ui.HBox(6, _dmTo, Small("Open", () => OpenConversation(_dmTo.Text.Trim()))), Scroll(_threads));
        left.CustomMinimumSize = new Vector2(340, 0);
        _conversation = Ui.Ui.VBox(4);
        _conversationScroll = Scroll(_conversation);
        _dmText = new LineEdit { PlaceholderText = "Write a message (Enter sends)", MaxLength = 300, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _dmText.TextSubmitted += _ => SendDm();
        var right = Ui.Ui.VBox(6, _conversationScroll, Ui.Ui.HBox(6, _dmText, Small("Send", SendDm)));
        right.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var page = Ui.Ui.HBox(16, left, right);
        page.Name = "Messages";
        return page;
    }

    /// <summary>Opens the conversation with <paramref name="name"/> (and the Messages tab).</summary>
    public void OpenConversation(string name)
    {
        if (name.Length == 0)
            return;
        _open = name;
        _dmTo.Text = "";
        Ui.Ui.ClearChildren(_conversation);
        _conversation.AddChild(Ui.Ui.Text(name, 19, Ui.Ui.Accent));
        if (_tabs.CurrentTab != MessagesTab)
            OpenTab(MessagesTab);
        else
            Do("dm-open", ("with", name));
        _dmText.GrabFocus();
    }

    void SendDm()
    {
        var text = _dmText.Text.Trim();
        if (text.Length == 0 || _open.Length == 0)
            return;
        Do("dm-send", ("to", _open), ("text", text));
        _dmText.Text = "";
    }

    public void SetThreads(DmThreads threads)
    {
        Ui.Ui.ClearChildren(_threads);
        if (threads.Threads.Count == 0)
            _threads.AddChild(Ui.Ui.Text("No conversations yet.", 15, Ui.Ui.Muted));
        foreach (var t in threads.Threads)
        {
            var name = t.With;
            var b = Small($"{(t.Unread > 0 ? $"● ({t.Unread}) " : "")}{t.With}: {t.Last}", () => OpenConversation(name));
            b.Alignment = HorizontalAlignment.Left;
            b.ClipText = true;
            _threads.AddChild(b);
        }
    }

    public void SetConversation(DmConversation c)
    {
        if (!string.Equals(c.With, _open, StringComparison.OrdinalIgnoreCase))
            return;
        _open = c.With;
        ConversationShown = c;
        Ui.Ui.ClearChildren(_conversation);
        _conversation.AddChild(Ui.Ui.Text(c.With, 19, Ui.Ui.Accent));
        foreach (var m in c.Messages)
            AddLine(m);
        Do("dm-threads");   // opening it read them
    }

    /// <summary>A message just sent or received: shown in its conversation if that's open.</summary>
    public void AddDm(DmReceived dm)
    {
        if (_tabs.CurrentTab == MessagesTab && string.Equals(dm.With, _open, StringComparison.OrdinalIgnoreCase))
        {
            AddLine(dm.Message);
            ConversationShown = ConversationShown is { } c ? c with { Messages = [.. c.Messages, dm.Message] } : new DmConversation(dm.With, [dm.Message]);
            if (!string.Equals(dm.Message.From, _me, StringComparison.OrdinalIgnoreCase))
                Do("dm-open", ("with", _open));   // marks it read
            else
                Do("dm-threads");
        }
        else if (_tabs.CurrentTab == MessagesTab)
            Do("dm-threads");
    }

    async void AddLine(DmRow m)
    {
        var mine = string.Equals(m.From, _me, StringComparison.OrdinalIgnoreCase);
        var line = Wrapped($"{m.Sent.ToLocalTime():HH:mm}  {m.From}: {m.Text}", 15, mine ? Ui.Ui.Muted : null);
        _conversation.AddChild(line);
        await ToSignal(GetTree(), SceneTree.SignalName.ProcessFrame);
        if (IsInstanceValid(_conversationScroll))
            _conversationScroll.ScrollVertical = (int)_conversationScroll.GetVScrollBar().MaxValue;
    }
}
