# Quartermaster Integrations

Valheim BepInEx mod that brings nearby **Quartermaster** storage into the crafting UI: requirement
rows show what you actually hold across pockets and chests, max-craft accounts for it, and batch
crafting is gated on what a craft can really pull.

Player-facing documentation — features, config, integrations, troubleshooting — lives in
[`resources/README.md`](resources/README.md). Release history is in
[`resources/CHANGELOG.md`](resources/CHANGELOG.md).

## Layout

| Path | What it is |
| :--- | :--- |
| `src/qi_main.cs` | Plugin entry point, Harmony patches, storage-query binding |
| `src/qi_debugger.cs` | All diagnostics: log file, patch inventory, craft snapshots |
| `src/*_integration.cs` | One file per soft dependency (AAA Crafting, My Little UI, ExtraSlots, AdventureBackpacks, AAABuildMenu, Epic Loot, PlanBuild, WardIsLove) |
| `resources/` | Packaging metadata: `manifest.json`, README, CHANGELOG, icon |
| `tooling/build.sh` | Build → binding audit → drop candidate in `artifacts/debug` → clean |
| `tooling/decompile.sh` | Decompile a dependency DLL for reading (output to `/tmp`) |
| `tooling/build-check/` | Stub-compile harness and the reflection-binding audit |
| `refs/`, `artifacts/` | Local only — not in this repo (see below) |

## Building

Requires the .NET SDK 8 and the game assemblies (they are not redistributable, so they are not
committed):

```
refs/game/valheim_Data/Managed/   assembly_valheim.dll, UnityEngine*.dll, Unity.TextMeshPro.dll, assembly_utils.dll
refs/game/BepInEx/core/           BepInEx.dll, 0Harmony.dll
refs/mods/                        Quartermaster, AAA Crafting, My Little UI, ExtraSlots, AdventureBackpacks
```

With those in place:

```bash
./tooling/build.sh
```

That compiles with warnings-as-errors, runs every reflection binding against the real assemblies,
copies the DLL to `artifacts/debug/`, and deletes the build output. It fails loudly if a binding
the mod relies on no longer exists — which is the point, because those failures are silent at
runtime.

## Compatibility

Optional integrations are reflection-bound at startup and report what they found in the load line;
none of them are hard dependencies. The only hard dependency is Quartermaster.

## Versioning

Version lives in four places — `PluginVersion` in `src/qi_main.cs`, the csproj (`Version`,
`AssemblyVersion`, `FileVersion`), `resources/manifest.json`, and the top `resources/CHANGELOG.md`
heading. Fixes bump the patch digit; a new integration bumps the minor and resets the patch.
`Awake()` warns in the log if the built assembly disagrees with `PluginVersion`.
