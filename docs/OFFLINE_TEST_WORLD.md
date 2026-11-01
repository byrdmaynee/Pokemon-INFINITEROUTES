# Offline Test World — manual test checklist

What to try in **Main menu → Offline Test World** (milestones M1b and M1c). Each item says what should happen;
anything else is a bug worth reporting with the HUD's location, season and tile numbers.

## Before you start

- [ ] Launch the client after pulling: the loading screen re-extracts your Black/White ROM once
      (extractor 0.7.3, ~20 s, ~245 MB in the cache). Later launches skip it.
- [ ] Opening two clients at once: the second waits for the first's extraction, then starts without
      extracting again.
- [ ] With no ROM extracted, the screen says to re-extract in Settings → ROM Library instead of crashing.

## Controls

| Key | Action |
|---|---|
| Arrows / WASD | Walk one tile per press; hold to keep walking |
| Shift (held) | Run |
| Z / Enter / Space | Talk to the NPC you face; press again (or Esc) to close. In battle: choose, or skip a message |
| X | Game menu: Party, Bag, Badges, Quests, Mail, Friends, Market, Trainer Card (Q / E switch tabs; X or Esc closes) |
| Mouse wheel | Zoom (0.5×–4×) |
| F3 | Collision overlay: red blocked, blue warp, green tall grass |
| F4 | Next season |
| Esc | Back to the main menu (closes a dialogue first) |

## Walking and the map

- [ ] You start in Nuvema Town; the HUD shows "Nuvema Town · <season> · tile x, y".
- [ ] Walking animates both feet; running uses the run frames and is about twice as fast.
- [ ] Facing changes when you press a direction into a wall (no step taken).
- [ ] You can't walk through houses, fences, trees, signs or water (compare with F3: red tiles).
- [ ] You can walk through tall grass (green on F3); nothing happens yet — encounters are M1c.
- [ ] In tall grass the player (and NPCs) stand down in it, half hidden, not on top of the bushes.
- [ ] Ground height follows slopes and steps; you never sink into or float above the ground.
- [ ] Walking north to Route 1 and on: chunks stream in ahead of you with no gaps at chunk seams,
      and the HUD location changes at the boundary.
- [ ] Walk far (e.g. along Route 1 to Accumula Town): no stutter build-up or missing chunks.
- [ ] Zoom out fully and in fully: the camera keeps the player centred.
- [ ] Ride a Pokémon Center escalator up to the raised floor and back: the player stays whole on every
      step (no legs or head cut off, never hidden inside the stairs), while counters and walls in front of a
      character still hide the part behind them (the nurse's legs behind her counter).

## Warps

- [ ] Door: walk up into the player's house door → fade → inside 1F, stepping off the door mat.
- [ ] Stairs: 1F stairs → 2F bedroom; 2F stairs → back to 1F next to the stairs.
- [ ] Exit mat: stand on the 1F exit mat and press Down → outside, in front of the house door.
- [ ] Other houses and the lab in Nuvema: each door leads to its own interior and back out.
- [ ] Map edges: warps at route/town edges that lead to another map (caves, gates) take you there.
- [ ] Known gap: 77 warps placed on rails (some bridges, special routes) do nothing yet.

## NPCs

- [ ] Nuvema has townspeople standing in fixed spots; Mom is in the house kitchen (1F).
- [ ] Some NPCs turn to look around; some wander a few tiles from home and come back.
- [ ] NPCs block their tile: you can't walk into one, and a wanderer won't walk into you.
- [ ] Face an NPC and press Z: it turns toward you and a box opens with its NPC/script/sprite ids.
      Press Z/Enter/Esc to close it. You can't walk while it is open.
- [ ] NPC sprites face the right way in all four directions (right-facing ones are mirrored left frames
      for 7-frame sprites — check they look natural).
- [ ] Walking away and back reloads the NPCs of that area (wanderers return to their home spot).
- [ ] Known gaps: story NPCs (Cheren, Bianca, the gift boxes upstairs) are hidden until story flags
      exist; there is no real dialogue until the script engine; 23 sprite ids that are 3D models are
      invisible but still block their tile; 40 rail-placed NPCs are missing.

## Seasons

- [ ] The starting season follows the real month (Jan spring, Feb summer, Mar autumn, Apr winter, May
      spring, …), shown in the HUD.
- [ ] F4 cycles Spring → Summer → Autumn → Winter: trees and grass change colour, the change is
      instant and NPCs stay where they are.
- [ ] Houses, the lab, mailboxes and hedges are there in every season (seasonal building packs only
      hold the buildings that change; the rest come from spring's).
- [ ] Collision is identical in every season (use F3 while cycling).
- [ ] Interiors look the same in every season.
- [ ] Walk through a few routes in winter: no missing or untextured chunks.

## Animated textures

- [ ] The sea south of Nuvema scrolls and the surf line moves on the beach.
- [ ] Rivers and ponds along the routes flow; fountains in cities animate.
- [ ] Water stays continuous across chunk seams (no visible jump at chunk edges).
- [ ] Animation keeps running after a season change (F4) and after going indoors and back out.

## Wild battles

- [ ] You start with a level 5 Snivy, 10 Poké Balls, 3 Great Balls, 5 Potions, 2 Antidotes, 2 Parlyz Heals
      and a Revive. X shows them.
- [ ] Walking through tall grass on Route 1 sometimes starts a battle (about 1 step in 12): a white flash,
      then the battle screen. Walking on paths, in towns or indoors never does.
- [ ] Route 1's wild Pokémon are Patrat and Lillipup, levels 2–4. Other routes have their own.
- [ ] The wild Pokémon is on the far platform, your Snivy from behind on the near one; names, ♂/♀,
      levels and HP bars look right. Snivy's box also shows HP numbers and an EXP bar.
- [ ] Fight shows Snivy's moves with type and PP; Esc goes back to Fight/Bag/Pokémon/Run.
- [ ] Using a move: messages play, HP bars drain, the hit Pokémon blinks. Z/Enter or a click skips ahead.
- [ ] Leer/Growl/Tail Whip lower stats ("…'s Defense fell!") and later hits do more/less damage.
- [ ] Beating the wild Pokémon: it faints, Snivy gains EXP (the bar fills), and levels up with
      "grew to Lv. N!" and new moves (Vine Whip at 7) when it gets enough.
- [ ] Run: usually gets away; "Can't escape!" sometimes against faster Pokémon, and then the wild
      Pokémon attacks.
- [ ] Losing: "You blacked out!", then you are back in Nuvema Town with Snivy fully healed.
- [ ] After a battle you are back where you were in the grass, and can walk again straight away.
- [ ] HP and PP carry over between battles until you heal.

## Catching, party and Bag

- [ ] Bag in battle lists balls and medicine with counts; Esc or Cancel goes back.
- [ ] Throwing a ball: it flies to the wild Pokémon, which vanishes into it; the ball wobbles 0–3 times.
      Breaking free shows "Oh no! The Pokémon broke free!" / "Almost had it!" etc., and the wild
      Pokémon attacks. Weakened (low HP) or sleeping/paralyzed Pokémon are caught noticeably more often.
- [ ] "Gotcha! … was caught!" ends the battle; back in the overworld a box says it joined your party
      (or went to the PC once you have six).
- [ ] Potion in battle: pick it, pick a Pokémon; HP rises and the wild Pokémon then gets its turn. Using a
      Potion on a Pokémon at full HP says "It won't have any effect." and keeps the Potion.
- [ ] Pokémon in battle: switch to another party member ("…, come back!" / "Go! …!"); the wild Pokémon
      attacks the newcomer. Fainted ones can't be picked.
- [ ] When your Pokémon faints and you have another, you must pick the next one (Esc does nothing); the
      battle carries on. Only when all have fainted do you black out.
- [ ] EXP goes to every Pokémon that was sent out and is still standing, split between them.
- [ ] X in the overworld opens the menu on the Party tab (icon, level, HP, status, moves, stats). Bag tab:
      pick Potion → it jumps to Party to choose who to heal; pick a Pokémon on its own to make it lead the
      next battle. X or Esc closes; you can't walk while it is open.
- [ ] Pokémon Center (e.g. Accumula Town): the nurse stands behind the ground-floor counter
      facing you (not her back). Talk to her across the counter → "Your
      Pokémon have been restored to full health" and HP, PP and status are all restored.
- [ ] PC: stand in front of the computer left of the nurse's counter, face it and press Z/Enter → the menu
      opens on a PC tab (only there). Pick a party Pokémon to deposit it (healed; the last Pokémon that can
      battle stays). Pick one in the box grid → Withdraw (greyed out with six in the party) or Release… (press
      twice). ◀ ▶ page through 24 boxes of 30. Catches with a full party land in the PC and can be withdrawn.
- [ ] Poké Mart: talk to either clerk behind the counter below the PC (across the counter is fine) → the
      menu opens on a Poké Mart tab. Prices match the games (Poké Ball ₽200, Potion ₽300). Buying ×10 Poké
      Balls adds a free Premier Ball; buttons you can't afford are greyed out. Selling pays half. Earning a
      badge adds stock (Great Ball and Super Potion after the first); each town has its own specialty balls.

## Trainers

- [ ] On Route 2, walking into a trainer's line of sight (straight ahead of where they face, a few tiles,
      not through trees or other people): a red "!" over their head, they walk up to you, and the battle
      starts with "You are challenged by Youngster Jimmy!" (names and classes come from your ROM).
- [ ] Talking to a trainer you haven't beaten starts the battle too.
- [ ] Their Pokémon say "The foe's …"; after one faints the next drops in ("… sent out …!"), with its own
      info box. Teams match the games (Jimmy: Patrat Lv7).
- [ ] Run says "No! There's no running from a Trainer battle!"; a Poké Ball is blocked ("Don't be a
      thief!") and used up.
- [ ] Winning: "You defeated …!" and "You got ₽… for winning!"; the money shows in the X menu. EXP is
      higher than from wild Pokémon of the same level.
- [ ] A beaten trainer never challenges again; talking to them shows a placeholder line.
- [ ] Losing to a trainer blacks out like a wild battle.

## Menu: badges, quests, mail, friends, market

- [ ] Top right of the screen: the tracked quest with its objectives, then "Badges n/8 · ₽money · ✉ n new".
      A new game tracks "A New Partner" (catch a wild Pokémon).
- [ ] Finishing a quest (catch something, heal at a Center, beat Jimmy…) pops "Quest complete: …" at the
      top; the story moves on (A New Partner → Trainer's Debut → The Striaton Gym → … → Road to the League).
- [ ] Badges tab: eight Unova badges with city, leader and type; earned ones are in colour with the date.
      Beating a gym leader shows "You received the … Badge from Leader …!".
- [ ] Quests tab: Story / Side / Daily lists, completed ones below. Picking one shows its description,
      objectives with progress bars and its reward. "Track on HUD" changes the top-right tracker; side and
      daily quests can be abandoned, story quests can't. Daily quests come back the next day.
- [ ] Mail tab: a welcome letter from Prof. Juniper, plus one per finished quest and market deal. Unread
      ones are marked ●, [+] means something is attached. Collect adds money, items or Pokémon; letters
      with uncollected attachments can't be deleted. Writing letters is greyed out (needs the server).
- [ ] Friends tab: sample friends with online / away / offline state, location and channel. Accept or
      decline the pending request; send a request by name (bad names are refused); Remove, Ignore and
      Unignore work. Whisper / Trade / Battle are greyed out (needs the server).
- [ ] Market tab → Browse: ~26 sample listings (items and Pokémon with nature, gender and IVs); filter
      by kind, sort by price / newest / ending soon, search by name. Buy takes the money and the goods
      arrive by mail. Listings you can't afford are greyed out.
- [ ] Market → Sell: pick a Bag item or party Pokémon, set quantity, price and 1–7 days; the fee (2%) and
      what you'd get after the 5% tax update as you type. It leaves your Bag/party; My listings shows it
      and Cancel mails it back. Offline, a fairly priced listing (≤ 125% of suggested) "sells" after about
      two minutes and the proceeds arrive by mail.
- [ ] Trainer Card tab: money, badges, catches, trainers beaten, quests done, play time, badge row and party.

## Saving

- [ ] The game saves by itself: after battles, warps, Pokémon Center visits and closing the menu, every
      minute while walking, and when you quit or go back to the main menu. "✓ Saved" flashes bottom right.
- [ ] Quit and relaunch, then open the Offline Test World: "Welcome back! Continuing your save from …",
      standing on the same tile facing the same way, with the same party (levels, HP, PP, status), PC box,
      Bag, money, badges, beaten trainers, quests (with their progress), mail, friends and market listings.
- [ ] Trainer Card shows "Last saved …" and the total play time across launches. "Save now" saves at once.
      "Start over…" asks once more (Esc keeps playing), then deletes the save and starts a new game at home.
- [ ] Saves live in `%APPDATA%\InfiniteRoutes\saves\offline\<game code>-<ROM hash>.json`, one per ROM,
      with the previous save kept as `.bak`. Re-extracting the ROM or deleting the cache keeps the save.

## Things that are expected to be missing (not bugs yet)

- Frame-swap animations (swaying flowers, blinking lights) and animated objects are still static.
- Trainers have no battle sprite or intro/defeat lines yet, pick moves at random, and double/triple/rotation
  trainers fight as single battles.
- In battle: no nicknames, abilities, held items, weather or move animations; moves with
  special effects beyond damage, status and stat changes just deal damage. A fifth move is skipped with a
  message instead of asking which to forget. The battle background is a placeholder.
- Other Pokémon Center staff only show their script number. Mart shelves are our own badge tiers and
  town specialties (not yet the ROM's mart tables) and only stock balls and medicine. The PC can't sort, move
  Pokémon between boxes or rename boxes, and other furniture (signs, bookshelves, TVs) does nothing yet.
- No other players; one save slot per ROM. Friends and market sellers are sample data; mail between players,
  whispers, trades, price history and IV/EV market filters need the server. Badges are drawn shapes,
  not the ROM's badge art, and quests are a starter set rather than ROM story flags.
- No sound or music.
