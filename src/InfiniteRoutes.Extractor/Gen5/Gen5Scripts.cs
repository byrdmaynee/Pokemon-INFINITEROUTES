using static InfiniteRoutes.Extractor.Nitro3D.NitroDict;

namespace InfiniteRoutes.Extractor.Gen5;

/// <summary>
/// What an NPC says and does when talked to, read from its zone script: the lines on the first-time path,
/// the trainer it battles (gym leaders and other scripted trainers), what it says after losing, the item it hands
/// over, and what it says once that's all done. Lines are pages separated as in <see cref="Text.Gen5Text"/> output.
/// </summary>
public sealed record NpcTalk(IReadOnlyList<string> Lines, int Trainer = -1, IReadOnlyList<string>? Win = null,
    IReadOnlyList<string>? Later = null, int GiftItem = 0, int GiftCount = 0);

/// <summary>
/// Black/White field scripts (<see cref="Archive"/>, the file named by zone header u16 @6). A file starts with u32
/// offsets (relative to the end of each) to its scripts, ended by 0xFD13; script n (from 1) is what an NPC with
/// script n runs. Commands are a u16 opcode and fixed arguments (<see cref="Arguments"/>). Not every opcode's
/// arguments are known yet, so <see cref="Walk"/> stops at an unknown one; about nine NPCs in ten walk to the end.
/// </summary>
public static class Gen5Scripts
{
    public const string Archive = "a/0/5/7";

    public const int End = 0x02, Return = 0x05, Call = 0x04, Jump = 0x1E, If = 0x1F, CallStandard = 0x1C, SetVar = 0x2A,
        Message = 0x3D, MessageAt = 0x3C, Message3 = 0x48, MessageDouble = 0x49, Bubble = 0x38, TrainerBattle = 0x85;

    /// <summary>Opcode → its arguments ('b' u8, 'h' u16, 'w' u32), worked out from the scripts themselves.</summary>
    public static readonly IReadOnlyDictionary<int, string> Arguments = new Dictionary<int, string>
    {
        [0x02] = "", [0x03] = "h", [0x04] = "w", [0x05] = "", [0x08] = "h", [0x09] = "h", [0x0A] = "h", [0x10] = "h",
        [0x11] = "h", [0x13] = "h", [0x19] = "hh", [0x1C] = "h", [0x1E] = "w", [0x1F] = "bw", [0x23] = "h", [0x24] = "h",
        [0x25] = "hh", [0x28] = "hh", [0x2A] = "hh", [0x2E] = "", [0x2F] = "", [0x30] = "", [0x32] = "", [0x34] = "hh",
        [0x36] = "", [0x38] = "hb", [0x39] = "", [0x3C] = "bbhhhh", [0x3D] = "bbhhh", [0x3E] = "", [0x3F] = "",
        [0x47] = "h", [0x48] = "bbhhhhh", [0x49] = "bbhhhhh", [0x4A] = "hb", [0x4B] = "", [0x4C] = "b", [0x4D] = "hb",
        [0x56] = "b", [0x57] = "bh", [0x64] = "hw", [0x65] = "", [0x68] = "hh", [0x6B] = "h", [0x6C] = "h", [0x6D] = "hhhhh",
        [0x6E] = "h", [0x74] = "", [0x78] = "h", [0x85] = "hhh", [0x8C] = "", [0x8D] = "h", [0x8E] = "", [0x97] = "hh",
        [0x98] = "hhhh", [0xA3] = "h", [0xA5] = "hh", [0xA6] = "h", [0xA9] = "h", [0xAA] = "", [0xAB] = "hh", [0xAC] = "",
        [0xAF] = "hhh", [0xB0] = "hhh", [0xB2] = "hhbh", [0xB3] = "hh", [0xB4] = "h", [0xB6] = "hhh", [0xB8] = "hhh",
        [0xCB] = "hh", [0xCC] = "h", [0xCD] = "h", [0xD2] = "h", [0xD5] = "hh", [0xE0] = "h", [0xE1] = "h", [0x102] = "hh",
        [0x103] = "hh", [0x107] = "hhh", [0x10D] = "hh", [0x11E] = "hhh", [0x178] = "h", [0x1A7] = "hh", [0x1BD] = "hh", [0x21E] = "h",
        [0x220] = "hhhh",
    };

    /// <summary>One decoded command: where it starts, its arguments, and where the next one starts.</summary>
    public sealed record Command(int Offset, int Op, int[] Args, int Next)
    {
        /// <summary>Where a jump or branch goes (its u32 is relative to the end of the command).</summary>
        public int Target => Next + Args[^1];
    }

    /// <summary>Where each script starts (index 0 is script 1).</summary>
    public static List<int> Offsets(byte[] file)
    {
        var result = new List<int>();
        for (var o = 0; o + 4 <= file.Length && U16(file, o) != 0xFD13; o += 4)
            result.Add(o + 4 + (int)U32(file, o));
        return result;
    }

    /// <summary>
    /// The commands from <paramref name="start"/> on, following jumps but not branches or calls, until the script
    /// ends, loops back, or reaches an opcode whose arguments aren't known.
    /// </summary>
    public static IEnumerable<Command> Walk(byte[] file, int start)
    {
        var seen = new HashSet<int>();
        var p = start;
        while (p >= 0 && p + 2 <= file.Length && seen.Add(p))
        {
            var op = U16(file, p);
            if (!Arguments.TryGetValue(op, out var layout))
                yield break;
            var args = new int[layout.Length];
            var q = p + 2;
            for (var i = 0; i < layout.Length; i++)
            {
                var size = layout[i] switch { 'b' => 1, 'h' => 2, _ => 4 };
                if (q + size > file.Length)
                    yield break;
                args[i] = size switch { 1 => file[q], 2 => U16(file, q), _ => (int)U32(file, q) };
                q += size;
            }
            var command = new Command(p, op, args, q);
            yield return command;
            if (op is End or Return)
                yield break;
            p = op == Jump ? command.Target : q;
        }
    }

    /// <summary>The message a command shows, or -1.</summary>
    public static int MessageOf(Command c) => c.Op switch
    {
        Message or MessageAt or Message3 or MessageDouble => c.Args[2],
        Bubble => c.Args[0],
        _ => -1,
    };

    /// <summary>
    /// What script <paramref name="script"/> (from 1) of <paramref name="file"/> makes an NPC say and do, with
    /// messages from the zone's <paramref name="text"/>; null when it shows nothing we can read.
    /// </summary>
    public static NpcTalk? Talk(byte[] file, List<int> offsets, int script, IReadOnlyList<string> text)
    {
        if (script < 1 || script > offsets.Count)
            return null;
        // The first check before anything is said usually asks "done this already?". One side is the first time
        // (a battle or a gift, when there is one), the other what the NPC says from then on.
        var first = Follow(file, offsets[script - 1], text);
        Path main = first;
        Path? later = first.FirstBranch is { } branch ? Follow(file, branch, text) : first;
        var end = offsets.Where(o => o > offsets[script - 1]).DefaultIfEmpty(file.Length).Min();
        if (!first.DoesSomething && (Explore(file, offsets[script - 1], text) ?? FindBattle(file, offsets[script - 1], end, text)) is { } action)
            (main, later) = (action, first);
        else if (first.Lines.Count == 0)
            (main, later) = (later, first);
        if (ReferenceEquals(main, later))
            later = null;
        if (main.Lines.Count == 0 && !main.DoesSomething)
            return null;
        return new NpcTalk(main.Lines, main.Trainer, main.Win.Count > 0 ? main.Win : null,
            later is { Lines.Count: > 0 } ? later.Lines : null, main.GiftItem, main.GiftCount);
    }

    /// <summary>The standard script that hands the player item <c>0x8000</c> × <c>0x8001</c>.</summary>
    public const int GiveItem = 10110;

    sealed record Path(List<string> Lines, List<string> Win, int Trainer, int GiftItem, int GiftCount, int? FirstBranch)
    {
        public bool DoesSomething => Trainer >= 0 || GiftItem > 0;
    }

    /// <summary>The first path through the script's branches (a few levels deep) that battles or gives something.</summary>
    static Path? Explore(byte[] file, int start, IReadOnlyList<string> text)
    {
        var queue = new Queue<int>([start]);
        var seen = new HashSet<int> { start };
        for (var tries = 0; queue.Count > 0 && tries < 24; tries++)
        {
            var at = queue.Dequeue();
            var path = Follow(file, at, text);
            if (path.DoesSomething)
                return path;
            foreach (var c in Walk(file, at).Where(c => c.Op == If))
                if (seen.Add(c.Target))
                    queue.Enqueue(c.Target);
        }
        return null;
    }

    /// <summary>
    /// A battle reached in ways <see cref="Walk"/> can't follow yet: "message, close it, battle" anywhere in the script's
    /// bytes, then whatever follows the battle.
    /// </summary>
    static Path? FindBattle(byte[] file, int start, int end, IReadOnlyList<string> text)
    {
        for (var o = start; o + 16 <= Math.Min(end, file.Length); o++)
        {
            if (U16(file, o) != Message || U16(file, o + 10) != 0x3E || U16(file, o + 12) != TrainerBattle)
                continue;
            var after = Follow(file, o + 12, text);
            var msg = U16(file, o + 4);
            List<string> lines = msg < text.Count && !string.IsNullOrWhiteSpace(text[msg]) ? [text[msg]] : [];
            return after with { Lines = lines, FirstBranch = null };
        }
        return null;
    }

    static Path Follow(byte[] file, int start, IReadOnlyList<string> text)
    {
        List<string> lines = [], win = [];
        int trainer = -1, giftItem = 0, giftCount = 0;
        int? item = null, count = null, branch = null;
        foreach (var c in Walk(file, start))
        {
            if (MessageOf(c) is var m and >= 0)
            {
                if (m < text.Count && !string.IsNullOrWhiteSpace(text[m]))
                    (trainer >= 0 ? win : lines).Add(text[m]);
                continue;
            }
            switch (c.Op)
            {
                case If when branch is null && lines.Count == 0 && trainer < 0:
                    branch = c.Target;
                    break;
                case TrainerBattle when trainer < 0:
                    trainer = c.Args[0];
                    break;
                case SetVar when c.Args[0] == 0x8000:
                    item = c.Args[1];
                    break;
                case SetVar when c.Args[0] == 0x8001:
                    count = c.Args[1];
                    break;
                case CallStandard when c.Args[0] == GiveItem && item is > 0 and < 1000 && giftItem == 0:
                    (giftItem, giftCount) = (item.Value, Math.Max(1, count ?? 1));
                    break;
            }
        }
        return new Path(lines, win, trainer, giftItem, giftCount, branch);
    }
}
