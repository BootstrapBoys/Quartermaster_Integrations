# Changelog

All notable changes to the **Quartermaster Integrations** mod will be documented in this file.

Every version that gets built is listed here, newest first — including builds that were tested but never uploaded. Unreleased versions are kept for history.

---

## [2.7.6] - 2026-11-03

### Added

* **Stamped materials stack again.** Some admin panels stamp items with an `ap_prov` provenance entry, which made stamped and unstamped fine wood (and anything else stamped) refuse to merge. `IgnoredStackTags` (4. Patches, default `ap_prov`) lists custom-data keys to ignore for stackable items; every other entry, quality, world level and durability must still match.

### Added

* **Epic Loot materials stack again.** Epic Loot stamps its `MagicItemComponent` onto essences and other materials, not just gear, and Quartermaster treats any custom-data difference as a different item - so an enchanted Essence would not merge with a plain one. `IgnoreEpicLootComponents` (4. Patches, default on) ignores that component when the item is stackable; gear keeps its real enchant payload and every other custom-data entry still has to match.

### Added

* **Chest dock actions now match what the chest allows.** `Chest Config` is hidden on chests your character cannot write - Quartermaster used to offer it and then silently drop the settings - and `Sort Chest` now claims and sorts any chest Quartermaster can reach but does not own, the same ownership claim vanilla makes when you open a chest, rather than handing it to the shared-chest handshake that needs the owner present. Works while the owner is online but elsewhere; chests another player has open are left to Quartermaster. Controlled by `AnyChestDock` (4. Patches, default on).

### Added

* **Sorting your inventory now merges duplicate stacks.** Quartermaster's sort only rearranges slots, and its merge routine runs for chests but never for pockets, so two stacks of the same iron sat untouched however often you sorted. `MergePocketsOnSort` (4. Patches, default on) combines identical pocket stacks first - honouring `IgnoreCrafter` and `IgnoredStackTags`, skipping equipped items - and then lets Quartermaster arrange the compacted result.

### Fixed

* **The recipe list no longer stays dimmed after a craft.** Crafting consumes materials, but the list was only painted when it was built, so every recipe could keep looking unaffordable while the craft button - which reads live counts - still worked. The panel now repaints once each craft finishes, with the count cache invalidated first.

### Changed

* **Ward ownership is now a three-way setting, under its own `5. Ward` category.** `WardOwnership` replaces the old skip switch: `Never` leaves every chest with the ward owner (deposits and machine collection by a visitor fail), `IfOwnerOffline` (default) skips only while the recorded owner is connected and lets Quartermaster claim when that owner is away, so deposits and machine output keep working, and `Always` is Quartermaster's normal request/claim. `WardHostRange` moved into the same category.

---

## [2.7.5] - 2026-11-02

### Added

* **Ward-permitted containers keep their owner.** Quartermaster requests ownership of every chest it reads and claims it after three unanswered requests, which in a shared base lets a permitted visitor take the ward owner's chests. `WardPermittedSkipsOwnership` (default on) makes containers inside a ward you are permitted on skip that handshake entirely; counts still read from the ZDO, while deposits and machine collection still require real ownership.

### Removed

* **The Wizardry integration is gone.** Wizardry's wizard table, arcane forge, weaving loom and potion cauldron already pull from Quartermaster storage through the normal `CraftRange`, with no special handling - verified in game with the integration switched off. Its switch under 2. Debug is gone with it; nothing else changes.

---

## [2.7.4] - 2026-11-01

### Added

* **Each integration has its own switch under 2. Debug.** Ten toggles - AAA Crafting, AAA Build Menu, My Little UI, ExtraSlots, Adventure Backpacks, Epic Loot, PlanBuild, WardIsLove, Wizardry and DropMoreLoot - all on by default. Turning one off leaves that mod alone immediately, without a restart, which also makes it possible to check whether an integration is needed at all.

* **Forget can remove a chest entry outright.** Quartermaster's Forget button does not forget: it moves the item into the chest's ignore list, so the row stays on screen as "(ignored)" and the chest keeps refusing to store it - which reads as a routing failure. New setting `ForgetRemovesEntry` (default off) makes Forget and Forget All delete the entry from the chest, so the row disappears and putting a sample back teaches the chest from scratch.

* **Materials stack across crafters when enabled.** Quartermaster will not merge a crafted stack with a looted one, so core wood crafted at a bench stays apart from chopped core wood. `IgnoreCrafter` (default off) merges them the way vanilla does, but only when the crafter is the sole difference - quality, variant, world level, durability, cheat flag and custom data must still match, so gear, enchanted items and world-levelled stacks keep Quartermaster's rule.

* **The ledger's stack-size toggle names DropMoreLoot while it is in charge.** Instead of a notice line at the bottom of the tab, "Use custom stack sizes" is disabled and labelled with DropMoreLoot's multiplier, and the maximum field and Save button are disabled with it - the tab can no longer offer a setting Quartermaster is not applying.

### Fixed

* **Wizardry: all four crafting tables reach storage.** Detection used Wizardry's own station fields, but its plugin assigns `WizardTable` at every build-piece registration and never fills `ArcaneForge`, `WeavingLoom` or `PotionCauldron` - so three stayed null and only the potion cauldron (the last assignment) was ever recognised. Stations are now matched by their `_TW` prefab name, which covers all seven Wizardry station pieces.

---

## [2.7.3] - 2026-11-01

Internal only.

---

## [2.7.2] - 2026-11-01

Internal only.

---

## [2.7.1] - 2026-11-01

### Fixed

* **Wizardry recognised only one of its four crafting tables.** The station list was resolved once and locked on the first match, so whichever of WizardTable, ArcaneForge, WeavingLoom or PotionCauldron registered first became the whole set and the rest still showed pockets only. The list is now re-read on use, and the debug line reports how many of the four it knows.

---

## [2.7.0] - 2026-11-01

Two integrations, one minor bump.

### Added

* **Wizardry** (optional, `Therzie.Wizardry` 1.2.3+). While you craft at one of its tables (WizardTable, ArcaneForge, WeavingLoom, PotionCauldron), storage within Quartermaster's BaseRange of that table counts on top of CraftRange around you, so a table set away from the chest wall can still use the base's materials. The tables are ordinary crafting stations, so this is range only - CanSupply, the chest's Crafting Supply flag and ward permissions still decide what is usable.
* **DropMoreLoot** (optional, `TastyChickenLegs.DropMoreLoot` 1.2.9+). New `1. General > DeferToDropMoreLootStacks` (default on): when DropMoreLoot's own stacking is enabled, its stack multiplier becomes the authority and Quartermaster stops applying MaximumStackSize. The ledger's stack tab now says which mod owns stack sizes instead of showing a number that is not in effect.

---

## [2.6.4] - 2026-11-01

### Fixed

* **WardIsLove wards were never seen, so every ward rule silently did nothing.** WardIsLove replaces the vanilla guard stone with its own prefab, so its wards are absent from `PrivateArea.m_allAreas` - the list that was being read. Wards now come from WardIsLove's own `WardMonoscript.m_wardInstances`, and a ward switched off in its UI is ignored.

---

## [2.6.3] - 2026-11-01

Internal only: dead code and redundant work removed. No functional or display changes.

---

## [2.6.2] - 2026-10-31

### Added

* **WardIsLove hosts the range for bases inside it** (`1. General > WardHostRange`, default on). Three rules, all measured by the ward bubble rather than by a radius:
  * A **Deposit Chest inside a ward** runs its network on that ward's bubble: the bubble replaces `BaseRange`, so the base covers exactly the warded area and the deposit chest drops out of range the moment you step outside it, however close you are.
  * **While you are inside a ward**, only chests inside that same bubble count for crafting - `CraftRange` no longer reaches past the bubble.
  * **Outside every ward**, crafting is Quartermaster's `CraftRange` unchanged - a chest inside a ward is still reachable from outside if it falls within that range. Only the base's own network range is bubble-bound.
  * **A Deposit Chest outside every ward** keeps Quartermaster's `BaseRange` and `CraftRange` exactly as configured.
  * Two deposit chests sharing a ward are merged into a single network, regardless of their spacing.
  * Ward permissions still decide, and chests with Crafting Supply off are still ignored.

---

## [2.6.1] - 2026-10-31

The remaining three integrations from the compatibility review, plus the review pass they triggered.

### Added

* **Epic Loot** (optional, `randyknapp.mods.epicloot` 0.15.5+). Registers a Quartermaster inventory provider, so enchanting and sacrifice costs can be paid from nearby storage and the materials are actually taken out of the chests.
  * This one moves items: counts come from Quartermaster's own availability query, and removal walks Quartermaster's spendable-container list, so CraftRange, the per-chest Crafting Supply flag and ward access still apply.
  * Refuses to register when **EpicLootContainerBridge** is installed - Epic Loot sums every provider, and two providers would double every chest total.
  * No save handler: it can pay costs, but never accepts items to store.
* **PlanBuild** (optional, `marcopogo.PlanBuild` 0.20.0+). Plan pieces can be built from storage, including "hold to add materials".
  * Implemented as a postfix on `PlanPiece.GetInventories`, which both the affordability check and the material-adding path walk, so what is counted and what is spent cannot disagree.
* **WardIsLove** (optional, `Azumatt.WardIsLove` 4.0.9+). Chests inside a ward the player is not permitted on are excluded from storage counts and are never pulled from.
  * Quartermaster applies only the vanilla private-area check, which a ward can be configured to bypass; this puts the ward's own decision back in charge.
  * Filter only: it never adds a container, and a chest outside every ward is unaffected.

### Changed

* Quartermaster's spendable-container list is resolved once per frame instead of on every item query, so Epic Loot's provider and PlanBuild's hover check no longer re-walk the chest list per call.

### Fixed

* The Epic Loot provider is unregistered when the plugin unloads. Epic Loot keeps providers in a static list, so a reload used to leave the old callbacks registered and add every storage count twice.

---

## [2.5.0] - 2026-10-30

First of the three integrations from the compatibility review. A new integration is a minor bump, so the patch digit resets to zero.

### Added

* **AAABuildMenu pinned-recipe counts** (optional, `Azumatt.AAABuildMenu` 1.0.6+). Pinned build rows render `have/need` from `PinnedRecipeTracker.CountItem`, which sums the player's pockets only. A postfix on that single method adds Quartermaster's storage count when the inventory passed in is the player's own, so pinned rows show the same numbers as the crafting window.
  * Display-only: both call sites compare the value against `row.ResNeeds` to choose a colour and nothing else reads it, so this cannot change what a build spends.
  * Gated to the player inventory by reference — a container inventory already counts its own contents, and adding nearby storage on top would double-count.

---

## [2.4.4] - 2026-10-29

Internal only: dead code, duplicated work and per-frame waste removed. No functional or display changes.

---

## [2.4.3] - 2026-10-28

Follow-up to in-game testing of 2.4.2.

### Fixed

* **MaxCraft showed `1` with an empty inventory**: AAA floors its own answer at `Mathf.Clamp(num, 1, 999)`, and the previous "only ever raise" rule preserved that floor. The postfix now reports **0** when its own count and `HaveRequirements` both agree nothing can be crafted — while still reporting `1` for recipes whose requirements are all unknown (modded upgrade tokens), and still leaving AAA's `0` alone when the crafting station is missing or under-levelled. The upgrade tab likewise reports 0 when the next quality is unaffordable.

---

## [2.4.2] - 2026-10-28

### Fixed

* **Batch `-` Button Lock-Up (Regression)**: `ForceEnableBatchButtons` gated the decrement on affordability, re-creating the trap that 2.1.6 claimed to fix. `-` is enabled whenever the quantity is above 1, so you can always step back down to a craftable amount.
* **Craft Button Latching**: The batch enforcement postfix no longer derives "can craft one" from `m_craftButton.interactable`. That field is written by the same postfix, so it was effectively comparing its own previous decision against itself and could stay enabled after the materials ran out.
* **MaxCraft Reported 0 For Every Recipe** when the selected recipe could not be resolved (renamed field or unknown pair type). AAA's own result is now left untouched instead of being zeroed.
* **MaxCraft Ignored AAA's Crafting-Station Gate**: the max-craft postfix now only *raises* AAA's own figure and never lowers it or revives a `0`. `Utilities.CalculateMaxCraftAmount` returns `0` when the recipe needs a crafting station you are not standing at, or one below the required level; the previous full replacement could report a batch of 100 in that state.
* **Pocket Counts Double-Added to AAA**: `GetItemsInCloseContainers` is container-only — its callers add the pocket count themselves — so the postfix now adds our *storage* count instead of a pocket-inclusive total, which had been counting pockets twice in both the requirement display and max-craft.
* **MaxCraft Over-Reporting**: `CalculateTrueMaxCraft` no longer floors the result at `1` when a requirement is provably short. Recipes whose requirements are all unknown (modded dummy upgrade tokens) still report `1` and remain craftable.
* **ExtraSlots Items Could Be Lost Or Share A Cell**: protected items are re-added before anything that can throw, and items the sort placed onto a protected slot are relocated instead of leaving two items in one grid cell.
* **`Inventory.Changed` Invalidation Storm**: cache invalidation is now a dirty flag and is skipped while the crafting UI is closed, so container activity elsewhere in the world no longer forces cache rebuilds and a recipe-list invalidation for AAA on every item move.
* **Silent Patch Failures**: every reflection patch goes through `HarmonyPatchGuard`, which logs which target failed instead of aborting the remaining patches for that integration.
* **Latching Reflection Lookups**: `GetSelectedRecipeData` no longer caches a failed property resolve, so a renamed field cannot permanently disable max-craft and batch enforcement.

### Changed

* **Storage counts come from Quartermaster itself**: `GetQuartermasterStorageCount` now binds `WarehouseService.CountAvailableNearPlayer(string, int, bool)` instead of walking every `Container` in the scene. Counts therefore honour `CraftRange`, the per-chest **Crafting Supply** flag, the global `CraftFromContainers` toggle, ward/in-use rules and world-level matching — i.e. exactly what Quartermaster will spend at craft time.
* **Craft button defers to Quartermaster's requirement check**: `canCraftSingle` / `canBatch` now use `Player.HaveRequirements` (the 3- and 4-argument overloads), which Quartermaster transpiles to be warehouse-aware. Our own fallback count could enable a craft for chests Quartermaster would refuse to pull from.
* **ExtraSlots protection delegated to Quartermaster**: the remove-from-item-list / clamp `m_height` / restore cycle is gone. Quartermaster's `SortInventory` already honours `ExternalSlotCompatibility` and its own sort button passes `preserveEquipped: true`; this mod now only postfixes `IsProtectedPlayerSlot`, so a failed reflection probe inside Quartermaster cannot cost the player their slot contents.
* **AdventureBackpacks proxy block retargeted**: `StoreAllInOpenChest` is `(Player, Container) : int` in Quartermaster 0.1.66. The old `(Container, ref int)` shape never bound, so the backpack proxy was never actually blocked.
* **New `DeferToOtherFormatting` option** (`1. General`, default `false`): when AAA Crafting or AdventureBackpacks has already decorated a requirement row, leave their text alone. Vanilla's own text is a bare number, so the check only triggers on another mod's formatting.
* **`SetupRequirement`'s sixth parameter is now used**: the game passes `craftMultiplier` to `SetupRequirement` directly, so the display prefers it and only falls back to reading AAA Crafting / My Little UI when it is 1.
* Batch amount is memoized per frame, cutting repeated reflection reads from once per requirement row to once per frame.
* Insufficient-material flashing is quantized to roughly 12 Hz to avoid a TextMeshPro mesh rebuild for every row on every frame.

---

## [2.4.1] - 2026-10-28

### Changed

* **Safe ExtraSlots List Manipulation**: Replaced `inventory.RemoveItem(item)` during Quartermaster sort protection with direct backing-list removal (`items.RemoveAt(i)`), preventing premature `Inventory.Changed()` events, accidental item un-equipping, or AdventureBackpacks "Yard Sale" triggers.

### Fixed

* **AdventureBackpacks Proxy Isolation**: `AB_BackpackProxy` containers are excluded from nearby-container scans, so backpack contents are never double-counted as world chests, and sorting the main inventory never pulls items out of an equipped backpack.
* **Sort Button Visibility with Open Backpack**: Quartermaster's Sort button stays active and visible while the AdventureBackpacks inventory panel is open.

---

## [2.4.0] - 2026-10-27

### Added

* **ExtraSlots Integration (`shudnal.ExtraSlots`)**: protects ExtraSlots rows (`y >= InventoryHeightPlayer`) and slot items (Food, Ammo, Quick, Misc, Equipment, and Extra Utility slots) from Quartermaster's `SortInventory` and `StoreAll` actions.
* **AdventureBackpacks Integration (`vapok.mods.adventurebackpacks`)**: initial soft-dependency support to detect `AB_BackpackProxy` containers.

---

## [2.3.1] - 2026-10-27

### Changed

* **Renamed for Expanded Scope**: Renamed the mod due to its expanded scope integrating additional mods. Superseded the same day by 2.4.0; kept here because this version was published.

---

## [2.3.0] - 2026-10-27

### Changed

* **Mod Scope & Renaming**: Renamed the mod namespace, assembly, and identity from *MyLittleUI Quartermaster Bridge* to **Quartermaster Integrations** (`com.bootstrapboys.quartermaster_integrations`).
* **Soft Dependency Refactoring**: Converted **My Little UI** (`shudnal.MyLittleUI`) from a hard dependency to a soft dependency, ensuring the mod functions with vanilla Valheim, Quartermaster, and AAA Crafting even when My Little UI is not installed.

---

## [2.2.6] - 2026-10-27

### Changed

* **Standardized Plugin GUID**: Updated `PluginGUID` from `com.bootstrapboys.valheim.mlui_qm_bridge` to `com.bootstrapboys.mlui_qm_bridge`.

---

## [2.2.5] - 2026-10-26

### Added

* **Strict Batch Limit Enforcement**: respects the hard batch ceiling of `100` for **AAA Crafting** (`v2.1.12`) and `99` for **MyLittleUI** (`v1.2.32`) across input parsing, `MaxCraft` calculations, and `+`/`-` button states.

---

## [2.2.4] - 2026-10-26

### Added

* **AAA Crafting v2.1.12 Compatibility**: reflection binding to `RecipeListPerfCache.Invalidate()` so container and inventory changes immediately refresh paginated recipe grids.

---

## [2.2.3] - 2026-10-26

### Fixed

* **MyLittleUI v1.2.32 Compatibility**: Updated the reflection binding for `MyLittleUI.MultiCraft.amount` with explicit `BindingFlags.NonPublic | BindingFlags.Static` after it was changed to `private`.

---

## [2.2.2] - 2026-10-26

### Fixed

* **Weapon & Armor MaxCraft Calculation**: Fixed `CalculateTrueMaxCraft` being artificially capped at `1` for weapons and armor by evaluating tier-1 ingredient amounts via `req.GetAmount(1)` and skipping upgrade-only (`<= 0`) or unknown dummy token requirements.

---

## [2.2.1] - 2026-10-26

Internal only: debug guard cleanup. No functional changes.

---

## [2.1.9] - 2026-10-26

### Fixed

* **Modded Dummy Upgrade Tokens**: Fixed recipes from mods (such as Therzie's Warfare/Armory/Monstrum) showing `MaxCraft: 0` and a disabled Craft button due to hidden/unlearned upgrade token requirements (`$item_upgrader_...`). Single-craft eligibility now defers to Valheim's Quartermaster-transpiled `player.HaveRequirements`.

---

## [2.1.6] - 2026-10-26

### Fixed

* **Batch `+` and `-` Button Lockup**: Fixed an issue where exceeding available resources for a batch disabled the `-` button, trapping the player at an uncraftable batch quantity. `ForceEnableBatchButtons` runs at `Priority.Last` after AAA Crafting.
* **Upgrade Tab Support**: Fixed the Upgrade button becoming disabled when a batch multiplier `> 1` was left over from the Craft tab.

---

## [2.1.3] - 2026-10-26

### Added

* **AAA Crafting MaxCraft Integration**: Patched `Utilities.GetItemsInCloseContainers`, `AAACraft.CalculateMaxCraftAmount`, and `Utilities.CalculateMaxCraftAmount`, and enabled `AzuAntiArthriticCraftingPlugin.CfcLoaded` so Quartermaster storage is included in `MaxCraft` and `RecipeTrackerUI`.
* **Configurable Display Modes & Visuals**: Added `RequirementDisplayMode` (`CombinedTotal`, `SplitInventoryAndStorage`, `TotalWithSplit`), customizable sufficient resource colors, and configurable red pulsing alerts when lacking batch materials.

---

## [1.0.56] - 2026-10-25

### Fixed

* Stabilized Quartermaster container scanning (`GetQuartermasterStorageCount`) and eliminated double-counting in `InventoryGui.SetupRequirement`.

---

## [1.0.0] - 2026-09-15

### Added

* Initial release of **MyLittleUI Quartermaster Bridge**.
* Added Harmony postfix on `InventoryGui.SetupRequirement` to append nearby chest inventory counts to MyLittleUI requirement labels while respecting `PrivateArea.CheckAccess` ward permissions.
