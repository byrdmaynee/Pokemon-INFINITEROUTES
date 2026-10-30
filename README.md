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

Open `src/InfiniteRoutes.Client/project.godot` in Godot 4.7 (.NET) and press F5. The first run shows the setup wizard.

## ROM tool

```bash
dotnet run --project src/InfiniteRoutes.Extractor.Cli -- validate "path/to/game.nds"
```

Other commands: `info`, `ls`, `narc`, `scan`, `library` (run with no arguments for help).

### Verifying dumps

`data/rom_manifest.json` has an empty `knownDumps` list for each game, so any dump that passes the header and structure checks is accepted as "valid, not yet verified". To lock a game down, add the SHA-1s of clean dumps from the No-Intro DAT (`ir-extract validate` prints a file's SHA-1).

## Config files

- `%APPDATA%/InfiniteRoutes/config/roms.pkir`: the player's ROM paths (TOML syntax; safe to edit by hand).
- Set `IR_HOME` to keep config and cache under one folder instead.
