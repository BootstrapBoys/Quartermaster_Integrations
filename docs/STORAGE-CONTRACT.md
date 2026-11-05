# Storage contract — v0.1 (draft)

**Status:** draft. Not yet implemented by anyone.
**First implementation:** `Quartermaster_Integrations` 2.8.0 (bridges Quartermaster's containers).
**Second implementation:** `Nisse` — read-only surface (`Count`, `CanAfford`, `IsReady`,
`ScopeDescription`) at 0.2; `TryConsume` at 0.3, which is when this contract is fully implemented
there. A 0.2 Nisse passes tests 1–3 and 7 but cannot pass 4–6, and that is expected rather than a
conformance failure.
**Consumers:** Housecarl (via its provider adapter), QI's own integrations.

This file is the agreement, not the code. It exists so two mods that will never share an assembly can
still answer the same question the same way. It is deliberately shorter than an API reference: only the
parts that two implementations could disagree about.

## What a consumer asks

One question, in three forms:

1. **How many of item X does this player have access to?** (`Count`)
2. **Can they pay for this recipe at this amount?** (`CanAfford`)
3. **Take X out, wherever it is.** (`TryConsume`)

Everything else is context for rendering an answer.

## Scope

Every operation is evaluated in a **scope**: what counts as "reachable" for this player right now.

| Implementation | Scope |
|---|---|
| QI | Quartermaster containers within `CraftRange`, filtered by supply permission and ward access |
| Nisse | containers inside a nisse's claim, filtered by the same permission and ward rules |

A scope is not a distance. It is "whatever this implementation considers reachable", and the contract
requires only that it be **stable within one call** — a single operation must not see the scope change
underneath it.

## Sources and order

Sources are evaluated in this order for counting, and spent in this order by `TryConsume`:

1. Player inventory
2. Extra slots (equipment, food, quick) — when ExtraSlots is present
3. Equipped AdventureBackpacks container — when AdventureBackpacks is present
4. Scoped storage (Quartermaster containers, or the nisse's claim)

Rules:

- **Absent mods are skipped, not failed.** A missing source contributes nothing and produces no error.
- **The order is part of the contract.** Two implementations that spend in different orders leave
  different remainders, and the player notices.
- **A source that is not permitted contributes nothing**, even if it holds the item. Ward access and
  supply permission are filters, not warnings.

## Operations

### `Count(string item) -> int`

Total quantity across all sources in scope. Zero when the item is absent, the player is absent, or the
implementation is not ready.

### `CanAfford(Recipe recipe, int amount) -> bool`

True only if every requirement of the recipe, multiplied by `amount`, can be satisfied from the sources
in scope — evaluated as one plan, not per-item independently. (A recipe needing 2 of A and 2 of B must
not return true when the player has 2 A and 1 B that overlap.)

### `TryConsume(string item, int amount) -> bool`

All-or-nothing. On `false`, **no source is mutated at all** — including the player's pockets. On `true`,
the sources are debited in the order above and the remainder is left untouched.

### `IsReady`, `ApiVersion`, `ScopeDescription`

`IsReady` is false when the implementation is disabled or its backing storage mod is absent.
`ApiVersion` is an integer that increases on any change to the semantics in this file.
`ScopeDescription` is human-readable, for UI that wants to say what it counted.

## Failure modes

| Condition | Result |
|---|---|
| Implementation disabled | `IsReady = false`; `Count` 0; `CanAfford` false; `TryConsume` false |
| Backing storage absent | same |
| Player absent (headless server) | same — this contract is client-scoped |
| Unknown item name | 0 / false, no log |
| Invalid amount (≤ 0) | 0 / false, no log |
| Bound member missing after an update | the consumer's audit fails; the implementation keeps working |

**Never throw across the boundary.** A consumer calling a storage API during combat is not in a position
to handle an exception.

## Reentrancy

- `TryConsume` returns `false` while the implementation is executing a sort, store or batch operation.
  No queueing: the caller decides whether to retry.
- `TryConsume` returns `false` during an active craft or batch craft owned by the implementation.
- `Count` and `CanAfford` are read-only and may be called at any time, including mid-operation, though
  the answer may be one tick stale.

This is the rule QI already enforces with `InQuartermasterSortOrStore`; Nisse must enforce the equivalent
around its rounds.

## Item identity

**Name-based, by shared name.** Two items with the same shared name count as the same item.

Consequences, accepted for v0.1 and documented rather than hidden:

- world level, quality and durability are not distinguished;
- Epic Loot enchantments on materials are not distinguished;
- a level-2 item satisfies a requirement that a level-1 item would also satisfy.

This matches what the crafting panels already do. If that changes, `ApiVersion` changes with it.

## Versioning

| Change | Version |
|---|---|
| New operation, or a new optional field on an existing one | minor bump |
| Change to source order, spend order, filtering or all-or-nothing behaviour | **major bump** — consumers must re-verify |
| Bug fix that makes the implementation match this document | patch |

`ApiVersion` is independent of the mod version. A consumer checks `ApiVersion`, not the plugin version.

## Conformance

An implementation is conformant when it passes these, in a real world with real chests:

1. `Count` equals the "have" number the crafting panel shows for the same item at the same moment.
2. A container moved outside the scope drops out of the count.
3. A ward-locked container contributes nothing.
4. `TryConsume` on an unaffordable amount mutates nothing.
5. `TryConsume` debits pockets before slots before backpack before storage.
6. `TryConsume` during a sort or batch returns `false` and mutates nothing.
7. `Count` never throws, for any string, at any time, including before the world is loaded.

Tests 1 to 3 are the ones that matter. If two implementations disagree on any of them, one is wrong and
this document does not yet say which.

## Deliberately not in v0.1

- Quality- or level-aware counting.
- Enumerating sources for UI ("in a chest", "in the backpack").
- Counting what is reachable but not permitted (read-only inspection).
- Server-side scope. This is a client-side contract; a dedicated server has no "nearby" for a player.

Each of these becomes a v0.2 conversation when a consumer actually needs it.
