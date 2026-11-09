# Quartermaster Integrations

Puts Quartermaster's nearby storage into the crafting window: how much of each ingredient you actually have, how many of the item you can craft, and how big a batch you can afford — counted with Quartermaster's own rules, so the numbers match what a craft will really pull.

---

## What it does

* **Available counts in every requirement row** — Each row shows the required amount plus what you hold across your pockets and nearby Quartermaster storage (e.g., `10 (16)`).
* **Batch-aware numbers** — Raise the craft amount in AAA Crafting or My Little UI and the rows scale with it, so a recipe needing 10 bronze reads `50` at a batch of 5. You can see what a batch actually costs before committing.
* **Counts you can trust** — Storage totals are requested directly from Quartermaster rather than estimated locally, meaning `CraftRange`, the per-chest **Crafting Supply** flag, ward access, in-use chests, and the global `CraftFromContainers` toggle are all fully respected. A chest you deliberately excluded from crafting supply will never inflate the display.
* **Max Craft that respects the station** — The max-craft figure follows the same accounting and never overrides the game: no station, or a station below the recipe's level, results in `0` rather than a phantom batch of 100.
* **Upgrade tab included** — Counts appear on the upgrade tab too, and its max-craft figure reports `0` when the next quality is unaffordable rather than a batch you could not pay for.
* **Honest craft button** — It enables only when the *whole batch* is affordable, and `-` always stays usable so you can never get stuck at a batch you cannot pay for.
* **Insufficient rows flash** — Colors and flash speed are configurable, so a missing ingredient is obvious at a glance.
* **Does not disturb your items** — ExtraSlots slot rows and typed slots are protected from Quartermaster's sort and store actions, and an open AdventureBackpacks backpack is never dumped into your inventory by a sort action.

---

## Integrations

| Mod | Required | What it adds here |
| :--- | :--- | :--- |
| **Quartermaster** (`local.valheim.quartermaster`) | **Yes** — 0.1.66+ | The source of every storage count |
| **AAA Crafting** (`Azumatt.AzuAntiArthriticCrafting`) | No — 2.1.12+ | Craft-amount input, Max Craft label, batch cap of 100 |
| **My Little UI** (`shudnal.MyLittleUI`) | No — 1.2.32–1.2.34 | Craft-amount input, batch cap of 99 |
| **ExtraSlots** (`shudnal.ExtraSlots`) | No — 1.2.21–1.2.24 | Slot rows and typed slots survive Quartermaster's sort/store |
| **AdventureBackpacks** (`vapok.mods.adventurebackpacks`) | No — 2.4.6+ | Backpack contents excluded from counts, backpack proxy never a store target |
| **AAABuildMenu** (`Azumatt.AAABuildMenu`) | No — 1.0.6+ | Pinned build rows show storage-inclusive have/need counts |
| **Epic Loot** (`randyknapp.mods.epicloot`) | No — 0.15.5–0.15.6 | Enchanting and sacrifice costs can be paid from storage; the items are actually taken from the chests. **Not compatible with EpicLootContainerBridge** — that bridge already reports storage, so the provider stays off while it is installed |
| **PlanBuild** (`marcopogo.PlanBuild`) | No — 0.20.0+ | Plan pieces can be placed from storage, including "hold to add materials" |
| **WardIsLove** (`Azumatt.WardIsLove`) | No — 4.0.9+ | Chests inside a ward you are not permitted on are excluded from counts and never pulled from; a Deposit Chest inside a ward runs its whole base on that bubble (`WardHostRange`); permitted containers keep their owner instead of being claimed (`WardOwnership`) |
| **DropMoreLoot** (`TastyChickenLegs.DropMoreLoot`) | No — 1.2.9+ | Its stack multiplier is respected; Quartermaster's stack override steps aside (`DeferToDropMoreLootStacks`) and the ledger's stack-size toggle is disabled and labelled with DropMoreLoot's multiplier |

Every optional integration is reflection-bound at startup and reports what it found. Missing one costs you that specific convenience, never the mod itself.

---

## How it works

Two rules keep the display honest:

1. **Storage numbers are requested from Quartermaster**, not recomputed locally. The mod binds `WarehouseService.CountAvailableNearPlayer`, the same query Quartermaster uses to decide what a craft may pull, so the UI cannot promise materials the craft would refuse.
2. **"Can I craft this?" is answered by the game.** The craft button is gated on `Player.HaveRequirements`, which Quartermaster makes storage-aware. The max-craft figure is computed from the same counts and reports `0` only when `HaveRequirements` agrees that nothing is craftable. The mod never invents a state the game disagrees with: if you cannot craft it, the row reads `0` and the button is disabled.

One more rule covers modded recipes: requirements your character does not recognize — such as the hidden upgrade tokens some modded gear uses — are ignored rather than treated as missing, so modded recipes are never falsely disabled.

**Material from other storage mods is not counted.** Every number here comes from Quartermaster. If another craft-from-container mod can also supply an ingredient, that stock will not appear in the displayed count — though the craft can still succeed, because the button and the max-craft figure defer to the game's requirement check rather than to these numbers.

---

## Installation

### With a mod manager (recommended)

Install through **Gale** or **r2modman**. Required dependencies will resolve automatically.

### Manual installation

1. Install [BepInExPack Valheim](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/).
2. Install [Quartermaster](https://valheim.hexium.gg/mods/Vassteel/Quartermaster) (0.1.66 or newer). Optionally add AAA Crafting, My Little UI, ExtraSlots, and/or AdventureBackpacks.
3. Drop `QuarterMaster_Integrations.dll` into your `Valheim/BepInEx/plugins/` folder.

---

## Configuration

Generated at `BepInEx/config/com.bootstrapboys.quartermaster_integrations.cfg` on first launch.

| Section | Key | Default | Description |
| :--- | :--- | :--- | :--- |
| `1. General` | `Enabled` | `true` | Turn the mod's UI changes off without uninstalling. |
| `3. Visuals` | `RequirementDisplayMode` | `CombinedTotal` | How the available count is written (see below). |
| `4. Patches` | `DeferToOtherFormatting` | `false` | If AAA Crafting or AdventureBackpacks has already formatted a row, leave its text alone. Only enable this if you prefer their style. My Little UI's own "available resources amount" counts your pockets only, so deferring to it drops chest totals. |
| `4. Patches` | `DeferToDropMoreLootStacks` | `true` | **DropMoreLoot only.** When its stacking is enabled, its multiplier wins and Quartermaster stops applying `MaximumStackSize`. The ledger's "Use custom stack sizes" toggle is then disabled and reads *Stack sizes are managed by DropMoreLoot (xN)*, with the maximum field and Save button disabled too. Turn off to keep Quartermaster's stack size. |
| `5. Ward` | `WardHostRange` | `true` | **WardIsLove only.** A Deposit Chest inside a ward runs its base on that ward's bubble instead of `BaseRange`, so its base/network range ends at the bubble edge. While you are inside a ward, only chests in that same bubble count for crafting. **Outside every ward, crafting is Quartermaster's `CraftRange` as normal** — a warded chest is still reachable if it falls within that range. |
| `4. Patches` | `ForgetRemovesEntry` | `false` | **Quartermaster only.** Its Forget button does not delete a chest entry — it moves the item to the chest's ignore list, where it keeps showing as "(ignored)" and keeps refusing storage. With this on, Forget and Forget All remove the entry outright: the row disappears and putting a sample back teaches the chest again. |
| `4. Patches` | `IgnoreCrafter` | `false` | **Quartermaster only.** It will not merge two stacks whose crafter differs, so core wood crafted at a bench sits apart from core wood you picked up (vanilla ignores the crafter). With this on, materials merge when the crafter is the only difference — same item, quality, variant, world level, durability, cheat flag and custom data. Gear, enchanted and world-levelled items keep Quartermaster's rule. |
| `4. Patches` | `IgnoredStackTags` | `ap_prov` | **Quartermaster only.** Comma-separated custom-data keys ignored when deciding whether two stackable items are identical. Empty compares every entry. Default `ap_prov` is the provenance stamp some admin panels write onto items, which otherwise keeps stamped and unstamped materials apart. Never add Epic Loot's enchant key here. |
| `4. Patches` | `MergePocketsOnSort` | `true` | **Quartermaster only.** Its inventory sort only rearranges slots and its merge routine runs for chests, never for your pockets, so sorting your inventory leaves duplicate stacks separate. With this on, identical pocket stacks are combined first (honouring `IgnoreCrafter` and `IgnoredStackTags`), equipped items are skipped, then the sort runs. |
| `4. Patches` | `ContainerFixes` | `true` | **Quartermaster only.** Makes its chest dock match what the container can actually do: `Chest Config` is hidden where the settings cannot take effect (not accessible, not writable); `Sort Chest` works on any accessible container, claiming it first when its owner is away; and `Deposit All` stores your items into any open container, not only Quartermaster deposit chests. A container another player has open is left alone. Replaces 2.7.7's `AnyChestDock`, whose value is carried over automatically. |
| `4. Patches` | `IgnoreEpicLootComponents` | `true` | Epic Loot writes its `MagicItemComponent` onto materials as well as gear. On a stackable item that component says nothing about identity, so an enchanted Essence and a plain one refused to stack because of it. With this on the component is ignored when merging stackable items; gear and every other custom-data entry still have to match. |
| `4. Patches` | `MailStaleLinkCleanup` | `true` | **Quartermaster only.** A mailbox remembers its outgoing parcel by ZDO id. If that parcel is gone - a send interrupted by a restart, or a parcel destroyed while the mailbox still pointed at it - Quartermaster reports `Checking queued mail` forever and refuses new mail. With this on, a link that has pointed at a missing parcel for a few seconds is cleared. A mailbox within 30 m is claimed first (what vanilla does when you open a chest), so the repair also works inside a ward and while the recorded owner is online elsewhere. |
| `4. Patches` | `SupplyRequiresAccept` | `true` | **Quartermaster only.** Quartermaster counts a chest as craft supply whenever it can read it, without consulting `Receive matching items` - so a chest taken out of the storage system still shows its contents as available. With this on, a chest with `Receive matching items` off is not supply at all: dropped from the recipe counts and from the pull validation. `Learning` is deliberately not part of this: it only stops the chest auto-remembering item types for assignment, and the pull path never consults it. |
| `4. Patches` | `KeepDockOffSlots` | `true` | **Quartermaster only.** Its chest dock is positioned from the panel it parents to, which does not know about rows added by another inventory mod - so the Sort Inventory button can land on the new bottom row. With this on, the dock is measured against the actual slot cells and moved down only when it overlaps one; Quartermaster's own Sort Inventory offsets (ledger, Interface) still apply on top. |
| `5. Ward` | `WardOwnership` | `IfOwnerOffline` | **WardIsLove only.** `Never` = never request or claim, so the ward owner keeps every chest and a visitor's deposits/machine collection fail; `IfOwnerOffline` = skip while the recorded owner is a connected *player* (the dedicated server session does not count: after a restart it owns everything it loaded, and skipping it would leave the whole base unwritable) and let Quartermaster claim only when that owner is not online, so deposits and machine output work; `Always` = Quartermaster's normal request/claim even inside your own ward. |
| `3. Visuals` | `SufficientColor` | `(0.68, 0.91, 0.90, 1.00)` | Color of the available count when you have enough. |
| `3. Visuals` | `InsufficientFlashBrightColor` | `(1.00, 0.22, 0.22, 1.00)` | Bright end of the insufficient-material flash. |
| `3. Visuals` | `InsufficientFlashDarkColor` | `(0.45, 0.02, 0.02, 1.00)` | Dark end of the insufficient-material flash. |
| `3. Visuals` | `FlashSpeed` | `4.0` | Flash speed, 1–12. |
| `2. Debug` | `DebugMode` | `false` | Timestamped diagnostic log written to `BepInEx/BSBLogs/QuarterMaster_Integrations.log`. |
| `2. Debug` | `EnableAAACrafting`, `EnableAAABuildMenu`, `EnableMyLittleUI`, `EnableExtraSlots`, `EnableAdventureBackpacks`, `EnableEpicLoot`, `EnablePlanBuild`, `EnableWardIsLove`, `EnableDropMoreLoot` | `true` | One switch per integration, all under 2. Debug next to `DebugMode`. Off means that mod is left entirely alone: no storage counts, no filtered chests, no patched UI. Changes apply immediately, no restart. Turning everything off leaves Quartermaster behaving as if this mod were not installed. |

### Display modes

For a recipe needing 10 bronze, with 4 in your pockets and 12 in nearby chests:

| `RequirementDisplayMode` | Row reads | Meaning |
| :--- | :--- | :--- |
| `CombinedTotal` (default) | `10 (16)` | Required (total available) |
| `SplitInventoryAndStorage` | `10 (4+12)` | Required (inventory + storage) |
| `TotalWithSplit` | `10 (16 [4+12])` | Required (total [inventory + storage]) |

> **Color format:** These values are stored as `(red, green, blue, alpha)` floats from `0` to `1`, not hex values. Hand-editing with a hex value will fail to parse, causing the default to be used instead.

---

## Debugging

Set `DebugMode = true` under `2. Debug` and the mod writes `BepInEx/BSBLogs/QuarterMaster_Integrations.log` (the file is named after the plugin and rewritten each launch) as well as the console:

| Line | What it proves |
| :--- | :--- |
| `[Patch] '<target>' applied` | that reflection patch bound at startup |
| `[Patches] <Type>.<Method> <- prefix N, postfix N, transpiler N, finalizer N` | the full inventory of methods this mod has patched — a missing one is a missing integration |
| `[Config] DisplayMode: …` | the settings that session actually ran with |
| `[Config] Patches: ForgetRemovesEntry=…, IgnoreCrafter=…, … WardOwnership=…` | every `4. Patches` / `5. Ward` switch as it was bound, so a log describes the code path it took |
| `[Craft] '<item>' xN` then one row per requirement | `need N \| pockets A + storage B = C \| enough True/False` for the selected recipe |
| `[AAABuildMenu] '<item>': pockets A + storage B = C` | a pinned row's count changed, and from what |
| `[Integrations] EpicLoot: … PlanBuild: … WardIsLove: …` | which optional integrations bound this session |
| `[QM] Bindings: CountAvailableNearPlayer=… CraftStores=…` | which Quartermaster entry points resolved |
| `[EpicLoot] Took N/Mx '<item>' from storage` | an Epic Loot cost was actually paid out of a chest |
| `[PlanBuild] Plan costs include N storage container(s)` | PlanBuild is being offered storage (one line per 10 s) |
| `[WardIsLove] Excluded '<chest>' … from storage` | ward permissions hid a chest from storage (one line per 10 s) |
| `[WardHostRange] N container(s) reachable inside M ward bubble(s)` | what the bubble is contributing to crafting (one line per 10 s) |
| `[Forget] 'item' removed from the chest (remembered=True, forgotten=False)` | Forget deletions when `ForgetRemovesEntry` is on (debug only) |
| `[Stacks] merging 'item' xN (Zebzz) with xM (looted): identical apart from the crafter` | a merge `IgnoreCrafter` allowed (debug only) |
| `[Stacks] after sorting, '$item' still has N stacks` | a sort pass ran and this item survived it as several stacks (debug only) |
| `[Pockets] Merged N pocket stack(s) before sorting.` | a pocket sort combined duplicate stacks first (debug only) |
| `[ChestDock] '<prefab>' is not ours to configure - 'Chest Config' hidden.` | the open chest cannot be written by you, so the config button was hidden (debug only) |
| `[ChestDock] Sorted '<prefab>' even though it is not ours to write.` | a chest you do not own was sorted directly instead of waiting for its owner (debug only) |
| `[ChestDock] Stored N item(s) in '<prefab>' (not a deposit chest).` | `Deposit All` used the vanilla store-everything path on an ordinary container (debug only) |
| `[Mail] Cleared a stale queued-mail link on '<prefab>' - parcel <id> no longer exists …` | a mailbox was stuck on a parcel that was never saved, and the link was cleared (debug only) |
| `[WardHostRange] inside N ward bubble(s), radius …` / `outside every ward` | entry/exit of a WardIsLove ward, checked twice a second (debug only) |
| `[WardOwnership] WardOwnership = …` | the active mode at startup (debug only) |
| `[WardOwnership] 'container' is in a permitted ward - leaving its owner alone` | a container skipped Quartermaster's ownership request (one line per container per 10 s, debug only) |
| `[DropMoreLoot] Stack sizes defer to DropMoreLoot …` | startup line naming the multiplier in charge and the toggle state |
| `[Supply] '<prefab>' is not counted: 'Receive matching items' is off.` | the chest was dropped from the craft counts because it is not a storage chest (debug only) |
| `[Dock] Moved the chest dock down N px so it clears the inventory row.` | an expanded inventory grid pushed the dock onto a slot, and it was moved clear (debug only) |
| `[Craft] Recipe list repaints after each craft.` | the recipe list is refreshed once a craft finishes, so it cannot keep a stale "can't afford" tint (debug only) |
| `[DropMoreLoot] Ledger stack tab: N control(s) disabled, toggle now reads …` | the stack tab was adjusted for the deferral (debug only) |

---

## Troubleshooting

**Check the load line first.** In your `BepInEx/LogOutput.log`, you should see:

```
Quartermaster Integrations v<version> loaded (MLUI: True, AAA: True, ExtraSlots: True, Backpacks: True, BuildMenu: True, EpicLoot: True, PlanBuild: True, WardIsLove: True, QM storage query: True, QM storages: True).
```

* `QM storage query: False` — Quartermaster is older than 0.1.66 or the method was not found; counts will fall back to pockets only. An explanatory `[QM]` error will appear above this line.
* `QM storages: False` — Quartermaster's container list was not found; Epic Loot and PlanBuild cannot take items from chests, and the WardIsLove filter is inactive. Counts still work.
* `EpicLoot: False` with an `[EpicLoot]` error — usually **EpicLootContainerBridge** being installed at the same time (see below).
* A `[Patch] 'something' skipped/failed` line indicates an integration that failed to bind, along with the reason why.

**Numbers look wrong?** The count is limited by three of Quartermaster's own rules. If the display shows pockets only, it is one of these:

* **Range** — a chest outside Quartermaster's `CraftRange` is not counted at all.
* **Chest flag** — the per-chest **Crafting Supply** toggle. A chest with it off is ignored on purpose.
* **Global toggle** — Quartermaster's `CraftFromContainers`. With it off, storage is not offered to crafting at all.

All three are enforced by Quartermaster, not by this mod, so the display follows them exactly.

**EpicLootContainerBridge is not compatible.** Epic Loot sums every registered provider, and that bridge already reports Quartermaster storage — with both installed every cost doubles. The integration detects the bridge and stays off, logging a warning at startup. Remove one of the two; there is no setting to run both.

**Epic Loot's enchanting UI lists storage items?** Epic Loot has a `SpendOnlyStorageMods` setting. Add `bootstrapboys.quartermaster` to it if you want storage to pay costs without showing up as editable inventory in menus that move items around.

**PlanBuild or WardIsLove not taking effect?** Both need `QM storages: True` in the load line plus their own flag in `[Integrations]`. WardIsLove only ever removes containers: a chest outside every ward is never affected, and inside a ward where you are permitted nothing changes.

**`WardHostRange` and a chest that won't count?** Inside a ward, only chests inside that same bubble count, the chest needs Crafting Supply on, must not be in use, and must be one you are permitted on. Outside every ward, crafting follows `CraftRange` exactly as before, including for chests that sit inside a ward — the bubble only bounds the base's own network range.


**Something else?** Set `DebugMode = true`, reproduce the issue, and include both `BepInEx/BSBLogs/QuarterMaster_Integrations.log` and `BepInEx/LogOutput.log` in your bug report.