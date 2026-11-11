# Online test (M2, first slice)

How to run a server on this machine (or your home network) and walk around Unova with other players.

## 1. Run the server

**From Visual Studio:** set **InfiniteRoutes.Server** as the startup project and run (profile *Run server*). With no
`server.pkir` beside it, the server uses `%LOCALAPPDATA%\InfiniteRoutes\server\` — it writes a default
`server.pkir` there on first run, and keeps its database and certificate in `data\` next to it. The data pack is
`auto`: the newest Black/White extraction your game client made (open the client once with your ROM added first).

**From a folder of its own** (e.g. on another machine):

```bash
mkdir my-server && cd my-server
dotnet run --project <repo>/src/InfiniteRoutes.Server -- init    # writes server.pkir here
dotnet run --project <repo>/src/InfiniteRoutes.Server -- check   # checks the config and data pack
dotnet run --project <repo>/src/InfiniteRoutes.Server            # runs it (Ctrl+C stops it and saves everyone)
```

Set `[data] data_pack` to an extraction cache folder if this machine has no client extraction (`ir-extract extract
<rom>` prints the folder). Accounts and characters go in a SQLite file — nothing to install.

On start it prints its **TLS certificate fingerprint**. The certificate is made on first start and kept in
`data/server-cert.pfx`, so the fingerprint stays the same.

- Friends on your network: set `public_address` to this machine's LAN IP and let ports 7450 (game) and 7451
  (accounts) through the firewall.
- PostgreSQL instead of SQLite: `[database] provider = "postgres"` and a connection string in `postgres`
  (use `${ENV:NAME}` for the password).

## 2. Connect

1. Client → **Play Online**. The server list starts with *Local test server* (127.0.0.1:7451); add others under
   **Servers…** (the address is the accounts port, 7451 by default).
2. Type an account name and password and press **Create account** (or **Log in**).
3. The first time, the client shows the server's certificate fingerprint: check it matches what the server printed,
   then **Trust and connect**. It's saved in `servers.pkir`; a different certificate later is refused.
4. Make a character (name, hero or heroine) and press **Play**.

## Chat

Enter (or T) opens the chat line at the bottom left; Esc closes it.

| Type | Goes to |
|---|---|
| `hello` | players near you (same map and channel) |
| `/g hello` | everyone on the server |
| `/w Name hello` | one player (by character name); `/r hello` replies to the last whisper |
| `/ch 2` | moves you to channel 2 |
| `/help` | the list |

Six lines per ten seconds at most (`[chat]` in server.pkir).

## Admin

1. Make the first Owner from the server's command line (works while the server is stopped or running):

   ```bash
   dotnet run --project <repo>/src/InfiniteRoutes.Server -- admin grant <account> owner
   ```

   (or set `[admin] bootstrap_owner = "<account>"` and restart). `admin staff` lists staff, `admin roles` lists the
   roles with their ranks and permissions, `admin audit` shows the log.
2. In the client: Play Online → log in → **Account and two-factor…** → **Set up two-factor**. Add the key to an
   authenticator app (Google/Microsoft Authenticator, 1Password, …) and type its code to turn it on.
3. In the game type `/admin`, then your password and the app's current code. The HUD shows ⚑ ADMIN MODE.
4. `/help` lists what your role can do: `/who`, `/inspect <player>`, `/mute <player> <minutes> <reason>`,
   `/unmute <player>`, `/kick <player> <reason>`, `/announce <text>`, `/tp <player>` or `/tp <zone> <x> <y>`,
   `/summon <player>`, `/spectate <player>` / `/spectate off`, `/invisible`, `/noclip`, `/grant <account> <role>`,
   `/ban <player> <minutes|perm> <reason>`, `/unban <player>`, `/roles`, `/audit`, `/revert <change> [force]`,
   `/exit`. Admin Mode ends after 30 minutes without an admin action.
5. **F9** opens the admin window. Type a **reason** in the box at the top first: every change needs one, and it goes
   in the audit log. Buttons your role can't use are greyed out (the server checks again anyway).
   - **Players:** Invisible/Noclip, an announcement box and Leave Admin Mode; who's online, or **Search everyone**
     by part of a character or account name (offline players too). Open, Go to, Summon, Spectate per player.
   - **Player:** account, role, where they are, mute/ban state. Set their role, ban (minutes or `perm`) or unban,
     mute, kick, go to, summon, spectate. Their game, online or offline: set money, give or take items, heal the
     party, give a Pokémon, release one, and **Edit** a Pokémon (level, nature, shiny, nickname, ability, held item,
     IVs, EVs, moves). The server checks an edited Pokémon could be obtained normally (level-up moves at its level,
     IVs/EVs in range, no hidden ability, a real item); one that couldn't needs the "make Pokémon that couldn't be
     obtained" permission. Pokémon made or edited by staff are marked ★ (only staff see it).
   - **Roles:** each role's rank and permissions. Owners make roles (copying another's permissions or none), tick
     and untick permissions, move a role's rank (1–99) and delete unused ones. Owner and Player are fixed.
   - **Audit:** the latest admin actions; game edits have **Revert**. If the game changed since the edit, it says so
     and offers **Force revert**.
   **Right-click** another player in the world for Open in admin window, Go to, Summon, Spectate and Inspect.
6. Spectating makes you invisible and keeps you with the player — through doors and across the map; the camera
   follows them. You can't walk meanwhile; `/spectate off` (or leaving Admin Mode) returns.

Roles are kept in the database and start as Player 0, Helper 10 (who, mute), Moderator 20 (+ look up players,
kick, ban, read the audit), Game Master 30 (+ announce, teleport, summon, spectate, invisible, noclip, edit items and
Pokémon), Admin 40 (+ grant roles, unobtainable Pokémon, revert) and Owner 100 (everything, always). The numbers are
ranks: you can only act on, and grant, roles ranked below yours (Owners can do anything). Every elevation (and failed
try), every command and every change is in the audit log. Mutes and bans are kept on the account: a banned account
can't log in and is told why and until when.

## Your game on the server

Each character has its own game held by the server: a new one (Snivy and the starting items) when it's made, saved
with your position. X shows the party (make one the lead), the Bag and the trainer card. Saving follows the agreed
plan: map changes and logging out save at once (and so will catches, battles, trades… when they come online), each
of those restarts that player's 30–60 s batch timer, and anything else is saved when it runs out.

## What to check

- [ ] You start in Nuvema Town; the top-left box shows the server, your channel and your character's name.
- [ ] Start a second client (another account) — each sees the other walking, with their name above them, in the
      hero or heroine sprite they chose.
- [ ] Walking through a door or onto an exit mat takes you there; the other player disappears when you go inside
      and reappears when you come out.
- [ ] Walls stop you; if the server disagrees with where you are (it checks every step), you're put back.
- [ ] Quit (Esc) and play again: you're where you left off. Logging in again from another client takes over and the
      first client is told why.
- [ ] Players are put on channel 1 until it's full; you only see players on your channel.
- [ ] Chat: local reaches the other player beside you, `/g` everyone, `/w` just them (and "isn't online" for a
      wrong name); on another channel local chat doesn't reach them.
- [ ] Admin: grant yourself Owner, set up two-factor, `/admin`; `/who` lists players; `/invisible on` makes you
      vanish for the other client (staff in Admin Mode still see you); `/summon`, `/tp`, `/announce` (banner for
      everyone) and `/kick <player> <reason>` work; a normal player's `/who` is refused; `ir-server admin audit`
      shows it all.
- [ ] X shows your party, Bag and trainer card; with two Pokémon, "Make lead" reorders them and it's kept after
      logging out and in.
- [ ] F9 in Admin Mode: open the other player, set their money and give an item (their X menu shows it), edit their
      Pokémon's level, then revert it from the Audit tab. Make a role, tick a permission, grant it to them, and see
      `/help` on their side change after they enter Admin Mode. Ban them: they're disconnected and can't log in.

## Not in this slice

Battles, wild Pokémon, NPC scripts, items and the other menu tabs (quests, mail, market) aren't online yet. Your
offline game is separate and unchanged.
An older server database is upgraded in place on start; its characters get a new game the first time they join.

`--capture-online=<dir> --account=<name> --walk=R3U1 --stay=10` plays a scripted session against the local server
(registering or logging in, trusting the certificate, making a character, walking) with screenshots. `--api-port=`
picks a server on another port; `--say=` runs chat lines and steps such as `panel`, `w:open=<name>`, `w:tab=2`,
`w:reason=…`, `w:press=<button text>`, `w:spin=<n>,<value>` and `w:dump` that drive the admin window.
