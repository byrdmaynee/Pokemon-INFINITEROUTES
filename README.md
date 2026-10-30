# Pokémon: Infinite Routes

An online Pokémon MMO built on the player's own Nintendo DS games. The design is in [docs/BUILD_SPEC.md](docs/BUILD_SPEC.md).

> No ROMs, extracted assets, or other game data belong in this repository.

## Layout

| Path | What |
|---|---|
| `src/InfiniteRoutes.Core` | Shared code: `.pkir` config files, app paths |
| `src/InfiniteRoutes.Extractor` | NDS readers (header, NitroFS, NARC), ROM validation, ROM library |
| `src/InfiniteRoutes.Extractor.Cli` | `ir-extract` command-line tool |
| `src/InfiniteRoutes.Client` | Godot 4.7 (.NET) game client |
| `tests/InfiniteRoutes.Tests` | Unit tests (synthetic ROMs, no real game data) |
| `data/rom_manifest.json` | Supported games, regions, and known-good dump hashes |

## Requirements

- .NET SDK 10
- Godot 4.7.x **.NET edition** (for running the client)

## Build & test

```bash
dotnet build InfiniteRoutes.slnx
```

```bash
dotnet test InfiniteRoutes.slnx
```

## Run the client

Set `GODOT` to your Godot 4.7 .NET executable once:

```bash
setx GODOT "C:\path\to\Godot_v4.7.2-stable_mono_win64.exe"
```

Then use any of these:
- double-click `run-client.cmd`;
- in Visual Studio, set **InfiniteRoutes.Client** as the startup project and pick the **Run game (Godot)** profile;
- open `src/InfiniteRoutes.Client/project.godot` in Godot and press F5.

The first run shows the setup wizard; after that the client opens on the main menu. To try a fresh first run without touching your real settings, set `IR_HOME` to an empty folder before launching.

**Screenshot smoke test:** `"%GODOT%" --path src/InfiniteRoutes.Client -- --capture=<folder>` walks through the screens, saves a PNG of each, and quits.

## ROM tool

```bash
dotnet run --project src/InfiniteRoutes.Extractor.Cli -- validate "path/to/game.nds"
```

Other commands: `info`, `ls`, `narc`, `scan`, `library`, `extract`, `text`, `sprite`, `hex`, `bmd`, `map` (run with no arguments for help).

### Verifying dumps

`data/rom_manifest.json` has an empty `knownDumps` list for each game, so any dump that passes the header and structure checks is accepted as "valid, not yet verified". To lock a game down, add the SHA-1s of clean dumps from the No-Intro DAT (`ir-extract validate` prints a file's SHA-1).

## Config files

- `%APPDATA%/InfiniteRoutes/config/roms.pkir`: the player's ROM paths (TOML syntax; safe to edit by hand).
- Set `IR_HOME` to keep config and cache under one folder instead.
