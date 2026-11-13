# Pokémon: Infinite Routes — Base Build Spec

| | |
|---|---|
| Version | 0.4 — Infinite Routes, buddy field skills, trainer ghosts (§7.11–7.13) |
| Date | 2026-11-12 |
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

[routes]                         # Infinite Routes (§7.11–7.13)
route_base_level  = 15           # level band at depth 1
route_level_step  = 3            # added per depth, capped at 100
route_claim_hours = 24           # how long the first finisher has to name a route
route_rotation    = "daily"      # how often encounters/weather/items change on a route
buddy_skill_steps = 500          # steps as buddy before field skills unlock
ghost_days        = 7

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

**Infinite Route (§7.11):**
- id, region, depth, parent route + exit index (or the gateway it starts from), seed, biome, and the chunk list built from them.
- Name (null = "An Uncharted Route"), charted-by character, and charted-at date.
- Claim: character and expiry.
- The route's day seed for what changes daily, plus its ghost spots.

**Route progress:** (character, route) → required trainers beaten, and when the route was completed.

**Ghost (§7.13):** character, route, spot, team snapshot, greeting, placed/expires, and daily counters.

**Buddy bond (§7.12):** steps walked as the buddy, kept on the Pokémon instance.

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

### 7.11 Infinite Routes (the frontier)

The game is named after this feature, and it is the main endgame. Each region's edges lead out into **generated routes** that never run out. Every route ends in more exits, which lead to more routes. Nobody has walked a new route before, and the first player to complete one names it for good.

**The map: a tree that grows forever**
- **Gateways.** Each region has a few gateways at the edges of its real routes, set in data. Each gateway needs a number of that region's badges (`gateway_badges` per gateway), so new players can reach shallow routes early.
- **Routes branch.** A gateway leads to a depth-1 route. Every route ends in **1–3 exits**, and each exit leads to its own route one level deeper. So the map is a tree with many unexplored edges at once, not a single line that the strongest player races down.
- **Routes are created when first entered.** The first time any player steps into an exit that leads nowhere yet, the server creates that route. The server is the only authority: it picks a random **seed**, builds the route from it, and stores it. After that the route never changes and is the same for everyone.
- **Storage is small.** A route is stored as its seed, depth, parent exit and biome, plus the chunk list built from them. The world can grow without limit cheaply.
- **Routes are permanent.** Routes and their names are never reset or regenerated. The charted map is a record of the server's history.

**How a route is built**
- **From the region's own map pieces.** A route is assembled from whole map chunks of its region's real routes, grouped by **biome**: grassland, forest, desert, cave, coast, mountain, and Unova's seasonal variants.
- **Edges must match.** Chunks are chosen so their edges line up: walkable tiles meet walkable tiles, at matching heights.
- **Only IDs go over the wire (pillar 4).** The server sends chunk IDs, and each client draws them from its own ROM cache.
- **Length and branching grow with depth.** A route is 3–8 chunks long (longer when deeper) and runs from the entrance to an **end marker** at the far exit.
- **Side areas.** Short side branches hold items and are guarded by obstacles that a buddy field skill clears (§7.12). The main path never needs a field skill, so any player can complete any route.
- **This is the biggest unknown.** Prototype stitching Unova's chunks first, before the rest of this section is built.

**What's on a route**
- **Difficulty by depth.** The level band is `route_base_level` + `route_level_step` × (depth − 1), capped at 100: by default 15 at depth 1, +3 per depth.
- **Trainers.** Generated NPC trainers use the region's own trainer classes and sprites. A route has 3 + ⌊depth / 3⌋ of them, at most 10. Their teams come from the route's species pool at the route's level band, and they get stronger and more varied with depth. These are the route's **required trainers**.
- **Wild Pokémon.** Encounters come from the biome's species pool for the region. Rare species become more likely with depth.
- **Changes over time.** The layout and required trainers never change. Encounters, weather and item spots change every day on a daily seed (`route_rotation`), so charted routes stay worth visiting.
- **Rematches.** Required trainers can be rebattled on a daily cooldown for money, like trainer rematches (§7.5).
- **Trainer ghosts.** Charted routes also hold other players' trainer ghosts (§7.13). Ghosts are optional, never required.

**Completing a route and naming it**
- **Unnamed routes.** A route that nobody has completed is called **"An Uncharted Route"** everywhere: the location banner, the map, friends lists, and chat links.
- **What counts as completing it.** Beat every required trainer on the route (losing doesn't count; rebattle), then reach the end marker.
  - Each player's wins are saved per route, so leaving and coming back keeps them.
- **Who names it.** The first player to complete the route gets a **naming claim** and a naming prompt.
  - The server decides who is first with a single atomic database write, so two players finishing at the same moment can't both get it.
- **The claim can expire.** If the player closes the prompt, they can still name the route later from X → **Your routes**. The claim lasts `route_claim_hours` (default 24).
  - If it lapses unnamed, the claim goes to the next player who completes the route.
  - Other players who complete the route while the claim is held see "Being charted by ‹name›".
- **Once named.** The name is permanent: the person who named it can't change it.
  - The route's sign shows the name and "Charted by ‹character› on ‹date›".
  - The person who named it gets a one-time reward based on depth, and the route appears on their trainer card.
- **Group play.** Parties and expeditions are later. Until then, completion and the claim belong to one player.

**Route name rules** (checked by the server; the client checks the same rules first only to show errors early)
- **Allowed characters.** Letters A–Z and a–z and digits 0–9 only, with single spaces between words: no leading, trailing or double spaces, and no punctuation, symbols, accents or emoji.
- **Length.** 3–20 characters, at most 3 words, and at least one letter.
- **Unique.** No two routes share a name, ignoring case and spaces.
- **Reserved names are refused.**
  - Place names from any supported game's text, such as "Route 4" or "Castelia City".
  - Anything that looks like "Route" followed by a number.
  - "Uncharted", plus staff and system words such as Admin, Moderator, Staff, Server, Official and Infinite Routes.
- **Blocked words.** Names go through the name filter (§7.10). It checks the name both as written and normalised:
  - lower case;
  - digits read as the letters they look like (0→o, 1→i/l, 3→e, 4→a, 5→s, 7→t, 8→b);
  - spaces removed;
  - repeated letters collapsed.
  A blocked word anywhere in the result refuses the name. An allowlist covers innocent words that happen to contain blocked ones.
- **Refusal messages.** A refused name gets a short reason, such as "That name isn't allowed" or "Names use letters, numbers and single spaces". The claim is kept so the player can try again. Refusals are rate-limited.
- **Reporting and staff action.** Players can report a route name.
  - Staff with the right permission can **rename** a route or **reset** it to "An Uncharted Route", with a reason.
  - Every rename and reset is kept in the audit log (§11).
  - A reset can either give the claim back to the person who named it or open it to the next player who completes the route.
- **Character names.** The same filter and normalisation also apply to character names.

| Feature | Pri |
|---|---|
| Gateways, route creation on first entry, permanent seeded routes, branching exits | P1 |
| Chunk stitching by biome with matching edges (prototype first) | P1 |
| Depth level bands, generated required trainers, rotating encounters/weather/items | P1 |
| Completion, naming claims, name rules and filter, "Charted by" signs, Your routes | P1 |
| Staff rename/reset, route-name reports | P1 |
| Charted-routes map (the frontier tree, coloured by depth, uncharted edges shown) | P2 |

**Prototype status (M5b)**
- **Routes are made by hand** (decision 18). Generated layouts weren't good enough to ship, so each route is built in the **route editor** (§10.1) and saved by number. A saved route takes the place of the generated one with that number; numbers with no saved route still get a generated layout as a placeholder.
  - **Who can edit:** the route editor needs the `world.routes.edit` permission. No built-in role has it except Owner (who has everything); an Owner gives it to roles in F9 → Roles. The server checks it on every save and delete, and both are audited.
  - **Where routes live:** on the server, as `route-NNNN.json` in the `routes` folder beside `server.pkir`: number, name, look (texture set), size, and one letter per tile (`T` forest, `.` ground, `p` path, `g` tall grass, `w` water).
  - **How players get them:** the server sends every saved route when a player joins (`RouteSet`) and each change as it's saved (`RouteChanged`), so everyone walks the same routes. Offline play has none (generated placeholders only).
  - **Ends:** every run of walkable tiles on the edge of the area. One is the entrance (the west one unless another is chosen); up to three others are exits. By default the entrance leads back along the route tree and exit k to route 3n+1+k; any end can instead lead to another route's entrance or exit, or to a warp of a real zone.
  - **What a route holds besides its tiles:**
    - **Scenery:** the cell round the playable area can be painted too. It's always solid, whatever it shows.
    - **Stamps:** rectangles (up to 64×64) copied from any part of Unova's real maps (every texture set, towns and cities included), with their collision: fences, ledges, cliffs, bridges, ponds, flowers and buildings.
      - **Objects only:** a stamp can instead copy just the placed models on its tiles (signs, rocks, route gates, houses, benches, vending machines, with their shadows), standing on the route's own ground and solid where they stand.
      - The editor lists the objects found on each part of the map (about 270 in White), so most can be placed without picking a rectangle.
    - **People** (a ROM overworld sprite, facing, movement and pages of text) are one of three kinds:
      - **Talks only.**
      - **A trainer made for the route:** a name, a ROM trainer class (title and battle sprite), 1–6 Pokémon (species and level, with the moves they know at that level), prize money, what they say before the battle, on losing and afterwards, and how far they see. Their id is 2,000,000 + route × 64 + person; the server and every client build them from the route file.
      - **Gives an item:** an item and a count, given once per player on the first chat.
    - **HM obstacles:** a small tree (Cut, 1 badge) or a boulder (Strength, 3 badges), drawn with the ROM's sprites. Talking to one clears it for that player until they leave the map; the server blocks steps into it until then. Staff in Admin Mode can always clear them.
    - **Wild Pokémon:** the look's own route's (the default), a real route's, or the route's own list (species, levels, weight) with a chance per step.
  - The server checks everything on save: sizes, ends and links, sprites, text length (cleaned like chat), obstacle kinds, stamp bounds, species and levels.
- **Layout first.** `RouteLayout` designs each route by rule on a grid of 2×2-tile blocks (trees are two tiles wide):
  - one two-tile path through forest, in straight runs with right-angle bends, with a block of open ground either side;
  - clearings at most turns;
  - tall grass laid right across the path in a few places, and in the clearings;
  - one or two dead-end side pockets, and sometimes a pond;
  - 0–2 branches to the north and south edges as extra exits.
- **Art second.** Each tile is drawn by a real tile of the theme that is the same kind, has the same position parity and sits in the same surroundings (`RouteKit`: its eight neighbours' kinds), or the nearest match.
  - Tile kinds come from collision and Black/White's texture names (`TerrainTheme.Classify`): trees `ki…`, paths `michi…`, water `sea…`/`…mizu…`/`ike…`/`kawa…`.
  - Only land at the theme's main ground level is used.
  - In White, 94–97% of a route's tiles find an exact match.
- **Collision** follows the design. A cell of forest surrounds the playable area and is solid.
- **Drawing it:** the client cuts each source tile's triangles out of its chunk model for the current season (`ChunkGeometry.ClipToTiles`) and builds the cell's mesh with the model's own materials (`RouteTerrain`).
- **Themes:** a texture set is used when it has enough forest, ground and path tiles of every parity. In White that's the sets of Routes 1, 3, 5, 6, 10 and 12.
- **Routes:** node n's exits lead to 3n+1…3n+3. Each is built from a seed made from n alone, the same on the server and the client, so the layout isn't sent over the network.
  - Each node is zone 1,000,000+n on map `ir{n}`, named "An Uncharted Route".
  - It borrows the theme's route's wild Pokémon.
  - The first route's entrance leads to Nuvema Town.
- **Testing:** admin `/route [n]` goes to a route's entrance.
- **Known gaps:**
  - Routes are flat: no ledges, cliffs, bridges or fences yet.
  - Water texture animation isn't attached.
  - There are no trainers, items, naming or gateways yet (the side pockets are where items will go).
- **What didn't work** (removed): stitching whole map cells (a patchwork of unrelated places), and quilting patches of tiles (plausible up close, no design overall).

### 7.12 Buddy field skills

The Active Buddy (M2) does more than follow you: its **types** give it field skills. Field skills **add to** the HM rules and never replace them (§6). HM field moves still need their badge.

- **Passive and active skills.** Some skills work by themselves. Others are used by pressing the buddy key while facing a spot.
- **Dual types.** A dual-type buddy has both types' skills.
- **Bond.** Skills unlock after the buddy has walked **500 steps** with you as your buddy (`buddy_skill_steps`). This bond is kept per Pokémon and survives trades.
- **Server checks.** The server checks the buddy's types and bond on every use.

| Type | Skill | Effect |
|---|---|---|
| Fire | Light | Lights dark caves (as Flash); burns bramble obstacles |
| Water | Ripple | Rippling-water and fishing spots show up more often nearby |
| Grass | Forage | Picks berries from bushes on routes (daily per bush) |
| Electric | Power | Powers dead generators that open side gates |
| Ice | Freeze | Freezes small ponds into ice you can walk on |
| Fighting | Smash | Breaks cracked boulders (small ones; Rock Smash still needs its badge for the real ones) |
| Poison | Clear | Clears poison mist obstacles |
| Ground | Dig | Digs up buried items at dig spots |
| Flying | Scout | Shows the current route's layout and which of its exits are still uncharted |
| Psychic | Sense | Marks hidden items within 8 tiles |
| Bug | Lure | Bug-type wild Pokémon are more likely while walking |
| Rock | Prospect | Sometimes finds evolution stones and shards at dig spots |
| Ghost | Phase | Walks you through spectral walls into hidden side areas |
| Dragon | Awe | Wild Pokémon 10+ levels below your buddy stay away (a free Repel) |
| Dark | Sneak | Fewer wild encounters while walking |
| Steel | Magnet | Pulls nearby items towards you (pick-up range +2) |
| Normal | Fetch | Now and then brings you a common item it found (daily cap) |

- **Obstacles on generated routes.** Each generated route places brambles, generators, ponds, boulders, mist, dig spots and spectral walls in its side areas. Every type matters somewhere, but the main path never needs a skill (§7.11).
- **Obstacles on real maps.** Real story maps get only the skills that read naturally there: Light, Forage, Dig, Sense, Scout and the passives. Nothing on a story path needs one.

| Feature | Pri |
|---|---|
| Bond steps, buddy key, server-checked skills | P1 |
| All 17 type skills and their obstacles on generated routes | P1 |
| Skill spots on the real regions' maps | P2 |

### 7.13 Trainer ghosts

A player can leave a **ghost** of their trainer on a charted Infinite Route. A ghost is a copy of their current team, played by the server's trainer AI. Other players can battle it like an NPC trainer, so routes fill with real players' teams.

**Placing a ghost**
- **Where.** Use X → **Your ghost** on a charted route. Each route has a few ghost spots, and the server places the ghost in a free one.
- **Limits.** One ghost per character. It lasts **7 days** (`ghost_days`) or until you replace or recall it.
- **What it is.** The ghost is a **snapshot**: species, moves, abilities, held items, IVs/EVs and nature as they were when you placed it. Your real Pokémon are never touched, lent or at risk.
- **Levels.** You can only place a ghost on a route whose level band reaches your strongest Pokémon's level, at the band's highest level or below. If the team is above the band, it is scaled down to the band's highest level (never scaled up).
- **What it looks like.** It uses your trainer sprite and name, with a ghostly tint. A short greeting (filtered, 40 characters) is optional.

**Battling ghosts**
- **Optional.** Ghosts are never required trainers, so they never block completing a route.
- **How often.** Each player can beat a given ghost once per day for money and BP based on the route's depth. Losses can be retried.
- **Not your own.** You can't battle your own ghost or the ghosts of characters on your account.
- **Mail for the owner.** The owner gets a **daily mail digest**: battles fought, wins, losses, and who beat it.
  - Each win earns a small reward, paid by the server (never by the player who lost), capped each day.
  - These caps stop farming between your own characters or friends' accounts. Trade-graph checks (§7.10) also look at it.

**Moderation**
- Ghost greetings use the chat filter, and a ghost's trainer name is already a filtered character name.
- Players can report a ghost. Staff can remove one with a reason, and the removal goes in the audit log.

| Feature | Pri |
|---|---|
| Place/recall a ghost, snapshot teams, level cap by band | P1 |
| Battling ghosts with the trainer AI, daily limits and rewards | P1 |
| Owner mail digest and capped win rewards | P1 |
| Reports and staff removal | P1 |

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
| **Route editor** (in the game: F10 in Admin Mode, with the `world.routes.edit` permission) ✅ | Makes Infinite Routes by hand, drawn live with the chosen look's real tiles. Tabs: **Tiles** (path, ground, tall grass, forest, water on the 2×2 grid, inside the playable area or in the scenery round it), **Objects** (a list of the objects found on any part of Unova's maps, rectangles picked from any part of them, whole or objects only; small trees and boulders for Cut and Strength), **People** (talks only, a trainer with a name, class, party and prize, or a gift-giver), **Wild** (a real route's Pokémon or the route's own list) and **Ends** (the entrance, and where each end leads). Undo, resize, a generated starting layout, checks; **Save** sends the route to the server, which stores it and sends it to every player; **Test walk** saves and takes you to its entrance | M5b |
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
| M1c | Battles 🟡 | ✅ Move data (a/0/2/1, 36 bytes: type, kind, category, power, accuracy, PP, priority, hits, ailment, crit, flinch, recoil/drain, heal, target, stat changes), level-up learnsets (a/0/1/8) and wild encounter tables (a/1/2/6, one or four seasonal 232-byte sets; zone header u16 @0x14) extracted to `data/`. Core battle engine (`Core.Battle`, server-ready): Gen 5 stats with IVs and natures, six EXP curves, type chart, priority/speed order, accuracy/evasion, crits, STAB, random roll, burn/poison/paralysis/sleep/freeze, confusion, flinch, stat stages, multi-hit, recoil, drain, healing moves, one-hit KOs, Struggle, escape odds, scaled EXP with level-ups and new moves (a fifth move asks which to forget, or skips it). Abilities: species ability ids (personal 0x18/0x19, hidden 0x1A) are extracted, each Pokémon has one (random of two for wild ones; trainers' gender/ability byte high nibble picks 1st/2nd/hidden) and keeps it in saves; 134 of the 164 work in battle — entry (Intimidate, Trace, Download, Pressure, Mold Breaker), absorbing moves (Levitate, Volt/Water Absorb, Flash Fire, Lightning Rod, Motor Drive, Storm Drain, Sap Sipper, Wonder Guard), damage (pinch abilities, Huge Power, Guts, Technician, Adaptability, Sheer Force, Thick Fat, Filter, Multiscale, Sturdy…), contact (Static, Flame Body, Poison Point, Effect Spore, Rough Skin, Iron Barbs), status and stat immunities (Limber, Insomnia, Clear Body, Hyper Cutter…), Synchronize, Simple, Contrary, Defiant, Moxie, Speed Boost, Shed Skin, Truant, Prankster, trapping (Shadow Tag, Arena Trap, Magnet Pull) and Run Away; the battle screen shows a banner naming the ability that took effect. Weather: sun, rain, sandstorm and hail from Sunny Day/Rain Dance/Sandstorm/Hail (5 turns) or Drought/Drizzle/Sand Stream/Snow Warning (until replaced); Fire/Water ×1.5/×0.5, sandstorm and hail chip damage and the Rock Sp. Def boost, Thunder/Hurricane/Blizzard accuracy, Solar Beam and Weather Ball power, Weather Ball's type, Morning Sun/Synthesis/Moonlight healing, no freezing in sun, and the weather abilities (Swift Swim, Chlorophyll, Sand Rush, Sand Veil, Snow Cloak, Rain Dish, Ice Body, Dry Skin, Solar Power, Hydration, Leaf Guard, Sand Force, Flower Gift, Overcoat, Cloud Nine, Air Lock); the battle screen shows falling rain or hail, blowing sand or a sunlit tint. Held items: wild Pokémon hold their species' items (personal 0x0C/0x0E: common 50%, rare 5%), trainers' Pokémon their listed ones, and items are given and taken in the Party tab and kept in saves; in battle: type items, Plates, incenses, gems, Choice Band/Specs/Scarf (with the move lock), Life Orb, Expert Belt, Muscle Band, Wise Glasses, species items (Light Ball, Thick Club, DeepSea Tooth/Scale, Soul Dew, Metal/Quick Powder, Lucky Punch, Stick), Leftovers, Black Sludge, HP/status/pinch/type-resist berries, Leppa, Focus Sash/Band, Rocky Helmet, Air Balloon, Absorb Bulb, Cell Battery, White Herb, Shell Bell, Big Root, King's Rock/Razor Fang, Scope Lens/Razor Claw, BrightPowder/Lax Incense, Wide/Zoom Lens, Quick Claw, Lagging Tail, Iron Ball and the Power items, Toxic/Flame Orb, weather rocks, Smoke Ball, Amulet Coin/Luck Incense, Lucky Egg and Exp. Share, plus Klutz, Unburden, Gluttony, Unnerve, Harvest and Frisk; consumed items are gone for good. Move effects (by move id): fixed damage (Sonic Boom, Dragon Rage, Seismic Toss, Night Shade, Super Fang, Psywave, Endeavor, Final Gambit, Counter, Mirror Coat, Metal Burst), variable power (Low Kick, Grass Knot, Heavy Slam, Heat Crash, Flail, Reversal, Eruption, Water Spout, Wring Out, Crush Grip, Gyro Ball, Electro Ball, Return, Frustration, Hidden Power with its IV type, Stored Power, Punishment, Magnitude, Present, Fury Cutter, Rollout, Trump Card, Facade, Hex, Venoshock, Brine, Payback, Revenge, Avalanche, Assurance, Acrobatics, Smelling Salt, Wake-Up Slap, hitting Fly/Dig/Dive), two-turn moves with Fly/Dig/Dive/Bounce/Shadow Force out of reach, recharging, Thrash-style rampages, Protect/Detect/Endure, Substitute, Rest, Toxic, Leech Seed, Wrap-style trapping and Mean Look, infatuation, Yawn, Foresight/Miracle Eye, Curse, Perish Song, Taunt, Disable, Lock-On, Focus Energy, Charge, Belly Drum, Pain Split, Psych Up, Haze, Heal Bell/Aromatherapy/Refresh, Reflect/Light Screen, Mist, Safeguard, Tailwind, Wish, Trick Room, Spikes/Toxic Spikes/Stealth Rock (Rapid Spin and Defog clear them), Roar/Whirlwind, Teleport, U-turn/Volt Switch/Baton Pass (the player picks who comes in; the foe still moves that turn), Thief/Covet/Knock Off/Trick/Switcheroo/Recycle, Skill Swap/Role Play/Entrainment/Worry Seed/Simple Beam/Gastro Acid, Soak/Reflect Type, Magnet Rise, Ingrain/Aqua Ring, Explosion/Self-Destruct, Jump Kick crash damage, False Swipe, Dream Eater, Snore/Sleep Talk, Fake Out, Sucker Punch, Focus Punch, Feint, Metronome, Mirror Move and Pay Day; while charging, recharging or rampaging the battle screen skips the move menu. Trainer AI follows the trainer's ROM AI flags (bit 0: skip moves that would fail or do nothing; bit 1: favour the strongest attack and sure knockouts; bit 2: status and setup moves while it's safe, healing when low), and trainers use the medicine in their four item slots when their Pokémon drops to a quarter of its HP or gets a status; wild Pokémon still pick at random. Nicknames: after a catch the battle asks for one (Esc skips), the Party tab's Name button renames, up to 10 characters, kept in saves. PC: each box has a name (click it to rename, up to 8 characters) and sorts by Pokédex number, level or name. Save slots: three games per ROM (slot 1 is the old single save file); the Trainer Card lists them with lead, badges and play time, and Play saves the current game and switches, putting the player where that save left off; the last slot used is picked again at launch. The Mart's standard shelf is Black/White's own (2, 11, 14, 17, 18 and 19 items at 0, 1, 3, 5, 7 and 8 badges, as stored in overlay 21). Repel, Super Repel and Max Repel (100/200/250 steps, kept in saves) keep wild Pokémon below the lead's level away, with a message when they wear off; Escape Rope takes the player back to where they last left the overworld. Trainers: each class's 80×80 battle sprite (a/0/7/2, 8 files per class: NCGR built from 64×64, 16×32, 32×16 and 16×16 blocks, palette at 7) is exported; their lines come from the ROM (a/0/9/0 maps each line of message file 189 to a trainer and a type: 0 before, 1 on losing, 2 after); a trainer says their opening line when they spot or are talked to, stands on the far platform and steps aside for their Pokémon, comes back to say their losing line, and says their after-battle line when talked to again. Battle scenery: the backdrop (batt_bg*, an arena of floor disc and sky cylinder; a–d are the seasons) and platform (batt_stage*) models in a/0/1/1 are exported as glTF and rendered in 3D behind the sprites, the backdrop from inside its arena and the platforms on their own layer under the sprites' feet; the set is picked by place (zone header byte 1 groups zones; tall grass, roads, Pinwheel Forest, the desert and beach routes, caves, rooms) and season, and gym leaders fight in the gym arena. `--capture-battle=<dir>` renders every backdrop. Move animations: each move's battle effect script (a/0/6/6, file n = move n: u32 1, 14 entry offsets, then commands of a u16 opcode and a fixed number of u32 arguments, ending with 0x4D; the interpreter is overlay 94's 78-entry table at 0x02209EA8, whose handlers give each opcode's argument count) is exported to `anim/moves.json`, and all 733 particle files (a/0/0/6, Nitro SPL " APS" "12_1": 88-byte emitters with optional scale/colour/alpha/texture animations, a child emitter and gravity/random/magnet/spin/collision/convergence behaviours, then A3I5/A5I3/16-colour textures) to `anim/particles/`. The client plays the script when a move is used: loading particle files (0x06), starting emitters at the attacker or defender (0x07/0x09) or aimed and flying from one to the other (0x0C/0x0E), waits (0x38/0x39), moving and squashing the Pokémon (0x12/0x15), tinting them (0x1B) and dimming the background (0x2A); particles are simulated like the DS library at 30 updates a second in the battle's own 3D space (single-battle positions (0.5, 0.4, 7) and (0.3, 0.4, −10) from overlay 94) through a camera fitted to the sprites. `--capture-moves=<dir> [--moves=1,33,…]` plays and screenshots moves. Double, triple and rotation battles: trainers fight in their ROM battle type (trainer byte: 0 single, 1 double, 2 triple, 3 rotation; with fewer able Pokémon on either side than that, as many as both have). The engine keeps each side's Pokémon by slot; each turn the player picks an action for each of theirs (`BattleChoice`: a move and its target, a switch, an item or running), switches go first, then items, then every move by priority, Stall, Quick Claw/Lagging Tail and speed (Trick Room reverses it). Targets follow the ROM's move target byte (0 one chosen Pokémon — a fainted pick passes to the nearest foe; 4 everyone adjacent, partner included; 5 the adjacent foes; 9 a random foe; the rest the user, its side or the field); spread moves do ×0.75 to each, screens cut damage by a third, and in a triple battle the two ends can't reach each other. Intimidate lowers every adjacent foe; fainted Pokémon are replaced at the end of the turn while the bench lasts (the player picks for each empty slot); EXP goes to whoever faced the fainted foe. Trainer AI scores its moves against each of the player's Pokémon and goes for the best. Rotation battles are fought one at a time, with a free rotation once a turn among the player's first three Pokémon. Wild double battles are supported by the engine (two wild Pokémon; no Poké Balls while both stand). The battle screen places each Pokémon at overlay 94's double and triple positions through the same fitted camera the move animations use (so effects land on the right one), puts each side's group on a widened platform, shows compact info boxes, asks each of the player's Pokémon in turn (Esc goes back to the previous one) with a target list when a move could hit more than one, and plays move animations from and at the right slots. `--capture-doubles=<dir> --battle-type=1|2|3` fights the weakest ROM trainer of that type. Water: facing water, a party Pokémon that knows Surf (or a Water type, until HMs exist) carries the player onto it, shown with the ROM's surfing sprite (a/0/4/9 sheets 8 and 11); surfing steps roll the zone's surf rate and slots; the Super Rod (handed out in the test world) fishes the water ahead with the fishing slots. Phenomena: now and then (2% a step) shaking grass, a cave dust cloud or rippling water (while surfing) appears near the player as a particle effect; stepping in gives a wild Pokémon from the zone's special slots (dust clouds sometimes give a gem instead), and fishing in rippling water uses the special fishing slots. Poké Balls (Gen 5 capture formula with three shake checks; Net/Nest/Timer/Quick bonuses; Master Ball), medicine (Potions, status heals, Revives), a party of six with switching, picking the next Pokémon after a faint, and EXP shared by the Pokémon that fought. Offline test world: Snivy at Lv 5 plus starter items; each step in tall grass rolls the zone's grass rate for a wild Pokémon from the season's slots; battle screen with sprites, info boxes, HP/EXP bars, messages, Fight/Bag/Pokémon/Run and a ball-throw animation; caught Pokémon join the party (or the PC when it is full); X opens the party and Bag (use medicine, change the lead); talking to a Pokémon Center nurse (shared script 2100, behind the ground-floor counter, talked to across it) heals the party; losing blacks out to Nuvema, healed. Trainers: trainer table (a/0/9/2, a/0/9/3: class, battle type, prize base, team with optional held items and moves; names are 9-bit compressed strings) extracted to `data/trainers.json`; trainer NPCs (script 3000 + id) spot the player within their sight range (NPC u16 @14), show "!", walk up and battle; their team comes out one by one, no running or catching, EXP ×1.5, prize money = base × 4 × last level; beaten trainers don't challenge again. Game menu (X): tabs for Party, Bag, Badges, Quests, Mail, Friends, Market and Trainer Card. `Core.Player` (server-ready): region progress (badges from gym-leader wins, trainers beaten, counters), a quest log (story chains, side and daily quests, objectives counted from the quest's start, HUD tracking), a mailbox with attachments (quest rewards and market deliveries), a friends list with requests and ignores, and the Global Market rules (fixed-price listings 1–7 days, 2% listing fee, 5% sales tax, search/sort, buy, cancel, expiry). Offline, friends and market sellers are sample data. Saving: `Core.Player.SaveGame` snapshots a `GameState` (party and box by species/move ids with IVs, EVs, nature, HP, PP and status; Bag; progress and play time; quests with their start baselines; mail and attachments; friends and ignores; market listings; location and facing) to versioned JSON, written via a temp file with a `.bak` of the previous save and read back with fallback to it; anything that no longer resolves is dropped. The client autosaves after battles, warps, Center visits and menu use, every 60 s and on quit, to `saves/offline/<code>-<sha>.json`, continues from it on launch, and offers Save now / Start over on the Trainer Card. Zone furniture events (20 bytes: script, x @8, y @12) are extracted to `zones.json`. PC: no event marks the Center PC, so it is placed from the nurse (3 tiles left of her, the terminal and the counter end in front); facing it opens the menu's PC tab: deposit (healed; the last Pokémon able to battle stays), withdraw and release across 24 boxes of 30. Poké Mart: item prices (a/0/2/4, u16 @0 × ₽10) extracted to `data/item-prices.json`; the clerks (sprite 77) in a Center open the menu's Poké Mart tab — a badge-tiered standard shelf and a town specialty shelf (our own tables for now), buying ×1/×5/×10 with a Premier Ball per ten Poké Balls, and selling at half price. **Still to do:** trainer sprites' idle animation, trainers switching Pokémon, reading the Mart lists straight from the ROM (overlay 21 has them, but how a town picks its list isn't decoded yet; the specialty shelves are our own), the rarer move effects (Transform, Encore, Bide, Future Sight, Pursuit, Beat Up, Stockpile, the doubles-only moves), Eviolite (needs evolution data), item moves (Thief, Knock Off, Trick), the abilities and moves that need a partner (Plus/Minus, Helping Hand, Follow Me, Lightning Rod/Storm Drain redirection, Wide/Quick Guard), shifting to the centre in triple battles, the foe rotating in rotation battles, the player choosing who comes in after U-turn/Baton Pass in doubles (the next on the bench comes in), wild double battles in dark grass (dark grass has no terrain flag of its own; its tile behaviour isn't decoded), the zone header's weather (Route 4 and Desert Resort sandstorms) carried into battles, the game's own zone-to-backdrop table (we pick by kind of place), backdrop animations (BCA0), the move-animation commands not played yet (camera moves 0x00, sounds 0x34/0x36/0x37/0x3A, background effects 0x24/0x25/0x2B, the rarer emitter and Pokémon-motion forms; Pokémon motions are approximations), the Surf mount model and HM badge checks (with TMs/HMs) |
| M1d | Audio | SDAT sequences pre-rendered to OGG with loop points; map and battle music, cries |
| M1e | Studio | Map & event editor, play from here |
| M2 | **Online core** 🟡 | Auth, characters, server-authoritative movement on Unova channels, other players visible, Local/Global/Whisper chat, persistence. **Admin:** Owner bootstrap, roles/permissions, 2FA elevation, Admin Mode (invisible, teleport, spectate), read-only player inspector, announce/kick, audit log. ✅ **Slice 1 — walk together:** `ir-server` (`src/InfiniteRoutes.Server`, .NET 10): `server.pkir` (TOML; `init` writes the template, `check` validates; every problem listed at once; `${ENV:}`/`${FILE:}` secrets; port 0 = any free port), EF Core with SQLite by default and PostgreSQL by setting (schema created on first start; migrations once it settles), the extraction cache as data pack (zones, maps, per-chunk collision, warps). Accounts API over HTTPS on `api_port` (`/api/info`, `register`, `login` → bearer token, `logout`, `characters` list/create): account names 3–16, passwords ≥ 8, PBKDF2-SHA256 210k rounds with per-account salt, session tokens kept as SHA-256 hashes, 24 h sessions, 10 logins/registrations a minute per address, character slots from `[rules]`. Game port: TCP + TLS (self-signed certificate made on first start and kept, or a .pfx), frames of a little-endian u32 length + MessagePack (`Core.Net`: Hello/Welcome/Refused, Step/StepResult, Warp/Warped, Turn, SwitchChannel, Ping/Pong, PlayersAppeared/Moved/Left, Notice; protocol version checked), Hello within 10 s with a live token and the account's own character, 60 messages a second at most, bounded send queues. World: channels filled in order (cap per channel and per server), every step checked (one tile, not blocked, a speed budget refilled over time with 30% slack), warps only from the tile (mats/edges) or the tile in front (doors/stairs), zones followed across the overworld, players seen on the same channel and map (overworld: within 40 tiles; rooms: same zone) with appeared/moved/left updates at 10 Hz, a second login takes over the first (from its live position), idle players disconnected after 25 min, positions written at once on map changes and leaving and every 45 s otherwise, all on shutdown. Client: **Servers** (servers.pkir: add, remove, connect), **Play Online** (log in or create an account; trust-on-first-use certificate pinning with the fingerprint shown; a changed certificate is refused), **Characters** (list, create with hero/heroine, play), and the overworld online: steps sent as taken and snapped back if refused, doors/mats asked of the server, other players drawn walking with name tags, HUD with server/channel/character, Esc leaves; the offline game is unchanged. See `docs/ONLINE_TEST.md`. ✅ **Slice 2 — chat, server-held games, admin:** chat (local = players who see you; global; whisper by character name with reply; system lines; staff lines marked; control characters stripped, 200 characters, a rate limit from `[chat]`). Each character's game state (`Core.Player.SaveGame` JSON on the character row; a new game from `Core.Player.StarterKit`, shared with the offline new game) is held by the server and sent to the client (`StateSnapshot`); the online menu (X) shows party (make lead), Bag and trainer card. Saving follows the agreed plan: per-player batch timer (`save_interval_seconds`), immediate writes on map changes, logout and `SaveNow` (for catches/battles/trades later) that write position and changed state together and restart that player's timer. Schema: a `SchemaInfo` version and step-by-step upgrades (SQLite and PostgreSQL statements) so older databases upgrade in place. Admin: roles Player < Helper < Moderator < Game Master < Admin < Owner with built-in permission sets (each role has those below it; nobody grants at or above their own role, only Owners make Admins/Owners); `ir-server admin grant|staff|audit` on the command line and `[admin] bootstrap_owner`; two-factor (RFC 6238 TOTP, 30 s, ±1 step, each code once) set up from the client's Account screen via `/api/2fa/setup|confirm`; Admin Mode (`Elevate`: password + code, 5 tries per 5 min, required 2FA unless `require_2fa = false`, ends after `elevation_timeout_minutes` idle); console commands checked server-side: who, inspect (read-only: account, character, money, badges, party, PC, Bag), kick (reason required, not staff at or above you), announce (banner + chat), tp (player or zone/x/y), summon, invisible (hidden from players, seen by staff in Admin Mode, can't be whispered), noclip, grant, audit, exit; an append-only audit log of elevations (and failures), every command, denials and 2FA changes. Client: chat box (Enter/T, `/g /w /r /ch /help`), `/admin` prompt, Admin Mode HUD, Account screen. ✅ **Slice 3 — admin GUI, spectate, mute:** mute/unmute (Helpers and up; minutes and a reason required; kept on the account so relogging doesn't lift it; schema 3), spectate (Game Masters: invisible, following the target's channel, map and tile, warped along through doors; the client's camera follows them and loads the map around them; walking refused meanwhile; ends on `spectate off`, leaving Admin Mode or the target leaving), the admin panel on F9 (`AdminPlayers` list refreshed every 3 s, per-player Inspect/Go to/Summon/Spectate/Mute/Kick by permission, a reason box, Invisible/Noclip, announce, leave Admin Mode), an inspector window (`AdminReply` topic `inspect:`), and a right-click menu on players in the world. M2's spec list is complete. ✅ **Slice 4 — player management and roles as data (ahead of M4):** roles live in the database (`Roles`: name, rank, permissions; schema 4) — the built-in six are seeded with their defaults, Owners make, rank (1–99), re-permission and delete others; 17 named permissions (`player.list/view/kick/ban`, `chat.mute`, `server.announce`, `world.teleport`, `player.summon`, `admin.invisible/noclip/spectate`, `player.inventory.write`, `pokemon.edit`, `pokemon.illegal`, `roles.grant`, `audit.view`, `audit.revert`); nobody acts on or grants a rank at or above their own except Owners. Bans (timed or permanent, with a reason; login and the game port refuse, sessions dropped). The F9 admin window: Players (online or search everyone, offline included), Player (role, ban, mute, kick, money, Bag, party/PC heal/give/release, a full Pokémon editor with server-side legality checks and the `AdminOrigin` ★ flag), Roles, Audit. Every game edit needs a reason and is kept as a before/after `StateChange`, revertible from the audit (refused, then forceable, if the game changed since); online players see edits at once. `ir-server admin roles`. ✅ **Slice 5 — wild battles online (protocol 3):** the server rolls grass encounters on each accepted step (tile flag 2, the zone's encounter table for the season, Repel counted down and checked against the lead's level; none for staff in Admin Mode; `/wild <species> <level>` for testing) and plays the battle with `Core.Battle`. Lockstep with hidden dice: the server writes the game out and reads it back so both sides start from identical Pokémon, sends that with the wild Pokémon and a start seed (`BattleStart`); each action (`BattleInput`: turn choices, locked moves, send-outs, rotations) is checked and played by the server, which replies with that action's seed and a state digest (`BattleTurn`); the client reseeds its copy of the battle (`SingleBattle.Reseed`), replays the action for its animations and compares digests. Moves learned and catch nicknames are sent as answers; `end` makes the server record the battle (`GameState.RecordBattle`, shared with offline), heal and send home on a loss, save at once and send the new state (`BattleOver`). Steps and warps are refused mid-battle; disconnecting ends the battle where it stands. EXP is shared in party order so both sides agree. Trainer battles, surf/fish encounters and battles other players can watch are later. ✅ **Slice 6 — the player menu (protocol 4, schema 5):** X opens a tabbed menu: Pokémon & Bag, Market (Buy / Sell / Trade), Friends, Mail, Messages, and a disabled InfiniteLoots Shop tab (real-money shop, later). Server `SocialService` over new tables (`Listings`, `Trades`, `Friendships`, `Blocks`, `Letters`, `DirectMessages`): Global Market listings (items or a Pokémon; 1–7 days; 2% fee up front, 5% tax; 20 per seller; search, sort, max price; take down; expired listings returned), trade offers between two players (give money/items/a Pokémon, ask money/items/a Pokémon of any or a given species; the giver's side held; accept, decline, withdraw, 3-day expiry), friends (requests, accept/decline, remove, presence with channel and place, online/offline lines to friends) and blocks (whispers, messages, mail, requests, offers), mail (subject 40 / body 500, money and item attachments taken at sending, 30 letters an hour, collect into the game, delete once collected), direct messages (300 characters, kept, live delivery, unread counts, rate-limited, refused while muted). Goods for anyone but the actor go by mail; the actor's goods leave their game first (under the world lock, saved at once) and the database's conditional delete/update decides races, handing goods back to the loser. Game edits are refused mid-battle. `SocialBadges` keep the HUD and tab titles counting what's waiting. ✅ **Active Buddy (protocol 5):** after 3 catches (`GameState.BuddyCatches`) a party Pokémon can follow the player (`SetBuddy`; kept in saves as a party index; hidden while fainted, surfing or out of the party), online and offline. Others see it: `NetPlayer` carries the buddy species and shininess, `BuddyChanged` tells viewers when it changes. Drawn with HeartGold/SoulSilver's own walking sprites when either ROM is in the library (`Gen4Followers`: the `tsure_poke` BTX0s in `a/0/8/1`, entries 297–862, species in dex order each followed by its forms or female look — 73 extras, mapped by colour-matching against the battle sprites with Unown and Rotom pinned, and checked by eye; two frames per direction, right-facing stored and mirrored for left; palette 1 is shiny; written as `sprites/followers/{species}[_{n}][s].png` sheets plus `followers.json`; `NetPlayer`/`BuddyChanged` carry whether the buddy is female). Otherwise, and for #494–649, the battle sprites (front, back for walking away, flipped for walking right) with the character-sprite shader, standing on the sprite's lowest drawn row, hopping one tile behind |
| M3 | **Unova complete (PvE)** | Full Unova story via Lua, all encounter types incl. hordes, trainers, gyms, E4, marts, PC, Day Care (no breeding yet), Pokédex. **Studio:** event command lists, dialogue browser, cutscene timeline, data tables, battle sandbox |
| M4 | **Social + trade + PvP** | ~~Friends~~ (M2 slice 6), parties, guilds, ~~P2P trade~~ (offers, M2 slice 6; a live trade window later), casual PvP. **Admin:** ~~full player/Pokémon/inventory editing, undo~~ (done in M2 slice 4), bulk grants, world panel, moderation panel → **closed alpha** |
| M5 | **Economy + competitive** | Global Market, ranked PvP + tiers, battle facility, rematches, breeding, economy telemetry. **Admin:** economy panel & rollback, live data editing (test-on-channel → publish), competitive panel, web admin panel. **Studio:** data-pack inspector |
| M5b | **Infinite Routes** 🟡 | ✅ Generated-terrain prototype (§7.11, *Prototype status*). To do: gateways, depth bands, required trainers, completion and naming with the name rules and filter, staff rename/reset; buddy field skills and their obstacles; trainer ghosts (§7.11–7.13). **Admin:** route panel (rename, reset, remove ghost) |
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
| 13 | Infinite Routes when everything nearby is named | Never reset: routes branch forever (1–3 exits each), are created on first entry, and are permanent. Difficulty rises with depth; charted routes stay fresh through daily rotation and ghosts | §7.11 |
| 14 | Who names a route | The first player to beat all its required trainers and reach the end; "An Uncharted Route" until then; 24 h claim, then it passes on; permanent once named (staff can rename/reset) | §7.11, `route_claim_hours` |
| 15 | Route name characters | Letters, digits and single spaces only; 3–20 chars; unique; reserved names and the normalised word filter | §7.11 |
| 16 | Buddy field skills vs HMs | Field skills add to HMs, never replace them; the main path of any route never needs one | §7.12 |
| 17 | Trainer ghosts | Snapshot copies (real Pokémon never at risk), on charted routes only, never required, rewards paid by the server and capped | §7.13 |
| 18 | How Infinite Routes are made | By hand in the route editor, saved by number. Stitching cells, quilting tiles and rule-based generation were all tried and rejected on quality; generation stays only as a starting layout in the editor and a placeholder for numbers with no saved route | §7.11, §10.1 |

No open decisions block M0/M1.
