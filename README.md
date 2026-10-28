# Polymorph

Just like that, remember the other block game mod ? Yup, this block game too now has one! The survival crafting grid can make more than one item from the same ingredients if there are multiple recipes present, a button shows up next to the output slot so that you can click it and pick the result you want.

There are some more things that what was the original project i forked (Recipe Selector), other than adding a little icon and making it more than just cycling, i added some server side denylist for conflicts that could emerge, for these there are 3 commands added that are only available to server admins (or yourself if you play in sp).

`/polymorph conflicts` creates a .txt file in the ModConfig folder containings all found conflicts of recipes, with the relative string to paste in the mod own Polymorph.json denylist, a denied result will not craft not on the server side (authoritative) and not even should be shown on the client side.(beware roofs and their variants are present aswell, so just ignore them while i find a better way to "find" "real" conflicts)

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

A cooking pot on a firepit cooks the first cooking recipe that matches its ingredients. When more than one recipe matches, a button between the pot slot and the output slot lets you pick which one that firepit cooks, and the "Will make" text follows your choice. Each firepit remembers its choice. You can still switch during the first quarter of cooking. After that the button is greyed out until the pot is done. A dirty pot only offers recipes that cook into something other than food, as in vanilla.

Cooking recipes go in the same denylist, written as `cooking:<recipe code>`, for example `"cooking:stick-gold"`. A denied recipe never cooks and does not show in the choices, and if it was the first match the next allowed recipe cooks instead. `/polymorph conflicts` lists pots that more than one recipe can cook, with the deny line for each recipe. It tries a few ingredient combinations per recipe, so a conflict between rarer combinations can be missing. `/polymorph syntax` and `/polymorph reload` check cooking lines too.

## Prior work

This started from two other mods.

- [Recipe Selector](https://github.com/DArkHekRoMaNT/RecipeSelector) by [DArkHekRoMaNT](https://mods.vintagestory.at/show/user/614DA343A4AC0CE99F63)
- [Recipe Selector Fork](https://github.com/Just-Irish/Recipe-Selector-Fork) by Just_Irish which updated that mod for 1.21

They are MIT and so is this one.
