## Version 3.0.0 (2026-10-30)

- Polymorph is now feature complete: the crafting grid, the quern, the cooking pot and the barrel, with settings and a conflicts screen. From here on it only gets maintenance and bug fixes; ideas are still welcome, see the README
- New settings in `polymorph.json`: a switch per station (crafting grid, quern, cooking pot, barrel), how far into grinding and cooking the choice can still change, and the barrel's options (ask every time, how long automation waits, how close a player must be). A station that is off behaves as in vanilla; the denylist still applies
- With ConfigKit installed, all settings and the denylist get an in-game screen, and a change applies for everyone right away, without `/polymorph reload`. Polymorph does not need ConfigKit: without it, the conflicts screen has a Settings button with a small screen for the same settings
- With Integrated Mod Manager installed, Polymorph's settings and the denylist show up in its screen too, and saving there applies them for everyone right away
- A `polymorph.json` from an older version gets the new settings written in with their defaults, so they show in the file
- New conflicts screen, opened by `/polymorph conflicts`, `/polymorph screen` or Ctrl+P ("Polymorph: open conflicts screen" in the controls settings). Each conflict shows what goes in, laid out like its station (a 3x3 crafting grid, the quern's slot, the pot's four slots, the barrel's item and liquid slots), an arrow, and every result it can make. A meal shows as the pot holding that meal, and barrel results say whether they are made now or after how long sealed
- On the conflicts screen a click on a result denies it or allows it again, and Shift+click keeps only that result and denies the rest of its conflict (Shift+click again puts it back). Save writes the denylist and applies it for everyone. Clicks not saved yet stay when the screen is closed
- The conflicts screen filters by station and by whether a conflict has a denied result, and searches item names
- The conflicts screen has a Reload denylist button, which reads `polymorph.json` again and applies it for everyone, and a Reload conflicts button, which finds every conflict again, rewrites the file and drops the clicks not saved yet
- Finding the conflicts runs beside the server instead of stopping it, so a large modpack no longer freezes the game for everyone while it works. The screen shows a loading message meanwhile, and `/polymorph conflicts` says the result in chat when it is done, or in the server log from the console. If the conflicts cannot be found, chat says why
- The conflicts found last are kept, so `/polymorph screen` and Ctrl+P open right away; they are only looked for again by `/polymorph conflicts` and Reload conflicts, or the first time after the server starts. A denylist change, from the screen, `/polymorph reload` or ConfigKit, updates them and rewrites the conflicts file right away
- A cooking recipe can be denied for one set of ingredients only, as `cooking:<recipe>@<item>+<item>`, for example soup with fish but not with poultry. `/polymorph conflicts` and the conflicts screen use that form; `cooking:<recipe>` still denies it in every pot
- A grid result that the same items also reach in another spot, where none of the competing recipes match, is no longer listed or offered as a conflict; the result the game itself picks is always kept. Slanted roofing pieces make a different part in each spot and turn back into stone elsewhere, as in vanilla
- Long recipe names in the barrel choices' tooltips no longer leave empty lines around them
- `/polymorph conflicts`, `screen` and `reload` say in chat that they are working

## Version 2.6.0 (2026-10-29)

- When more than one barrel recipe fits a barrel's contents, a button next to the input slot picks the recipe, and the "Will turn into" text follows it. Sealing locks the choice
- Each result in the choices says under it whether it is made now or after sealing, and for how long
- An instant recipe waits for a choice instead of picking the first match. The player who filled the barrel is asked, in the barrel window or, after pouring with a bucket, in a small window of its own
- Each barrel remembers the recipe chosen for each set of matching recipes until it is destroyed. A player is still asked every time they fill it; automation uses the remembered choice right away
- The denylist takes barrel recipes as `barrel:<recipe code>`. `/polymorph conflicts`, `syntax` and `reload` handle them

## Version 2.5.0 (2026-10-28)

- When more than one cooking recipe matches a cooking pot, a button in the firepit window picks the recipe. Each firepit remembers its choice, and the "Will make" text follows it
- The cooking choice can change during the first quarter of cooking, then it is locked until the pot is done
- The denylist takes cooking recipes as `cooking:<recipe code>`. `/polymorph conflicts`, `syntax` and `reload` handle them

## Version 2.4.0 (2026-10-27)

- The choice button shows up again in modpacks that use Overhaul Lib. In Overhaul Lib 2.0.10 its inventory window moved to the "Overhaul lib legacy compat" mod, so Polymorph now adds the button to whatever inventory window a mod builds instead of looking for one mod's code. The "Polymorph found Overhaul Lib but could not add the choice button" warning is gone
- Recipes from other mods are no longer blocked. The game's own crafting grid search now runs first, with every other mod's changes to it, and Polymorph only adds the choice of result on top. A recipe another mod hides stays hidden
- The "[ArdynLeveling] Native crafting search" warnings come from ArdynLeveling itself and also appear without Polymorph. They are harmless
- Thanks to InsaneMonkey for the detailed report

## Version 2.3.0 (2026-10-20)

- The quern can grind an item into more than one result. When mods `add` `grindingProps` to an item that already grinds into something, the old result is kept as another choice instead of being overwritten. A `replace` stays vanilla
- A button between the quern input and output slots picks the result. Each quern remembers its choice
- The choice can change during the first quarter of grinding an item, then it is locked until that item is done
- The tooltip says how many more results an item grinds into
- The handbook shows every quern result on the item, quern and result pages, without denied ones, and updates on `/polymorph reload`
- The choice button says "Change result" on hover
- The denylist takes quern results as `quern:<item>@<result>`. `/polymorph conflicts`, `syntax` and `reload` handle them
- An open result panel follows changes to the quern, and closes once the result is locked
- If another mod changed the quern so Polymorph cannot patch it, querns stay vanilla and the crafting grid still works

## Version 2.2.0 (2026-10-15)

- The mod is named Polymorph
- `/polymorph syntax` checks the denylist and reports lines that match no grid recipe, without changing the list
- Loading and `/polymorph reload` skip those lines and say which ones they were
- Each result in the conflicts file has the exact deny text to copy

## Version 2.1.3 (2026-10-14)

- `/rsft2 reload` reads the denylist again and sends it to connected players
- The conflicts file lists only an arrangement that crafts more than one different item

## Version 2.1.2 (2026-10-13)

- `/rsft2 conflicts` writes that file under ModConfig
- A denylist in `ModConfig/recipeselectorforkt2.json` is sent to clients
- A denied recipe never crafts, even when it is the only match

## Version 2.1.1 (2026-10-10)

- Clicking the button opens a slot for each result
- Clicking a slot selects that recipe and closes the panel

## Version 2.1.0 (2026-10-08)

- When several recipes match, a button appears beside the output slot
- The same arrangement keeps the result you picked

## Version 2.0.1 (2026-10-08)

- Updated the Recipe Selector fork so it runs on the current game, Vintage Story 1.22.x
