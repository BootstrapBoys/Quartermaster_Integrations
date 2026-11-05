# Housecarl → QI handoff

**For:** the Quartermaster_Integrations chat.
**From:** the pack consolidation / Housecarl design thread, 2026-11-04.
**Status:** decisions settled on QI's role; no code written in either repo.

You know QI. You do not know Housecarl — that is what this file is for. Section 3 is the work; the
rest is context you need to judge it.

---

## 1. What Housecarl is

A new mod, `BootstrapBoys-Housecarl`: one plugin that absorbs the pack's quality-of-life layer, so one
mod owns one setting and the pack stops having three owners for craft-from-chests and four for loot.

- **58 mods absorbed, 4 pinned as dependencies.** The pinned four are `AAA_Crafting` (kept as the craft
  UI), `MyLittleUI`, `HUDCompass` and `BetterCarts`. They keep every feature; they are not absorbed.
- **Sidecar model.** Housecarl's modules sit *beside* a dependency, never inside it. A pinned mod with
  Housecarl uninstalled behaves exactly as it does today.
- **Three levels of dependency use:** sidecar (own feature beside it), seam (consume a published API),
  bridge (patch mod A so it consults mod B's API — the patch lives in Housecarl).
- **Not built yet.** Ring 1 (interface: HUD, notifications, chat, journals) is the first slice.

## 2. Why this reaches QI

The pack asks one question in many places: *does this player have item X, counting everything they can
reach?* QI already answers it correctly — pockets, ExtraSlots, AdventureBackpacks, Quartermaster
containers in range, ward permissions included. It answers it wherever a mod asks: AAA's max-craft,
MLUI's requirement rows, EpicLoot's inventory provider, PlanBuild's blueprint cost, ExtraSlots' slot
protection, AdventureBackpacks' container. Each of those patches dials the same underlying query, which
is exactly why it should be published rather than re-derived.

Housecarl has the same question in its future modules, and so do the bridges. The wrong outcome is a
second implementation that drifts. The right one is that **QI owns the answer and publishes it**, which
makes QI the pack's storage provider. That is the ask.

This is the same principle the whole project runs on: one owner per knob — applied to a question
instead of a config entry.

## 3. What QI is asked to do

Four items. Only the first two are urgent, and none block Housecarl Ring 1.

### 3a. Add a licence (blocking for everything else)

`BootstrapBoys/Quartermaster_Integrations` has no `LICENSE`. An API with no licence is a promise we
cannot back — MIT is the fit for this pack, and it is compatible with the MIT obligation `MineTogether`
already carries from the VeinMining port. SPDX header per source file.

### 3b. Extract the storage query to one code path

Do **not** add a public method alongside the existing patches. Pull the query out of the patch paths
first, then have both the patches and the facade call it. A facade that diverges from the patches is
worse than no facade, because it will be right in testing and wrong after the first bug fix.

The paths that must converge:

| File | What it does today |
|---|---|
| `aaacrafting_integration.cs` | `CalculateTrueMaxCraft`, `CalculateMaxCraftAmount_Postfix`, batch cap |
| `mlui_integration.cs` | requirement rows |
| `epicloot_integration.cs` | `countItem` / `removeItem` / `removeExactItem` provider |
| `extraslots_integration.cs` | slot protection and slot as a source |
| `adventurebackpacks_integration.cs` | equipped container as a source |
| `pocket_merge.cs`, `stack_crafter_patch.cs` | stack rule when merging |

### 3c. Publish the facade

The semantics in 3d and 3e are also written up as a standalone agreement — `docs/STORAGE-CONTRACT.md` —
because Nisse will be the second implementation and must answer identically. Treat that file as the
normative version and this section as the summary.

Proposed shape — settle the details in your chat, this is not a mandate:

```csharp
namespace BootstrapBoys.QI
{
    public static class StorageApi
    {
        public const int ApiVersion = 1;

        /// True when QI is enabled AND Quartermaster is present.
        public static bool IsReady { get; }

        public static int  Count(string itemName);
        public static bool TryCount(string itemName, out int count);

        /// Can the player pay this recipe at this amount, from all sources?
        public static bool CanAfford(Recipe recipe, int amount);

        /// Spend <amount> of <itemName> across all sources, in the same order the craft path uses.
        /// False and no change when the player cannot pay.
        public static bool TryConsume(string itemName, int amount);

        /// Effective Quartermaster range in metres, for UI that wants to explain itself.
        public static int Range { get; }
    }
}
```

**Call convention: reflection, not a compile-time reference.** Consumers bind `StorageApi` by name,
check `ApiVersion`, and log OK/FAIL at startup. No `BepInDependency` in either direction — QI must stay
optional and client-only, and Housecarl must not become uninstallable without it.

Reflection is acceptable here precisely because the contract is public and versioned; that is the
difference between this and reflecting into internals.

### 3d. Pin the semantics — this is the part that matters

"Have I got X?" is ambiguous, and if the contract does not say, two consumers will answer differently.

State all of these in the XML docs and the README:

- **Sources, in spend order:** player inventory → ExtraSlots (equipment, food, quick) → equipped
  AdventureBackpacks container → Quartermaster containers within `CraftRange`.
- **Filters:** Quartermaster supply permission and ward rules, exactly as the craft path applies them
  today. `IgnoredPickupTypes` is a *pickup* list and must not leak into supply.
- **Absent mods:** sources degrade silently; the answer is whatever remains.
- **Item identity:** shared name string, matching what the crafting panel and the EpicLoot provider
  already use. Document that world level, quality and EpicLoot variants are not distinguished — that is
  a known sharp edge (see `ZenPlayer_Backpack_Bridge`'s `HaveItem(name, true)` bug).
- **What `Count` means:** the same number the crafting UI shows in its have column. If the API and the
  UI can disagree, the API is lying.

### 3e. Failure modes, defined and boring

| Condition | Behaviour |
|---|---|
| Quartermaster absent | `IsReady = false`, `Count = 0`, `Try* = false` |
| QI disabled in config | same |
| Unknown item name | `0` / `false`, no log spam |
| Bound member missing after a QI update | consumer's audit prints FAIL; QI itself keeps working |

Never throw across the API boundary. Never allocate per call.

### 3f. Cost

Expose the cached snapshot QI already maintains and invalidates via `MaxCraftAmount.InventoryChanged`,
not a per-call chest scan. Document the cache lifetime so a consumer knows how stale an answer can be.

### 3g. Config-conflict guard (independent, do it regardless)

At load, if `Vassteel-Quartermaster CraftFromContainers = true` **and** `RossQoL CraftFromChests =
true`, log one line naming the setting to turn off. The refusal pattern already exists at
`epicloot_integration.cs:47` for `EpicLootContainerBridge`.

This is the only QI work item that is about QI's own correctness rather than Housecarl, and it is small.

## 4. What QI must not do

These were decided elsewhere; please do not re-open them without a new reason.

- **Do not fold in `ZenPlayer_Backpack_Bridge`.** It bridges ZenPlayer ↔ AdventureBackpacks with no
  Quartermaster surface, compiles against `AdventureBackpacks.API`, and carries hard `BepInDependency`s.
  It stays its own package.
- **Do not absorb Piecekeeper's category repair or Lettfeti's list caching.** Both have no storage
  surface; they moved to Housecarl's build-menu module. (The earlier "QI takes these" plan is void.)
- **Do not build a craft UI.** `AAA_Crafting` stays the view and owns the amount box and search. QI
  feeds it numbers; it does not replace it.
- **Do not add a second storage answer.** Every new consumer goes through the facade.

## 5. Patch claims

Housecarl will not patch anything QI already patches. QI's current claims, as of 2.7.6:

| Target | QI's patch |
|---|---|
| `InventoryGui.Update` | AdventureBackpacks sort button |
| `InventoryGui.DoCrafting` | craft repaint |
| `AAACraft.CalculateMaxCraftAmount`, `Utilities.CalculateMaxCraftAmount` | storage-inclusive max |
| `Utilities.GetItemsInCloseContainers` | storage candidates |
| `AAABuildMenu.PinnedRecipeTracker.CountItem` | pinned build rows |
| `ExtraSlots.IsGridPositionASlot`, `IsItemInSlot`, `IsProtectedPlayerSlot` | slot protection |
| `PlanBuild.GetInventories` | blueprint cost |
| `WardIsLove.AvailableInContainer`, `CheckAccess` | ward supply |
| `DropMoreLoot.Configure`, `StackBody` | stacking |
| `Quartermaster.InventoryTransfers.Stackable`, `ItemId` | stack rule |
| `EpicLoot.API.RegisterInventoryProvider` | enchant spend (API, not a patch) |

Housecarl's planned claims are presentation only — marker registration, tooltip content. If it ever
wants one of the above, the target moves to one owner; both patching it is the bug this project exists
to remove.

## 6. Release shape

- **2.8.0** — licence, extracted query, facade, 3g guard. One new capability, so a minor bump per the
  existing rule; version in the four usual places; changelog is functional release notes only.
- **No config switch for the API.** It is passive, it cannot tell who is calling, and a switch would
  only mean "consumer sees zeros". `TryConsume` is the only mutating call and it is documented.
- **Do not change existing behaviour** in the same release. If the extraction moves a number, that is a
  bug fix with its own entry.

## 7. Acceptance tests

1. Housecarl with QI absent: `IsReady = false`, no exception in either log.
2. QI present, Quartermaster absent: `IsReady = false`.
3. `Count(item)` equals the crafting panel's have column for the same item, at the same moment.
4. A chest moved outside `CraftRange` drops out of the count.
5. A ward-locked chest is excluded, matching what the panel shows.
6. `TryConsume` spends in the documented order and leaves the remainder untouched when it fails.
7. 100 calls a second produce no per-call chest scan and no allocations worth measuring.

## 8. Does QI still work after the changes?

**Yes, if the extraction is extraction.** Every integration keeps its call site; only the body behind
those call sites moves. The patch inventory should be byte-identical before and after — same Harmony
targets, same order, same postfix signatures. If the patch inventory changes, you have refactored
something else by accident.

Four things can still move silently, and each needs a deliberate check:

| Risk | What to verify |
|---|---|
| **Evaluation order** | The query must scan sources in the same order as today. A different order changes which stack is consumed first, and therefore what a player sees left over. |
| **Cache invalidation** | The count cache must still be invalidated at `MaxCraftAmount.InventoryChanged` and wherever it is invalidated today. One missed invalidation turns into wrong max-craft numbers minutes later. |
| **Spend order** | `TryConsume` is new. It must reuse the craft path's order, not invent one. If the API spends differently from the panel, the panel and the API disagree and the consumer is wrong. |
| **Reentrancy** | This is the real one. See below. |

### Reentrancy

QI already has the mechanism: `AdventureBackpacksIntegration.InQuartermasterSortOrStore`, set around
Quartermaster's sort and store actions so AdventureBackpacks' `Container.TakeAll` / `MoveAll` hooks
cannot dump the backpack into the player's inventory mid-operation.

An API consumer calling `TryConsume` during that window recreates the same bug from the outside, with
no AdventureBackpacks patch to blame. So:

- **`TryConsume` returns `false` while `InQuartermasterSortOrStore` is true.** No queueing, no retry —
  the caller decides what to do.
- **`TryConsume` returns `false` during an active craft or batch queue.** The panel owns the inventory
  at that point; a second spender is how items duplicate or vanish.
- **`Count` may read the cached snapshot at any time.** It is read-only and cannot corrupt anything,
  which is also why it needs no switch.

### Proving it

QI already logs craft snapshots and a patch inventory. Use them as the parity harness:

1. Capture before the refactor, on a save with storage in and out of range: max craft for five
   recipes, have/need rows for five items, EpicLoot provider count and remove results, batch cap
   behaviour at 1, 50 and 100.
2. Refactor.
3. Capture again and diff the **numbers**, not the log text.

If the numbers match and the patch inventory is unchanged, the refactor is behaviour-neutral and the
facade is safe to publish. If a number moves, it is a bug in the extraction, not an acceptable
consequence of adding an API.

## 9. Open questions for your chat

- `TryConsume` at v1, or read-only until a consumer actually needs to spend?
- Should the API expose *which* source answered, for tooltips that want to say "in a chest"?
- Item identity stays name-based — is that defensible, or does v1 need a quality/level-aware overload
  before anyone depends on it?

## 10. If Nisse lands

A separate project (`~/projects/valheim/nisse/`) is building a clean-room replacement for Quartermaster,
with its own storage API. If it reaches craft-from-claim parity, it supersedes this facade and QI retires
to a shim or unlists.

That does not change the work in this file. QI is needed until then, and the facade's **semantics** —
sources, spend order, failure modes — are the specification Nisse will implement. Build it once,
carefully, and it gets reused.

What it should change is ambition: three methods and a version int, not a plugin-registration framework.
If Nisse lands, the wrapper is throwaway and the semantics are not.

## 11. Sequencing

Nothing here blocks Housecarl Ring 1. Recommended order:

1. Licence (today, it is 10 minutes and unblocks everything).
2. Config-conflict guard (small, independent, fixes a real pack risk).
3. Query extraction + facade when Housecarl Ring 2 starts, which is the first consumer.

If Ring 2 slips, the facade waits. The licence and the guard do not.

## Where the rest of this lives

- `../../housecarl/README.md` — Housecarl's scope, rings and roadmap.
- `../../housecarl/DEPENDENCIES.md` — the sidecar model, the bridge contract, and the
  provider obligations this file summarises.
- `../../housecarl/LICENCES.md` — licence tier for every mod in scope, including the four pinned
  dependencies.
