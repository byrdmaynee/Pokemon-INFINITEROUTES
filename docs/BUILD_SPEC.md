# Pokémon: Infinite Routes — Base Build Spec

| | |
|---|---|
| Version | 0.3 — decisions resolved (§18), server config `.pkir` + deployment (§4.5) |
| Date | 2026-10-30 |
| Scope | The "base game": a working online MMO that gives players the full set of standard Pokémon-MMO features, built on assets each player supplies from their own Nintendo DS cartridges/dumps |
| Status | Approved base design — decisions recorded in §18 |

---

## 1. Product summary

Infinite Routes is a client/server online game. The client is our own engine: it does **not** emulate the DS. At first launch the player points the game at their own DS ROM files, and the client pulls graphics, maps, music, and text out of them into a local cache. The server never receives, stores, or hands out ROM data. It runs the game logic (movement, encounters, battles, economy) using a data pack the server operator builds from their own copies.

All regions are joined into one persistent world. Players see each other on the map. They can chat, trade, battle, form guilds, and use a player market. Battles use Generation 5 rules and presentation in every region.

### 1.1 Design pillars
1. **Bring your own game.** We ship no Nintendo assets. If a game isn't supplied, the content that needs it is locked, and the UI says clearly what's missing.
2. **The server is the only authority.** The client renders and sends inputs. Every roll (encounters, IVs, shinies, damage, drops) happens on the server.
3. **One world, many regions.** Each region keeps its own story progress, but the character, Pokémon, bag, and money are shared everywhere.
4. **Everything goes over the wire as an ID.** The server sends species, move, item, and map IDs. Each client turns them into names using text from its own ROM, so players with different ROM languages can share a world.
5. **Build for live ops.** Events, balance changes, and new content are data/script changes, not client patches, wherever possible.

---

## 2. Supported games & ROM requirements

### 2.1 Game table

| Game | Gen | Region served | Role | Required? |
|---|---|---|---|---|
| Black **or** White | 5 | Unova | Hosts the battle system (UI, all 649 battle sprites, animations, battle music) **and** the Unova overworld | **Yes, always** |
| Platinum | 4 | Sinnoh | Canonical Sinnoh overworld, story, music | Needed for Sinnoh |
| HeartGold **or** SoulSilver | 4 | Johto + Kanto | Canonical Johto/Kanto overworld, story, music | Needed for Johto/Kanto |
| Black 2 **or** White 2 | 5 | Unova (2 yrs later) | Extra assets now; "Unova B2W2" region expansion later | Optional |
| Diamond / Pearl | 4 | Sinnoh (original) | Recognised and validated. Saved for later content (e.g. a "Classic Sinnoh" instance) | Optional |

Notes:
- **Why Black/White is mandatory:** every battle in every region uses the Gen 5 battle presentation, and only Gen 5 ROMs have sprites for all 649 species. Without it the game can't run a battle.
- **Why Platinum and not D/P for Sinnoh:** Platinum changed maps, events, and encounter tables (Distortion World, expanded Sinnoh dex). The server's world has to match one canonical layout, and Platinum is the superset.
- **Paired versions** (HG/SS, B/W, B2/W2): either one is accepted. Version-exclusive Pokémon live in the server's encounter data, not the ROM, so both versions give identical gameplay. Areas that exist in only one version (Black City vs White Forest) are **phased per player**: each player sees the area from the version they supplied. The server tracks both, and their encounters are merged into one table.

### 2.2 ROM validation
1. Read the NDS header: game title (0x00), game code (0x0C, 4 chars, last char = region), revision (0x1E).
2. Compute SHA-1 of the whole file.
3. Match against `rom_manifest.json`, a list of known-good dumps (No-Intro DAT hashes) with the extractor profile for each.
4. Results: `Ready` · `Valid – needs extraction` · `Recognised game, unknown dump` (bad dump, hack, or trimmed) · `Unsupported region/language` · `Not a supported game`.
5. Base launch supports **USA/EUR English** dumps. Other languages are planned. The extractor is keyed by (game code, revision), so adding a language means adding a profile, not changing code.
6. ROM hacks are rejected in the base game. Hacked ROMs break the assumptions about asset offsets and IDs.

Accepted inputs: `.nds`, and `.nds` inside a `.zip` (`.7z` planned). Archives are read in memory, never extracted to disk unasked.

### 2.3 ROM settings (player-facing)

**First-run wizard.** Welcome → legal notice ("you must own the games you supply") → ROM Library → extraction progress → login.

**Settings → ROM Library** has one row per game:
- Status icon and text (as in §2.2), file path, SHA-1 (shortened), extractor version.
- Buttons: **Browse…**, **Re-validate**, **Re-extract**, **Remove**.
- **Scan folder…** finds and assigns every supported ROM in a folder.
- If a required ROM is missing, its region shows as locked in the world map and travel UI, with a "Supply ROM" link back to this screen.

**Path changes:** the new file is validated first. If its SHA-1 matches the cached one, the cache is reused. If it doesn't match, the client re-extracts. The old cache is kept until the new extraction finishes.

**Storage:** paths go in `%APPDATA%/InfiniteRoutes/config/roms.pkir` (OS equivalents on Linux/macOS; set `IR_HOME` to keep config and cache in one folder). The cache goes in `%LOCALAPPDATA%/InfiniteRoutes/cache/<gameCode>/<sha1>/<extractorVersion>/`.

---

## 3. Asset extraction pipeline (client)

Extraction runs locally, in the background, and can resume. The output is engine-native formats, so the game never parses DS formats at runtime.

| DS source | Format(s) | Converted to |
|---|---|---|
| Filesystem | NitroFS (FNT/FAT), NARC archives, LZ77/LZ10/LZ11 compression | Virtual file tree |
| 2D graphics | NCGR (tiles), NCLR (palettes), NSCR (screens), NCER/NANR (cells/animations) | PNG atlases + animation JSON |
| Battle sprites (Gen 5) | Animated multi-part NCER/NANR/NMCR/NMAR | Sprite atlas + per-part animation rig JSON |
| Overworld maps | NSBMD (models), NSBTX (textures), NSBCA/NSBTA/NSBTP (anims), map matrix + permissions/collision blocks | glTF 2.0 per map chunk + tile metadata JSON |
| Overworld sprites | NCGR/NCLR sheets for characters/Pokémon followers | PNG atlases |
| Music/SFX | SDAT: SSEQ + SBNK + SWAR (sequenced), STRM (streamed) | **Base game:** pre-rendered OGG with loop points. Later: a runtime sequenced synth for dynamic music |
| Text | Message archives (encrypted in both Gen 4 and Gen 5) | UTF-8 JSON keyed by (archive, line); control codes mapped to markup tokens |
| Fonts | NFTR | Bitmap font atlases |

Rules:
- Each converter is a separate, versioned module. Bumping `extractorVersion` invalidates only the caches it affects.
- **Integrity:** after extraction the client builds a hash of the *gameplay-relevant* data it read (species/move/item ID tables, map ID list). At login this hash is checked against the server's data pack. A mismatch blocks entry with a clear error. It doesn't make anything secure (the server is authoritative anyway); it catches wrong or broken dumps early.
- **Client mods:** a `mods/` folder may override cached assets (UI themes, sprite/texture packs, font swaps). Mods are cosmetic and client-only. The server never sees them.
- **Privacy/legal:** nothing extracted is uploaded, shared peer-to-peer, or put in crash reports. Crash reports remove cache paths.

---

## 4. Technical architecture

### 4.1 Stack (recommended)

| Layer | Choice | Why |
|---|---|---|
| Shared core | C# / .NET 10 LTS class library (`InfiniteRoutes.Core`) | Battle engine, data models, and protocol written once and used by both client and server |
| Client | Godot 4.x (.NET build) | Real 3D renderer (Gen 4/5 overworlds are 3D), strong UI tooling, ships to Windows/Linux/macOS, MIT licence |
| Server | .NET 10, modular monolith at first | Same language as the core. Modules split out into services when load needs it |
| Networking | TCP + TLS, length-prefixed MessagePack frames, versioned opcodes. HTTPS REST for auth, market browsing, profiles | Turn-based, grid-moving game: TCP latency is fine and simpler than UDP |
| Database | PostgreSQL 16+ | Transactions for trades and the market. JSONB for flexible Pokémon metadata |
| Cache/pubsub | Redis (or Valkey) | Sessions, presence, cross-shard chat, matchmaking queues |
| Server scripting | Lua 5.x via MoonSharp/NLua | Story events, NPCs, live events — hot-reloadable |
| Admin | In-game admin GUI (§11) + small web panel (ASP.NET Core + any SPA), both on one server-side Admin API | Full control in-game; web access when not in the game |
| Observability | OpenTelemetry → Prometheus/Grafana + Loki | — |

### 4.2 Server modules

```
                    ┌──────────────┐
  Client ──HTTPS──▶ │ Auth / Account│──▶ PostgreSQL
     │              └──────────────┘
     │ TCP+TLS (session token)
     ▼
┌──────────────────────────────────────────────────────────┐
│ Gateway  (connection, framing, rate limits, routing)     │
├──────────────┬──────────────┬─────────────┬──────────────┤
│ World        │ Battle       │ Social      │ Economy      │
│ - shards/    │ - engine     │ - chat      │ - NPC shops  │
│   channels   │   instances  │ - friends   │ - market     │
│ - movement   │ - PvE / PvP  │ - parties   │ - trades     │
│ - AoI        │ - replays    │ - guilds    │ - ledger     │
│ - scripts    │              │             │              │
├──────────────┴──────────────┴─────────────┴──────────────┤
│ Matchmaking · Events/LiveOps · Moderation · Telemetry    │
└──────────────────────────────────────────────────────────┘
           │ PostgreSQL (state)   │ Redis (presence/pubsub)
```

- **World shards:** one per region. Each shard has **channels** (instances of the same world) with a cap on players (default 250) so maps don't get crowded. Players can switch channel freely, except in battle or in a trade.
- **Area of interest:** a player receives entities on the current map plus maps directly connected to it. Interiors are their own map.
- **Phasing:** NPCs and objects that depend on story flags are filtered per player. A player who hasn't beaten the gym leader still sees the leader standing in the gym, even if others have beaten them.
- **Persistence:** character state is written behind every 30 s and on exit. **Trades, market purchases, Pokémon creation/release, and currency changes are written synchronously in one DB transaction** and recorded in an append-only ledger.

### 4.3 Movement & netcode
- Movement is on a tile grid, as in the original games. The client sends step intents (`dir`, `mode`: walk/run/bike/surf, `seq`) and predicts them locally.
- The server checks each step against the map permission/collision data from its data pack, along with speed limits, field abilities, and story gates. If a step is rejected, the client snaps back to the last acknowledged tile.
- The server broadcasts position updates at 10 Hz to everyone in the AoI. Other players are interpolated.
- Encounter rolls happen on the server when it accepts a step onto an encounter tile.

### 4.4 Server data pack
The operator runs the **same extractor in headless mode** against their own ROMs. That produces the *gameplay* data: species stats, learnsets, evolutions, moves, items, trainers, encounter tables, map connections, warps, collision, and event placements. On top of that sits an **overrides layer**: versioned JSON/YAML patches checked into the repo (MMO balance, hordes, rematches, new items/events). **The overrides are the authority.** The data pack holds no graphics, audio, or text.

### 4.5 Server configuration (`.pkir`) & deployment

All deployment settings and default game rules for a server live in one human-editable config file, **`server.pkir`**. A `.pkir` file uses TOML syntax with our own extension, so it gets syntax highlighting in editors that support TOML and a schema for checking it. The server checks it at startup and refuses to start with a clear list of errors if anything is wrong.

```toml
# server.pkir
[server]
name           = "Infinite Routes — Home Test"
bind_address   = "0.0.0.0"
game_port      = 7450            # TCP+TLS game traffic
api_port       = 7451            # HTTPS auth/market/admin API
public_address = "192.168.1.50"  # what clients are told to connect to (LAN IP now, VPS IP/domain later)
max_players    = 200

[tls]
mode      = "self-signed"        # self-signed | files | acme (Let's Encrypt, for VPS with a domain)
cert_file = ""
key_file  = ""

[database]
postgres = "Host=localhost;Port=5432;Database=ir;Username=ir;Password=${ENV:IR_DB_PASSWORD}"
redis    = "localhost:6379"

[data]
data_pack   = "./datapack"       # built by the headless extractor
overrides   = "./data/overrides"
scripts     = "./data/scripts"
live_layer  = "./data/live"      # admin live edits (§11.3)

[rules]
shiny_rate          = 8192       # 1/N base
exp_multiplier      = 1.0
money_multiplier    = 1.0
breeding_mode       = "economy"  # economy | classic
iv_ev_visible       = true
region_unlock_badges = 8
character_slots_free = 1
day_length_minutes  = 60

[admin]
bootstrap_owner = ""             # account name granted Owner on first start, then ignored
require_2fa     = true
elevation_timeout_minutes = 30
ip_allowlist    = []             # applies to Admin/Owner

[features]                       # feature flags (§7.9)
global_market = true
ranked_pvp    = true
```

- **Secrets** are never written into the file in plain text. Use `${ENV:NAME}` or `${FILE:path}` references.
- **Which rules win:** repo overrides (`data/overrides`) < `server.pkir [rules]` < admin live layer (`data/live`). Admin changes made in-game are written to the live layer, not back into the `.pkir`.
- **Hot reload:** `[rules]` and `[features]` reload on file save or with `/admin reload config`. Network, TLS, and database settings need a restart; the server warns when a change needs one.
- **Client side:** players add servers in **Settings → Servers** (name + address:port), or use **Direct connect**. The server list is saved in the client's own `servers.pkir`. First connection to a `self-signed` server shows its certificate fingerprint and asks the player to trust it.

**Deployment stages**
1. **Home network (now):** one machine runs everything with `docker compose up` (server + PostgreSQL + Redis), or the server alone with local DBs installed. `public_address` = the machine's LAN IP and TLS `self-signed`. Friends outside the LAN need port forwarding of `game_port`/`api_port`. Note: do this only for short tests, and keep the admin IP allowlist on.
2. **Cheap VPS (later):** same Docker Compose file, `public_address` = domain, TLS `acme`, firewall only the two ports open, nightly `pg_dump` off-box. Moving only means copying `server.pkir`, the data pack, and a DB dump.
3. **Scale-out (if needed):** managed Postgres, modules split into services (§4.2). The config format stays the same.

---

## 5. Data model (core entities)

**Account:** id, email, password hash (Argon2id), 2FA secret, created/last login, ban state, settings sync blob.

**Character:** id, account id, name (unique), appearance (gender, skin tone, hair/outfit cosmetics), money, region progress `{regionId → story flag bitset, badges, last position}`, Pokédex (seen/caught per species/form), play time, trainer card stats.

**Pokémon instance:**
```
uuid, species, form, nickname, level, exp, nature, abilitySlot (0/1/hidden),
gender, shiny, ivs[6] (0-31), evs[6] (510 total, 252 cap), moves[4]{id, ppUps},
heldItem, friendship, pokerus{strain, days}, ball, markings, ribbons[],
ot{characterId, name, trainerId, secretId}, met{mapId, level, date, method},
eggSteps (if egg), flags{untradeable, eventOrigin, giftOrigin},
history[] (trade/market/breed audit trail ids)
```

**Storage:** party (6), PC boxes (30 slots each; start with N boxes, more bought with in-game currency), bag pockets (Items, Medicine, Poké Balls, TMs/HMs, Berries, Key Items, Battle Items, Cosmetics).

Every Pokémon has a permanent UUID and an audit trail, so duplication or ownership exploits can be traced and rolled back.

---

## 6. Game rules (unified ruleset)

| System | Base rule |
|---|---|
| Species | National Dex #1–649 plus forms. A species is obtainable only where its encounter data puts it (e.g. Gen 5 species only in Unova) unless an override adds it elsewhere |
| Battle mechanics | Generation 5 (BW2-accurate) everywhere: damage formula, abilities, items, weather, crits, type chart |
| Stats | IVs 0–31, EVs 510/252, 25 natures, hidden abilities from defined sources |
| Shiny odds | 1/8,192 base (Gen 5 rate), set by `shiny_rate` in `server.pkir`. Modifiers: Shiny Charm, event boosts. Each horde Pokémon rolls separately |
| TMs | Reusable (Gen 5 rule) in all regions |
| HMs | No move slot needed. Field moves (Cut, Surf, Strength, Fly, Waterfall, Rock Smash/Climb, Dive, Whirlpool, Defog, Flash) unlock with the matching badge and are used from a key-item **field menu**, as long as any party Pokémon *could* learn the move. HM moves can still be taught/forgotten freely for battle |
| Levelling | Gen 5 scaled exp formula. Exp Share is a toggle that gives party-wide shared exp |
| Day/night | Server clock with a faster cycle (default: 1 real hour = 1 game day). Unova seasons rotate monthly on real time |
| Story | Each region has its own story progress. A new character picks a starting region and that region's starter. The other regions unlock after earning **all 8 badges in the starting region** (`region_unlock_badges`). Each region's starter is offered on first arrival there |
| Legendaries | Story legendaries are one per character. Roaming legendaries are rolled per character |

---

## 7. Feature list — base game

Priority: **P0** = needed for the first closed test; **P1** = needed for open beta; **P2** = launch polish.

### 7.1 Client & account
| Feature | Pri |
|---|---|
| Launcher with auto-updater (delta patches, signed builds) | P0 |
| ROM wizard + ROM Library settings (§2.3) | P0 |
| Account register/login, email verification, password reset | P0 |
| 2FA (TOTP) | P1 |
| Character creation: name, gender, skin tone, starting outfit | P0 |
| Character slots: **1 free**. More unlocked in-game (default: +1 on becoming Champion of any region, +1 on becoming Champion of all three; max 3; configurable). Characters on the same account count as linked for fraud analysis (§7.10) | P1 |
| Settings: graphics (resolution, window/fullscreen, vsync, frame cap, UI scale), audio (master/music/SFX/cries), text speed, battle animations on/off, chat filters, keybinds, gamepad support | P0 (P1 for gamepad) |
| Accessibility: full rebinding, UI scale to 200%, colourblind-safe type/status markers, reduced motion, chat text size | P1 |
| Client mods folder (themes, sprite packs) | P2 |
| Screenshots | P2 |

### 7.2 World & exploration
| Feature | Pri |
|---|---|
| All three launch regions with full overworlds (Unova, Sinnoh, Johto+Kanto) | Unova P0; others P1 |
| Travel between regions (ports/airports in each region) | P1 |
| Other players visible; channels; channel switcher | P0 |
| Walk, run, bike, surf, fly, dive/waterfall/rock climb etc. as each region needs | P0 |
| Following Pokémon (lead Pokémon walks behind the player; HGSS overworld sprites, with fallbacks for species that have none) | P2 |
| Day/night, weather, Unova seasons | P1 |
| Story progress per region — every gym, rival, villain team arc, Elite Four, Champion | Unova P0; others P1 |
| Scripted events running on the server (Lua) with phasing | P0 |
| Pokémon Centers, Marts, PC (accessible in any Center), Day Care | P0 |
| Mounts/cosmetic bikes & surf skins | P2 |
| Emotes & sitting/idle animations | P2 |

### 7.3 Encounters & collection
| Feature | Pri |
|---|---|
| Grass, cave, surf, fishing (Old/Good/Super Rod), dark grass/shaking grass/dust clouds/rippling water (Unova) | P0 |
| Headbutt trees, Rock Smash, Honey trees | P1 |
| **Hordes**: 5-Pokémon wild battles started by Sweet Scent/Honey, with their own horde encounter tables. Mainly for EV training and shiny hunting | P0 |
| Swarms / mass outbreaks (rotate daily per region) | P1 |
| Static & legendary encounters, roamers | P1 |
| Encounter tables that merge all version exclusives of a pair | P0 |
| Pokédex per character with habitat/encounter-location view, completion rewards | P1 |
| Catching (Gen 5 formula), all ball types and their effects | P0 |
| Evolution: every Gen 1–5 method (level, stone, trade, trade+item, friendship, time, location, move-known, gender, party-based, etc.) | P0 |

### 7.4 Training & breeding
| Feature | Pri |
|---|---|
| EV training: vitamins, wings, EV-reducing berries, Power items, Macho Brace, Pokérus | P0 |
| **Exact IVs and EVs on the summary screen** (stat page toggle: stats / IVs / EVs). The Global Market can filter on them | P0 |
| Move Relearner, Move Tutors (in-game currency/BP/Heart Scales), Move Deleter | P0 |
| Nature & ability changers: nature-changing items (Mints-style; changes the stat effect) and Ability Capsule (swaps ability slot 0↔1, not hidden). **Expensive**, bought with BP or a lot of Pokédollars, as money sinks | P1 |
| Breeding: Day Care with compatible egg groups, Ditto, Everstone nature passing, Destiny Knot IV passing, Power items to fix an IV, egg moves, hidden-ability inheritance. **Default mode `economy`**: both parents are consumed, there's a fee per egg, and the egg comes instantly (a sink that keeps the market in balance). `classic` (parents kept, eggs over time) can be set with `breeding_mode` in `server.pkir` | P1 |
| Egg hatching (step-based; Flame Body/Magma Armor halves steps) | P1 |

### 7.5 Battles
| Feature | Pri |
|---|---|
| Server-authoritative Gen 5 battle engine (all 559 moves, 164 abilities, items, field effects) | P0 |
| Modes: wild single, horde (1v5), trainer single/double/triple/rotation | Single P0; doubles P1; triples/rotation P2 |
| Multi-battles: 2 players vs NPC pairs; 2v2 PvP | P1 |
| Turn timer (default 60 s; PvP has an overall game timer too). Timeout runs a default action | P0 |
| Disconnect handling: PvE pauses 60 s, then auto-flees/forfeits. PvP gives a grace period, then forfeit | P0 |
| Battle animations from the Gen 5 assets; a toggle to skip them | P0 |
| Spectating PvP/tournament matches | P1 |
| Battle replays (deterministic event logs; viewable/shareable by ID) | P2 |
| Trainer rematches: regular trainers rebattlable on a daily cooldown; gym leaders and Elite Four rematches with stronger teams (weekly cooldown) | P1 |

### 7.6 Social
| Feature | Pri |
|---|---|
| Chat channels: Local (map), Region, Global, Trade, Help, Party, Guild, Whisper, Battle, plus per-language global channels | P0 (Local/Whisper/Global), rest P1 |
| Chat commands (`/w`, `/r`, `/p`, `/g`, `/ignore`, `/roll`, `/who`, etc.), link items/Pokémon into chat (hover = summary card) | P1 |
| Friends list (online state, region, channel), ignore list | P0 |
| Parties (up to 4): party chat, see each other's marker on the region map, join the same channel, co-op multi battles | P1 |
| Guilds: create (fee), name + tag shown over players, ranks with permissions, MOTD, roster, guild chat, invite/kick/promote, guild log | P1 |
| Trainer card / player profile (badges, dex count, PvP rating, guild, showcase 6 Pokémon) | P1 |
| Casual player-vs-player challenges (anywhere in the overworld; rules picked by challenger) | P0 |

### 7.7 Trading & economy
| Feature | Pri |
|---|---|
| Player-to-player trade window: up to 6 Pokémon + items + money per side. Any change resets both confirmations. Two-step confirm (lock → confirm) with a final summary. Trade evolutions trigger after the trade | P0 |
| Untradeable flag (starters, story legendaries, some gifts, configurable) | P0 |
| **Global Market** (player auction house): list Pokémon/items at a fixed price, listing fee + sales tax (money sinks), duration 1–7 days, search/filter by species, nature, ability, IV/EV ranges (if visible), shiny, level, gender, egg moves, price. Price history graphs. Purchase/sale history. Mail-style delivery when offline | P1 |
| NPC marts per region, plus region-specific special shops (BP shops, cosmetics shop) | P0 |
| Currencies: Pokédollars (main), Battle Points (BP; facilities & PvP). **No real-money currency in the base game** (§17) | P0 |
| Daily money sources: trainer rematches, gym rematches, item pickups, selling | P1 |
| Berry farming: per-player soil plots in Gen 4-style berry patches, real-time growth, watering/mulch | P2 |
| Economy telemetry: money in/out by source/sink per day, market volume, price indices | P1 |

### 7.8 Competitive & endgame
| Feature | Pri |
|---|---|
| Ranked PvP matchmaking: Singles and Doubles queues | P1 |
| Formats/tiers configured in data (Ubers/OU/UU/NU/LC-style banlists computed from usage stats on a schedule, or set by hand), clauses (Species, Item, Sleep, OHKO, Evasion, Moody, Baton Pass etc.), level scaling to 50/100 | P1 |
| Rating: Glicko-2 with seasons (monthly), season-end rewards (BP, cosmetics, titles), leaderboards | P1 |
| Team Preview, team validator with clear errors | P1 |
| Automated tournaments: scheduled brackets (Swiss or single elim), sign-up window, prizes | P2 |
| Battle facility: one Battle Tower–style facility first (single/double streaks vs set teams, BP rewards), reused later for Battle Frontier (HGSS/Pt) and Battle Subway (BW) themes | P1 |
| Gym leader & Elite Four rematches (see 7.5) | P1 |

### 7.9 Live ops & events
| Feature | Pri |
|---|---|
| Event framework: scheduled start/end, event maps/NPCs (Lua), temporary encounter-table changes, event currencies, event cosmetics shop | P1 |
| Seasonal events (autumn/Halloween, winter, spring) as data + scripts | P2 |
| Server MOTD, in-game announcements, maintenance mode with countdown | P0 |
| Feature flags (turn modules on/off per region/channel without a deploy) | P0 |

### 7.10 Safety, moderation & anti-cheat
| Feature | Pri |
|---|---|
| Server-authoritative everything (§4); every input checked; rate limits per opcode | P0 |
| Report player/message (with chat context snapshot) | P0 |
| In-game administration GUI + console with roles, audit log, and undo — full spec in §11 | P0 (core), P1 (full) |
| Chat & name filter (configurable word list, per-player "family filter" toggle) | P0 |
| Anti-bot heuristics: input-timing entropy, route repetition, 24/7 sessions → flagged for review (never auto-ban in base) | P1 |
| RMT/fraud detection: trade-graph analysis (one-way value flows, new accounts funnelling wealth) | P2 |
| Account security: rate-limited login, Argon2id, 2FA, new-device email alert | P0/P1 |

---

## 8. Battle engine spec

- **Deterministic.** A battle is `(seed, rules, teams, ordered action list)` → event log. The same inputs always give the same result.
- **Runs on the server only.** The client gets an event stream (`MoveUsed`, `Damage{target, amount, effectiveness}`, `StatusApplied`, `Faint`, `Switch`, ...) and animates it. The client contains the engine code only to play replays.
- **Accuracy target:** match Gen 5 cartridge behaviour. Pokémon Showdown (MIT-licensed) is a **reference/test oracle**: we generate random battles, run them through both engines with the same RNG calls where possible, and compare outcomes. Differences get triaged into a known-differences list.
- **Coverage:** every move/ability/item has at least one unit test. Interaction tests for the known hard cases (Baton Pass chains, Illusion, Imposter, Transform+Choice, weather wars, Trick Room + priority, Encore/Disable timing, multi-hit + Sturdy/Focus Sash, etc.).
- **Horde battles:** 1 player Pokémon vs 5 wild. Spread moves hit all five. Catching is only allowed once one wild Pokémon is left.
- **Performance target:** ≥ 2,000 concurrent battles per 8-vCPU server node.

---

## 9. Scripting & content

- The original ROM event scripts are bytecode written for a single-player game. **We don't interpret them at runtime.**
- Tooling: a **script decompiler** (part of the extractor, used offline by developers) turns ROM scripts into readable pseudo-code and auto-generates **Lua skeletons** per map: NPC placement, dialogue references by (archive, line) ID, flag sets, give-item/battle calls. Content designers then fix them up by hand for MMO behaviour (phasing, instancing, rematches).
- The Lua API on the server (selection): `say(textRef)`, `choice(...)`, `battle(trainerId, opts)`, `give_item`, `give_pokemon`, `set_flag/get_flag`, `warp`, `move_npc`, `play_cutscene`, `start_instance`, `event_active(id)`.
- Dialogue in scripts always refers to *text IDs*. The client looks up the string in its own extracted text. New MMO-only text (no ROM source) lives in our own localisation files.
- Scripts hot-reload on the server. Cutscenes are a timeline of commands (camera, NPC moves, emotes, text) that the client plays.

---

## 10. Infinite Routes Studio (content tools)

Studio is a **Godot editor plugin** (`addons/ir_studio/`) inside the client project. Contributors work in the Godot editor they already have, and Studio is only included in developer builds. Its goal is to make porting the story and adding content (M3, M6) mostly visual work rather than writing code.

### 10.1 Tools
| Tool | What it does | Milestone |
|---|---|---|
| **Map & event editor** | Opens any extracted map in the 3D view, with a tile grid overlay showing collision, permissions, and encounter tiles. NPCs, warps, triggers, item pickups, signs, encounter zones, and horde spots are nodes with viewport gizmos, edited in the Inspector. The tile overlay checks placements for collisions and unreachable warps | M1 |
| **Play from here** | One button (F5 override) starts a local server and client, spawning the player on the selected tile with a chosen set of story flags, party, and time of day | M1 |
| **Event command lists** | No-code scripting in the style of RPG Maker. Every NPC/trigger has an ordered list of commands (Show text, Choice, Branch on flag, Battle trainer, Give item/Pokémon, Set flag, Move NPC, Warp, Play cutscene, ...). Lists compile to the Lua API in §9. Raw Lua is available for anything the commands can't do; hand-written Lua blocks are kept when the list is recompiled | M3 |
| **Dialogue browser** | Searchable view of every text line extracted from your ROMs, by (archive, line), with a preview of text-box rendering. Text is chosen by reference and never copied. Also edits MMO-only strings in `data/localization` | M3 |
| **Cutscene timeline** | Uses Godot's AnimationPlayer timeline for camera moves, NPC paths, emotes, text, and sounds. Exports to the cutscene command timeline that the server sends to clients | M3 |
| **Data tables** | Spreadsheet-style editors for encounter tables (incl. hordes/swarms), trainers & rematch teams, shops, tutors, items, species overrides, and drop/rate settings. Entries are checked as you type: invalid move for a species, rates that don't sum to 100%, bad level ranges | M3 |
| **Battle sandbox** | Build two teams (or import Showdown-format text), run the real engine locally with a fixed seed, and step through the event log turn by turn with full state shown. Can save a run as a regression test | M3 |
| **Data-pack inspector** | Browse the server data pack with the overrides applied, and see which override changed each value | M5 |

### 10.2 Working rules
- **Text formats are the truth.** Studio reads and writes the same JSON/YAML and Lua files under `/data` that the server loads. Godot `.tscn` scenes are temporary editor views built from those files, not what gets committed. This keeps the server free of any Godot dependency and keeps Git diffs/merges readable.
- **Each contributor uses their own ROMs.** Studio reads graphics, maps, and text from each contributor's local extraction cache (§3). Nothing taken from a ROM is ever committed. Contributors who haven't supplied the ROMs for a region can still edit its data tables, but see placeholder visuals.
- **Live reload.** While "Play from here" is running, saving a script or data file reloads it in the local server immediately. No restart needed.
- **Original geometry is read-only in the base game.** Studio places content *on* the original maps. New maps need original art and are a post-base feature.
- **Shared UI widgets.** Data-table editors, the Pokémon editor, and the flag picker are built as reusable controls and also used by the in-game admin GUI (§11).

---

## 11. In-game administration

Admins run the server from inside the normal game client. Once a user is authenticated as an admin, they can view and change any player, Pokémon, item, map, or setting through a GUI. Everything the GUI does goes through the server-side **Admin API**. The web admin panel (§4.1) uses the same API, for when an admin isn't in the game.

### 11.1 Access, roles & authentication
- **Roles:** `Player` → `Helper` → `Moderator` → `Game Master` → `Admin` → `Owner`. Roles are bundles of fine-grained permissions (e.g. `player.view`, `player.inventory.write`, `pokemon.edit`, `economy.rollback`, `server.shutdown`, `roles.grant`). The Owner can define custom roles.
- **Bootstrap:** the first Owner is granted from the server command line (`ir-server admin grant <account> owner`). After that, roles are managed in the GUI.
- **Elevation:** admins log in like normal players. Entering **Admin Mode** (`/admin` or a keybind) asks for the password **and** a TOTP code. 2FA is mandatory for every role above Helper. An elevated session ends after 30 min of inactivity (configurable). An optional IP allowlist can apply to Admin/Owner.
- **The server enforces everything.** The client contains the admin UI code, but it does nothing without permissions granted by the server, and every admin message is checked against the caller's permissions on the server. Hiding UI is for convenience, not security.
- **No self-escalation:** nobody can grant a role at or above their own. Changes to Admin/Owner roles need an Owner.

### 11.2 Admin Mode in the world
- Visible admin badge, or **invisible mode** (hidden from players, no collision, left out of `/who` and friend lists).
- Free movement/noclip, speed control, teleport (to map+tile, to player, summon player, send player), follow/spectate any player, and watch any live battle.
- **Right-click anything** (player, NPC, object, tile, Pokémon in a battle) to get a context menu with the actions allowed for that target.
- **Admin console:** every GUI action has a matching command (with autocomplete), and every command has a GUI. Commands can be saved as macros.

### 11.3 Panels — what can be viewed and changed
| Panel | View & modify |
|---|---|
| **Player inspector** | Account (email, status, roles, sessions, login/IP history — permission-gated, linked/alt accounts). Character (name, appearance, money, BP, position, play time). Region progress (story flags and badges, named from scripts, editable with a flag picker). Pokédex. Party & every PC box. Bag. Friends/guild. Trade & market history. Private admin notes |
| **Pokémon editor** | Every field in §5 (species/form, level/exp, nature, ability, IVs/EVs, moves, shiny, OT, met data, flags...). A **legality indicator** checks the result. For roles below Admin, saving a Pokémon that couldn't be obtained legitimately is **blocked**. Admin/Owner can save it after a warning and a required reason, which is audited. Create from scratch, clone, delete, or move between players/boxes |
| **Items & currency** | Give/take items, money, BP. **Bulk grants** to all online players, a guild, a channel, or a pasted list (for compensation and events) |
| **World** | Override time/weather per map or channel. Spawn NPCs, trainers, wild/static Pokémon, and item pickups. Start/stop events. Set swarms/outbreaks. Lock or evacuate a map. Set channel caps and open/close channels |
| **Live data** | Edit encounter tables, shops, trainers, and global rates (shiny, exp, money, drop multipliers) with Studio's data-table widgets (§10). Changes can be **tried on one channel**, then **published** everywhere. Live changes go into a `live/` override layer that can be exported to the repo as a patch |
| **Economy** | Browse, remove, and refund market listings. Freeze the market or trading. Money-supply dashboard. Transaction search. **Rollback** of a trade or market transaction |
| **Moderation** | Reports queue (with chat context). Chat log search. Warn / mute / kick / temp ban / perm ban. Name reset. Strike history. Appeal status |
| **Battles & competitive** | Inspect live battle state. End a battle (set winner/draw/void). Adjust ratings. Reset/close seasons. Edit tier banlists. Run tournaments (seed, disqualify, advance) |
| **Server** | Announcements (banner, chat, popup). MOTD. Maintenance countdown. Feature flags. Reload scripts/data. Restart a channel. Online player list. Health/metrics summary |
| **Roles** | Assign/remove roles. Edit custom role permissions (Owner only) |
| **Audit log** | Search every admin action (§11.4) and revert from it |

### 11.4 Safety rails
- **Every action is audited:** who, when, which role, target, a before/after diff, and a **reason** (required for punishments, economy changes, and bulk actions). The log is append-only and nobody can edit it, Owners included.
- **Undo:** every write produces a reversible change record that can be reverted with one click from the audit log. If a revert would conflict (e.g. a granted item was already traded away), the GUI shows what will happen before applying it.
- **Confirmations:** mass or destructive actions (bulk grants, wipes, bans, shutdowns, publishing live data) need typed confirmation.
- **Limits:** caps per role per day on currency/items/Pokémon created (Owner can override). Alerts go to Owners when a cap is reached.
- **Traceability:** Pokémon and items created by admins carry an `adminOrigin` flag, so anything injected into the economy can always be identified. The flag is **visible to staff only**. Players see a normal Pokémon/item (event gifts can show a normal event ribbon instead).
- **Separation:** characters in Admin Mode can't trade, list on the market, or join ranked PvP. Admins play the normal game on a separate, non-elevated session.

---

## 12. Non-functional requirements

| Area | Target |
|---|---|
| Concurrency | 5,000 CCU per world deployment at launch; horizontal channels |
| Latency | Playable at ≤ 250 ms RTT; movement feels local at any latency (prediction) |
| Server tick | Movement/AoI 10 Hz; battles event-driven |
| Client performance | 60 fps on integrated GPUs from 2018 onward at 1080p; RAM < 2 GB |
| Extraction time | All required ROMs (3) in < 3 min on a mid-range SSD machine |
| Availability | 99.5% monthly excluding scheduled maintenance |
| Data safety | PITR backups for PostgreSQL (15-min RPO); daily full snapshot kept 30 days |
| Security | TLS everywhere (self-signed allowed for home/LAN tests); secrets only via env/file references in `server.pkir` (§4.5); OWASP ASVS L2 for web/auth |
| Platforms | Windows 10+ (P0), Linux (P1), macOS (P2). Mobile is out of scope for base |

---

## 13. Repository layout

```
/src
  /InfiniteRoutes.Core          shared models, battle engine, protocol, RNG
  /InfiniteRoutes.Extractor     NDS/NitroFS/NARC readers + converters (lib + CLI)
  /InfiniteRoutes.Client        Godot project (C#), incl. in-game admin GUI
    /addons/ir_studio           Studio editor plugin (dev builds only)
  /InfiniteRoutes.Server        host + modules (World, Battle, Social, Economy, Admin API, ...)
  /InfiniteRoutes.Admin         web admin panel (same Admin API as in-game)
/data
  /overrides                    balance/content patches (authoritative)
  /scripts                      Lua per region/map
  /localization                 MMO-only strings
  rom_manifest.json             known-good dump hashes + extractor profiles
/tests                          unit, battle-oracle, integration, load
/tools                          script decompiler, data-pack builder, load bots
/docs                           this spec, ADRs, protocol reference
```

---

## 14. Testing strategy

- **Extractor:** golden-file tests per converter (fixtures created from developer-owned ROMs, **kept out of the repo**; CI runs extractor tests on a private runner that has the ROMs mounted).
- **Battle engine:** unit tests per move/ability/item, plus differential testing against the Showdown oracle (nightly, ≥ 100k random battles).
- **Server:** integration tests for trade/market transactions (atomicity, races, double-spend attempts), movement validation, script flows.
- **Load:** headless bot clients (walk, battle, chat, trade) to 2× target CCU before each beta phase.
- **Security:** fuzzing the protocol decoder; authz tests for every opcode; pen test before open beta.
- **Admin API:** a permission-matrix test that calls every admin action under every role. A completeness test that fails CI if any admin handler doesn't write an audit record and a reversible change record. Revert tests for every write action.
- **Studio:** round-trip tests (load data → edit in Studio → save → byte-identical when nothing changed). Command-list → Lua compile tests.

---

## 15. Milestones

| # | Milestone | Exit criteria |
|---|---|---|
| M0 | **Foundations** | Repo, CI, core libs. Extractor reads NDS header/NitroFS/NARC and validates all 9 games. ROM wizard + ROM Library UI work |
| M1 | **Unova single-player slice** | Delivered in phases: |
| M1a | Data & 2D assets ✅ | LZ10/LZ11, Gen 5 text, species data, battle sprites (normal/shiny, front/back) extracted into the cache; extraction runs at first launch and from the ROM Library; Pokédex Viewer in the client |
| M1b | Overworld 🟡 | ✅ Whole Unova overworld matrix extracted: map chunks + buildings (NSBMD/NSBTX → glTF, all 7 DS texture formats), collision with tall grass/water flags, chunk streaming, tile walking with raycast ground height, DS-style camera, animated player sprite (hero/heroine walk + run frames; character sprites face the camera but are depth-tested as if standing upright, so stairs and raised floors behind them don't cut through), every zone's map (interiors, caves, gyms) with indoor furniture packs, and warps from zone events (doors and stairs on step-in, exit mats and map edges on push, fade transition) — verified Nuvema Town → player's house 1F/2F → back out → Route 1. NPCs from zone events (sprite table a/0/4/8 → a/0/4/9 sheets; 1-, 7-mirrored- and 12-frame layouts): stand, look around (movement 2) or wander within their leash (3), block their tiles, and turn to face the player on Z/Enter with a placeholder dialogue box; only NPCs without a hide flag show until story flags exist. Seasons: seasonal outdoor areas (groups of four, area byte 7) export a model per season, with the season's building pack layered over spring's (it only holds the buildings that change); the client follows Black/White's monthly rotation and F4 cycles it. Texture scroll animations: each area's SRT animation (area u16 @4 low byte → BTA0 in a/0/6/9) is exported as JSON and played on matching chunk materials at 30 fps — sea, rivers, shorelines, fountains, clouds. Frame-swap animations (area u16 @4 high byte → a/0/7/0: per animation a frame table — start ticks, texture and palette indices, tracks, length — and a BTX0 of the frames) are exported as JSON plus one PNG per frame, and swap the texture on every chunk material drawn with that texture — swaying flowers, sea foam, river banks, waterfalls, gym fire and electricity. Extraction holds a per-cache lock so two clients or the CLI never extract into the same folder. Debug: F3 collision/warp overlay, mouse-wheel zoom. **Still to do:** rail-coordinate warps (77 of 886) and rail NPCs (40 of 2,257), NPC dialogue (needs the script engine), story-flag NPC visibility, 3D-model sprites (23 ids), furniture interaction, animated object models (a/2/1/0) |
| M1c | Battles 🟡 | ✅ Move data (a/0/2/1, 36 bytes: type, kind, category, power, accuracy, PP, priority, hits, ailment, crit, flinch, recoil/drain, heal, target, stat changes), level-up learnsets (a/0/1/8) and wild encounter tables (a/1/2/6, one or four seasonal 232-byte sets; zone header u16 @0x14) extracted to `data/`. Core battle engine (`Core.Battle`, server-ready): Gen 5 stats with IVs and natures, six EXP curves, type chart, priority/speed order, accuracy/evasion, crits, STAB, random roll, burn/poison/paralysis/sleep/freeze, confusion, flinch, stat stages, multi-hit, recoil, drain, healing moves, one-hit KOs, Struggle, escape odds, scaled EXP with level-ups and new moves (a fifth move asks which to forget, or skips it). Abilities: species ability ids (personal 0x18/0x19, hidden 0x1A) are extracted, each Pokémon has one (random of two for wild ones; trainers' gender/ability byte high nibble picks 1st/2nd/hidden) and keeps it in saves; 134 of the 164 work in battle — entry (Intimidate, Trace, Download, Pressure, Mold Breaker), absorbing moves (Levitate, Volt/Water Absorb, Flash Fire, Lightning Rod, Motor Drive, Storm Drain, Sap Sipper, Wonder Guard), damage (pinch abilities, Huge Power, Guts, Technician, Adaptability, Sheer Force, Thick Fat, Filter, Multiscale, Sturdy…), contact (Static, Flame Body, Poison Point, Effect Spore, Rough Skin, Iron Barbs), status and stat immunities (Limber, Insomnia, Clear Body, Hyper Cutter…), Synchronize, Simple, Contrary, Defiant, Moxie, Speed Boost, Shed Skin, Truant, Prankster, trapping (Shadow Tag, Arena Trap, Magnet Pull) and Run Away; the battle screen shows a banner naming the ability that took effect. Weather: sun, rain, sandstorm and hail from Sunny Day/Rain Dance/Sandstorm/Hail (5 turns) or Drought/Drizzle/Sand Stream/Snow Warning (until replaced); Fire/Water ×1.5/×0.5, sandstorm and hail chip damage and the Rock Sp. Def boost, Thunder/Hurricane/Blizzard accuracy, Solar Beam and Weather Ball power, Weather Ball's type, Morning Sun/Synthesis/Moonlight healing, no freezing in sun, and the weather abilities (Swift Swim, Chlorophyll, Sand Rush, Sand Veil, Snow Cloak, Rain Dish, Ice Body, Dry Skin, Solar Power, Hydration, Leaf Guard, Sand Force, Flower Gift, Overcoat, Cloud Nine, Air Lock); the battle screen shows falling rain or hail, blowing sand or a sunlit tint. Held items: wild Pokémon hold their species' items (personal 0x0C/0x0E: common 50%, rare 5%), trainers' Pokémon their listed ones, and items are given and taken in the Party tab and kept in saves; in battle: type items, Plates, incenses, gems, Choice Band/Specs/Scarf (with the move lock), Life Orb, Expert Belt, Muscle Band, Wise Glasses, species items (Light Ball, Thick Club, DeepSea Tooth/Scale, Soul Dew, Metal/Quick Powder, Lucky Punch, Stick), Leftovers, Black Sludge, HP/status/pinch/type-resist berries, Leppa, Focus Sash/Band, Rocky Helmet, Air Balloon, Absorb Bulb, Cell Battery, White Herb, Shell Bell, Big Root, King's Rock/Razor Fang, Scope Lens/Razor Claw, BrightPowder/Lax Incense, Wide/Zoom Lens, Quick Claw, Lagging Tail, Iron Ball and the Power items, Toxic/Flame Orb, weather rocks, Smoke Ball, Amulet Coin/Luck Incense, Lucky Egg and Exp. Share, plus Klutz, Unburden, Gluttony, Unnerve, Harvest and Frisk; consumed items are gone for good. Move effects (by move id): fixed damage (Sonic Boom, Dragon Rage, Seismic Toss, Night Shade, Super Fang, Psywave, Endeavor, Final Gambit, Counter, Mirror Coat, Metal Burst), variable power (Low Kick, Grass Knot, Heavy Slam, Heat Crash, Flail, Reversal, Eruption, Water Spout, Wring Out, Crush Grip, Gyro Ball, Electro Ball, Return, Frustration, Hidden Power with its IV type, Stored Power, Punishment, Magnitude, Present, Fury Cutter, Rollout, Trump Card, Facade, Hex, Venoshock, Brine, Payback, Revenge, Avalanche, Assurance, Acrobatics, Smelling Salt, Wake-Up Slap, hitting Fly/Dig/Dive), two-turn moves with Fly/Dig/Dive/Bounce/Shadow Force out of reach, recharging, Thrash-style rampages, Protect/Detect/Endure, Substitute, Rest, Toxic, Leech Seed, Wrap-style trapping and Mean Look, infatuation, Yawn, Foresight/Miracle Eye, Curse, Perish Song, Taunt, Disable, Lock-On, Focus Energy, Charge, Belly Drum, Pain Split, Psych Up, Haze, Heal Bell/Aromatherapy/Refresh, Reflect/Light Screen, Mist, Safeguard, Tailwind, Wish, Trick Room, Spikes/Toxic Spikes/Stealth Rock (Rapid Spin and Defog clear them), Roar/Whirlwind, Teleport, U-turn/Volt Switch/Baton Pass (the player picks who comes in; the foe still moves that turn), Thief/Covet/Knock Off/Trick/Switcheroo/Recycle, Skill Swap/Role Play/Entrainment/Worry Seed/Simple Beam/Gastro Acid, Soak/Reflect Type, Magnet Rise, Ingrain/Aqua Ring, Explosion/Self-Destruct, Jump Kick crash damage, False Swipe, Dream Eater, Snore/Sleep Talk, Fake Out, Sucker Punch, Focus Punch, Feint, Metronome, Mirror Move and Pay Day; while charging, recharging or rampaging the battle screen skips the move menu. Trainer AI follows the trainer's ROM AI flags (bit 0: skip moves that would fail or do nothing; bit 1: favour the strongest attack and sure knockouts; bit 2: status and setup moves while it's safe, healing when low), and trainers use the medicine in their four item slots when their Pokémon drops to a quarter of its HP or gets a status; wild Pokémon still pick at random. Nicknames: after a catch the battle asks for one (Esc skips), the Party tab's Name button renames, up to 10 characters, kept in saves. PC: each box has a name (click it to rename, up to 8 characters) and sorts by Pokédex number, level or name. Save slots: three games per ROM (slot 1 is the old single save file); the Trainer Card lists them with lead, badges and play time, and Play saves the current game and switches, putting the player where that save left off; the last slot used is picked again at launch. The Mart's standard shelf is Black/White's own (2, 11, 14, 17, 18 and 19 items at 0, 1, 3, 5, 7 and 8 badges, as stored in overlay 21). Repel, Super Repel and Max Repel (100/200/250 steps, kept in saves) keep wild Pokémon below the lead's level away, with a message when they wear off; Escape Rope takes the player back to where they last left the overworld. Trainers: each class's 80×80 battle sprite (a/0/7/2, 8 files per class: NCGR built from 64×64, 16×32, 32×16 and 16×16 blocks, palette at 7) is exported; their lines come from the ROM (a/0/9/0 maps each line of message file 189 to a trainer and a type: 0 before, 1 on losing, 2 after); a trainer says their opening line when they spot or are talked to, stands on the far platform and steps aside for their Pokémon, comes back to say their losing line, and says their after-battle line when talked to again. Battle scenery: the backdrop (batt_bg*, an arena of floor disc and sky cylinder; a–d are the seasons) and platform (batt_stage*) models in a/0/1/1 are exported as glTF and rendered in 3D behind the sprites, the backdrop from inside its arena and the platforms on their own layer under the sprites' feet; the set is picked by place (zone header byte 1 groups zones; tall grass, roads, Pinwheel Forest, the desert and beach routes, caves, rooms) and season, and gym leaders fight in the gym arena. `--capture-battle=<dir>` renders every backdrop. Poké Balls (Gen 5 capture formula with three shake checks; Net/Nest/Timer/Quick bonuses; Master Ball), medicine (Potions, status heals, Revives), a party of six with switching, picking the next Pokémon after a faint, and EXP shared by the Pokémon that fought. Offline test world: Snivy at Lv 5 plus starter items; each step in tall grass rolls the zone's grass rate for a wild Pokémon from the season's slots; battle screen with sprites, info boxes, HP/EXP bars, messages, Fight/Bag/Pokémon/Run and a ball-throw animation; caught Pokémon join the party (or the PC when it is full); X opens the party and Bag (use medicine, change the lead); talking to a Pokémon Center nurse (shared script 2100, behind the ground-floor counter, talked to across it) heals the party; losing blacks out to Nuvema, healed. Trainers: trainer table (a/0/9/2, a/0/9/3: class, battle type, prize base, team with optional held items and moves; names are 9-bit compressed strings) extracted to `data/trainers.json`; trainer NPCs (script 3000 + id) spot the player within their sight range (NPC u16 @14), show "!", walk up and battle; their team comes out one by one, no running or catching, EXP ×1.5, prize money = base × 4 × last level; beaten trainers don't challenge again. Game menu (X): tabs for Party, Bag, Badges, Quests, Mail, Friends, Market and Trainer Card. `Core.Player` (server-ready): region progress (badges from gym-leader wins, trainers beaten, counters), a quest log (story chains, side and daily quests, objectives counted from the quest's start, HUD tracking), a mailbox with attachments (quest rewards and market deliveries), a friends list with requests and ignores, and the Global Market rules (fixed-price listings 1–7 days, 2% listing fee, 5% sales tax, search/sort, buy, cancel, expiry). Offline, friends and market sellers are sample data. Saving: `Core.Player.SaveGame` snapshots a `GameState` (party and box by species/move ids with IVs, EVs, nature, HP, PP and status; Bag; progress and play time; quests with their start baselines; mail and attachments; friends and ignores; market listings; location and facing) to versioned JSON, written via a temp file with a `.bak` of the previous save and read back with fallback to it; anything that no longer resolves is dropped. The client autosaves after battles, warps, Center visits and menu use, every 60 s and on quit, to `saves/offline/<code>-<sha>.json`, continues from it on launch, and offers Save now / Start over on the Trainer Card. Zone furniture events (20 bytes: script, x @8, y @12) are extracted to `zones.json`. PC: no event marks the Center PC, so it is placed from the nurse (3 tiles left of her, the terminal and the counter end in front); facing it opens the menu's PC tab: deposit (healed; the last Pokémon able to battle stays), withdraw and release across 24 boxes of 30. Poké Mart: item prices (a/0/2/4, u16 @0 × ₽10) extracted to `data/item-prices.json`; the clerks (sprite 77) in a Center open the menu's Poké Mart tab — a badge-tiered standard shelf and a town specialty shelf (our own tables for now), buying ×1/×5/×10 with a Premier Ball per ten Poké Balls, and selling at half price. **Still to do:** trainer sprites' idle animation, trainers switching Pokémon, double/triple/rotation trainer battles (run as singles for now), reading the Mart lists straight from the ROM (overlay 21 has them, but how a town picks its list isn't decoded yet; the specialty shelves are our own), the rarer move effects (Transform, Encore, Bide, Future Sight, Pursuit, Beat Up, Stockpile, the doubles-only moves), Eviolite (needs evolution data), item moves (Thief, Knock Off, Trick), the abilities that need double battles, the zone header's weather (Route 4 and Desert Resort sandstorms) carried into battles, double/triple/rotation battles, the game's own zone-to-backdrop table (we pick by kind of place), backdrop animations (BCA0), move animations, surfing and fishing encounters, shaking grass and dust clouds |
| M1d | Audio | SDAT sequences pre-rendered to OGG with loop points; map and battle music, cries |
| M1e | Studio | Map & event editor, play from here |
| M2 | **Online core** | Auth, characters, server-authoritative movement on Unova channels, other players visible, Local/Global/Whisper chat, persistence. **Admin:** Owner bootstrap, roles/permissions, 2FA elevation, Admin Mode (invisible, teleport, spectate), read-only player inspector, announce/kick, audit log |
| M3 | **Unova complete (PvE)** | Full Unova story via Lua, all encounter types incl. hordes, trainers, gyms, E4, marts, PC, Day Care (no breeding yet), Pokédex. **Studio:** event command lists, dialogue browser, cutscene timeline, data tables, battle sandbox |
| M4 | **Social + trade + PvP** | Friends, parties, guilds, P2P trade, casual PvP. **Admin:** full player/Pokémon/inventory editing, bulk grants, world panel, moderation panel, undo → **closed alpha** |
| M5 | **Economy + competitive** | Global Market, ranked PvP + tiers, battle facility, rematches, breeding, economy telemetry. **Admin:** economy panel & rollback, live data editing (test-on-channel → publish), competitive panel, web admin panel. **Studio:** data-pack inspector |
| M6 | **Sinnoh + Johto/Kanto** | Platinum and HGSS extract profiles, both regions playable end to end, inter-region travel → **open beta** |
| M7 | **Launch polish** | Live-ops event framework + first seasonal event, followers, tournaments, replays, berry farming, Linux/macOS builds → **launch** |

Post-base backlog (to build on afterwards): B2W2 Unova region, Classic Sinnoh (D/P), Pokéathlon, Super Contests, Musicals, Underground, Hidden Grottoes, PWT, Join Avenue, guild housing/bases, the dynamic-music synth, mobile client.

---

## 16. Team / effort estimate (rough)

A small team (2–3 engine/server devs, 1 tools dev, 1–2 content scripters, part-time artist/UI) can realistically reach **M4 (closed alpha) in 9–14 months**. Porting every region's story content (M3, M6) is the biggest cost — scripting, not code.

---

## 17. Legal & risk notes

- **The bring-your-own-ROM model lowers risk but doesn't remove it.** We distribute no Nintendo assets, but the game still depends entirely on Nintendo/Game Freak/The Pokémon Company IP, and those companies have shut down fan games before, including free ones.
- **The name.** "Pokémon" in the title is a registered trademark and the most visible risk. The fallback is to drop the prefix and go by **"Infinite Routes"**. Keep logos, domains, and handles built around "Infinite Routes" from the start, so switching is just a rename.
- **Money.** Charging money (subscriptions, premium currency, paid cosmetics) greatly increases legal exposure. The base spec has **no monetisation**. Any later decision about it should be made with legal advice.
- **ROM sourcing.** The client only accepts files the player chooses. We never link to, host, or help obtain ROMs. Player-facing text says they must own the games.
- **Test fixtures.** ROMs and extracted assets never go into the repo, CI artefacts, or bug reports.

---

## 18. Decision log

All decisions made 2026-10-30. Each one can be changed in `server.pkir` where noted.

| # | Decision | Outcome | Where |
|---|---|---|---|
| 1 | Black vs White exclusive areas | Phased per player: each player sees the area from the version they supplied; encounters merged | §2.1 |
| 2 | Breeding mode | `economy` (parents consumed, fee, instant egg); `classic` available | §7.4, `breeding_mode` |
| 3 | IV/EV visibility | Exact numbers shown; market filters on them | §7.4, `iv_ev_visible` |
| 4 | HM rules | Badge + field menu, no move slot used | §6 |
| 5 | Nature/ability changers | Yes, expensive (BP / large Pokédollar sinks) | §7.4 |
| 6 | Region unlock | Start region, then all 8 badges unlock the others | §6, `region_unlock_badges` |
| 7 | Character slots | 1 free; +1 at first Champion, +1 at all three Champions (max 3) | §7.1, `character_slots_free` |
| 8 | Shiny rate | 1/8,192 base + modifiers | §6, `shiny_rate` |
| 9 | Hosting | Configured in `server.pkir`; home network for testing now, cheap VPS later | §4.5 |
| 10 | Name fallback | "Infinite Routes" | §17 |
| 11 | Admin creating Pokémon that couldn't be obtained legitimately | Blocked below Admin; Admin/Owner allowed with warning + required reason | §11.3 |
| 12 | `adminOrigin` visibility | Staff only | §11.4 |

No open decisions block M0/M1.
