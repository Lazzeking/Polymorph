# Polymorph

Just like that, remember the other block game mod ? Yup, this block game too now has one! The survival crafting grid can make more than one item from the same ingredients if there are multiple recipes present, a button shows up next to the output slot so that you can click it and pick the result you want.

There are some more things that what was the original project i forked (Recipe Selector), other than adding a little icon and making it more than just cycling, i added some server side denylist for conflicts that could emerge, for these there are 4 commands added that are only available to server admins (or yourself if you play in sp).

`/polymorph conflicts` creates a .txt file in the ModConfig folder containings all found conflicts of recipes, with the relative string to paste in the mod own Polymorph.json denylist, a denied result will not craft not on the server side (authoritative) and not even should be shown on the client side.

Other than that, there is `/polymorph syntax` checks the file and tells in chat which lines don't match with a known recipe, after having checked with it you can do a simple `/polymorph reload` to send the good ones to the players who are connected at that time.

## Quern

When mods give an item more than one result for grinding, Polymorph keeps them all and a button between the quern input and output slots lets you pick which one that quern makes, each quern remembers its choice also when it is automated. You can still switch during the first quarter of grinding an item as instead it could lock the output switch at the start of the grinding as it was finnicky, after that the button is greyed out and it frees up when that item is done or you take the input out, vanilla behaviour is kept if you grind output 1 and then change to output 2, if the output slot is already occupied we let the quern drop it. (i thought about a little patch like dropping it into your inventory but thats no what this mod should do)

We use plain vanilla patches and every "add patch" in json to an item that already grinds into something is kept as one more result instead of overwriting the old one:

```json
[
  {
    "op": "add",
    "path": "/grindingProps",
    "value": { "groundStack": { "type": "item", "code": "game:ingot-copper" } },
    "file": "game:itemtypes/resource/flint.json"
  },
  {
    "op": "add",
    "path": "/grindingProps",
    "value": { "groundStack": { "type": "item", "code": "game:ingot-gold" } },
    "file": "game:itemtypes/resource/flint.json"
  }
]
```

Flint grinds into nothing in vanilla, so the first patch works exactly as in vanilla, the second one would overwrite copper, so we keep copper as a choice and gold becomes the default, the result vanilla would have ended with. The server log says which result it kept and from which patch. A `replace` patch, or any other change to the result, stays vanilla: the mod's result simply replaces the old one. If a mod removes `grindingProps`, the kept results go away too.

The item tooltip shows the default result and how many more there are, for example "When ground: Turns into 1x Copper Ingot and 1 more".

The handbook shows every result too: the item's page lists them all under "Grinds into", the quern's page lists them among what it grinds, and each result's page lists the item under "Created by" grinding without the denied results.

Quern results go in the same denylist as grid recipes, written as `quern:<item>@<result>`, for example `"quern:game:flint@game:ingot-gold x1"` (the ` x1`  isnt necessary). A denied result is never ground and does not show in the choices as the default workings of our denylist, if every result of an item is denied, the quern does nothing, so keep an eye out. `/polymorph conflicts` lists every item with more than one quern result at the end of the file, with the deny line for each, and `/polymorph syntax` and `/polymorph reload` check quern lines too.

For Vintage Story 1.22.x.

## Cooking pot

A cooking pot on a firepit normally cooks the first recipe that matches its ingredients and thats it. When more than one recipe matches, a button between the pot slot and the output slot lets you pick which one that firepit cooks, and the "Will make" text follows your choice. Each firepit remembers its choice. You can still change your mind during the first quarter of cooking, after that the button is greyed out until the pot is done (no take backs mid soup). A dirty pot only offers recipes that cook into something other than food, as in vanilla.

Cooking recipes go in the same denylist, written as `cooking:<recipe code>`, for example `"cooking:stick-gold"`, which denies the recipe in every pot. Now one recipe like soup takes a ton of different ingredients, so a line can also name them: `"cooking:soup@game:fish-raw+game:vegetable-fennel+game:waterportion"` denies soup only for a pot holding exactly those items (however many of each) and leaves soup with poultry or red meat alone, `/polymorph conflicts` and the conflicts screen use that form. A denied recipe never cooks and does not show in the choices, if it was the first match the next allowed recipe cooks instead. `/polymorph conflicts` lists pots that more than one recipe can cook, with the deny line for each recipe, it tries a few ingredient combinations per recipe so a conflict between the rarer combinations can be missing (it would take forever otherwise). `/polymorph syntax` and `/polymorph reload` check cooking lines too.

## Barrel

A barrel uses the first barrel recipe that fits its contents: a sealed recipe waits for you to seal the barrel, an instant one is made right away. When more than one recipe fits, a button next to the input slot lets you pick which one that barrel makes and the "Will turn into" text follows your choice. Each result in the choices says under it if its made now or after sealing, and for how long. Once the barrel is sealed the choice is locked.

An instant recipe does not pick for you, the player who filled the barrel gets asked: in the barrel window, or if you poured with a bucket without opening it, in a small window of its own. Each barrel remembers the recipe chosen for each set of matching recipes until it is destroyed. A player filling it still gets asked every time (with the remembered one marked) while automation like a hopper just uses the remembered choice right away. A barrel nobody has chosen for yet waits, and says so in its window, patiently.

Barrel recipes go in the same denylist, written as `barrel:<recipe code>`, for example `"barrel:drygrass-gold"`. A denied recipe never makes anything and does not show in the choices, if only one recipe is left the barrel uses it as it normally would. `/polymorph conflicts` lists barrel contents that more than one recipe fits with the deny line for each recipe, and `/polymorph syntax` and `/polymorph reload` check barrel lines too.

## Settings

`ModConfig/polymorph.json` holds the denylist and these server settings:

- `CraftingGrid`, `Quern`, `CookingPot`, `Barrel`: let players choose at that station. Off, it behaves like vanilla, the denylist still applies though
- `QuernSwitchPercent`, `CookingSwitchPercent`: how far into grinding or cooking (in percent) you can still change your mind, 25 by default
- `BarrelAskEveryTime`: a player filling a barrel gets asked about instant recipes every time. Off, only the first time
- `BarrelAutomationWaitSeconds`: after asking a player, how long automation waits before going with the remembered choice, 30 by default
- `BarrelChooseRange`: how many blocks away a player can choose for a barrel, 8 by default

With [ConfigKit](https://mods.vintagestory.at/configkit) installed the same settings and the denylist show up on its in-game screen, and a change applies for everyone at once. Players see the server's values, only admins can change them. [Integrated Mod Manager](https://mods.vintagestory.at/integratedmodmanager) works too, Polymorph shows up there with the same settings and the denylist, and saving applies them for everyone right away (no restart). No ConfigKit or Integrated Mod Manager ? No problem, the conflicts screen then has a Settings button that opens a small screen with the same settings (all but the denylist, the conflicts screen already does that one), Save there writes `polymorph.json` and applies them for everyone.

## Conflicts screen

`/polymorph conflicts` finds the conflicts, writes the conflicts file and then opens a screen for the admin who ran it. The search runs beside the server so the game keeps going while it works through a big modpack, the result comes in chat (or in the server log if you ran it from the console). `/polymorph screen` opens the screen with the conflicts found last so it opens right away, it only looks for them the first time after the server starts. Saving on the screen, `/polymorph reload` and ConfigKit changes update the screen and rewrite the file right away without looking again, which recipes conflict does not change while the server runs anyway. The same screen opens with Ctrl+P ("Polymorph: open conflicts screen" in the game controls settings, change it there if you like).

Each conflict shows what goes in, laid out like its station (a 3x3 crafting grid, the quern slot, the cooking pot four slots, the barrel item and liquid slots), an arrow, and every result it can make, hover any slot for the item name. There is a station filter, a filter for conflicts that have a denied result or none yet, and a search that matches item names.

- Click a result to deny it, click it again to let it back in
- Shift+click a result to keep only that one and deny every other result of its conflict, Shift+click it again to put the conflict back as it was
- Save writes the denylist and applies it for everyone, same as `/polymorph reload`
- Reload denylist reads `polymorph.json` again (for example after editing it by hand) and applies it for everyone, the screen follows along
- Reload conflicts finds every conflict again and rewrites the file, clicks you have not saved are dropped so its also the way to start over

Closing the screen keeps the clicks you have not saved, the Save button still counts them when you open it again, so an accidental Ctrl+P wont eat your work.

## Feature complete

With 3.0.0 Polymorph does everything i wanted it to do: the crafting grid, the quern, the cooking pot and the barrel, with the settings and the conflicts screen on top. So from here on its maintenance mode, i keep it working with new game versions and fix the bugs you find, but i am not actively adding anything else.

That said, if you have a cool idea, drop it in the comments here or open an issue on [GitHub](https://github.com/Lazzeking/Polymorph/issues) and if its good and fits the mod maybe i will work on it, no guarantees though (the block game might surprise us again).

Bugs are always welcome of course, the server log (or client log) plus the list of your mods helps a ton.

## Prior work

This started from two other mods.

- [Recipe Selector](https://github.com/DArkHekRoMaNT/RecipeSelector) by [DArkHekRoMaNT](https://mods.vintagestory.at/show/user/614DA343A4AC0CE99F63)
- [Recipe Selector Fork](https://github.com/Just-Irish/Recipe-Selector-Fork) by Just_Irish which updated that mod for 1.21

They are MIT and so is this one.
