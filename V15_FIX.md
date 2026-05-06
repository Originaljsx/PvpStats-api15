# PvpStats — Dalamud API v15 fix

This document records the exact changes that get [wrath16/PvpStats](https://github.com/wrath16/PvpStats) loading and recording matches again on Dalamud API 15.

## What broke and why

`Dalamud.NET.Sdk/15.0.0` was published on **2026-04-29**. The release renamed/replaced enough API surface that PvpStats v2.6.1.1 (which targets `Dalamud.NET.Sdk/14.0.1`) refused to load.

The hits inside PvpStats:

- `ObjectKind.Player` removed → renamed to `ObjectKind.Pc`
- `OnTerritoryChanged(ushort)` signature → now `(uint)`
- `IDutyState` events now take `IDutyStateEventArgs` instead of `(object?, ushort)`
- `ClientState.LocalPlayer` removed → moved to `ObjectTable.LocalPlayer` (also `IPlayerState`)
- `ImRaii.Color` non-disposable form removed → must use `ImRaii.ColorDisposable`
- `AtkValue.Type` enum members renamed to `AtkValueType.*` and the property is now nullable
- The CC action-effect signature scan no longer resolves; replaced with a CS-resolved member pointer
- Plugin manifest now requires a `DalamudApiLevel` field
- New SDK targets `.NET 10`, not `.NET 8`

Plus a non-API change: patch 7.5 added the **Archeia Harmonias** CC arena, which the manager needed to recognize.

## v2.6.2.1 — InternalName rename

The original v2.6.2.0 release shipped with the upstream `InternalName: "PvpStats"`. Dalamud's plugin installer dedupes by `InternalName`, so the custom-repo entry was hidden behind the official Dalamud-repo upstream entry. Fixed in v2.6.2.1 by renaming:

- `PvpStats/PvpStats.csproj`: added `<AssemblyName>PvpStatsApi15</AssemblyName>`
- Source manifest renamed `PvpStats/PvpStats.json` → `PvpStats/PvpStatsApi15.json`
- `InternalName: "PvpStats"` → `InternalName: "PvpStatsApi15"`
- Display `Name: "PvP Tracker"` → `"PvP Tracker (API 15)"`
- `Author` and `RepoUrl` updated to credit the fork
- `IconUrl` repointed to the fork's `images/icon.png`
- pluginmaster.json's `InternalName`, `Name`, `AssemblyVersion` mirrored

Note: the rename changes the plugin's config directory from `%AppData%\XIVLauncher\pluginConfigs\PvpStats\` to `…\PvpStatsApi15\`. To preserve existing match history, copy the contents of the old folder to the new one once after installing.

## The original fix is two commits on a new branch

Branch: `api15-fix`, based on upstream `master` at `5369a7a Fix 7.5 periods`.

### Commit 1 — `Merge PR #12: Dalamud API 15 compatibility` (`6e49d7e`)

Merge of [PR #12 by `rennalol`](https://github.com/wrath16/PvpStats/pull/12) — open, mergeable_state=clean, +136/−102 across 16 files. Every breaking change above except the manifest field is fixed by this PR. Files touched:

| File | What it does |
|---|---|
| `PvpStats/PvpStats.csproj` | `Dalamud.NET.Sdk` 14.0.1 → 15.0.0 |
| `PvpStats/Helpers/MatchHelper.cs` | Adds Archeia Harmonias arena (casual 1357-1358, ranked 1103-1112, custom 1113); updates `GetMatchType()` and `GetArenaName()` |
| `PvpStats/Managers/Game/CrystallineConflictMatchManager.cs` | Biggest single change. `ProcessPacketActionEffectDelegate` → `ActionEffectHandler.Delegates.Receive`; `[Signature(...)]` attribute → `InteropProvider.HookFromAddress(ActionEffectHandler.MemberFunctionPointers.Receive, ...)`; `ActionAnimationId` → `SpellId`; `EffectCount` → `NumTargets`; `ObjectKind.Player` → `ObjectKind.Pc`; refactor of effect-type checks into a switch |
| `PvpStats/Managers/Game/FrontlineMatchManager.cs` | Territory `ushort` → `uint`; duty completion handler signature; `ObjectKind.Pc` |
| `PvpStats/Managers/Game/MatchManager.cs` | Same territory + duty event signatures |
| `PvpStats/Managers/Game/RivalWingsMatchManager.cs` | Same |
| `PvpStats/Services/AtkNodeService.cs` | `ValueType.*` → `AtkValueType.*`; `.HasValue` null check |
| `PvpStats/Services/GameStateService.cs` | `ClientState.LocalPlayer` → `ObjectTable.LocalPlayer`; `ObjectKind.Pc` filter |
| `PvpStats/Types/Match/MatchEnums.cs` | New arena enum value |
| `PvpStats/Windows/DebugWindow.cs` | `ImRaii.Color` → `ImRaii.ColorDisposable` |
| `PvpStats/Windows/Detail/CrystallineConflictMatchDetail.cs` | Same ImRaii rewrite |
| `PvpStats/Windows/Detail/FrontlineMatchDetail.cs` | `using Dalamud.Interface.Windowing` |
| `PvpStats/Windows/Detail/RivalWingsMatchDetail.cs` | Same |
| `PvpStats/Windows/Tracker/FLTrackerWindow.cs` | Same |
| `PvpStats/Windows/Tracker/RWTrackerWindow.cs` | Same |
| `PvpStats/packages.lock.json` | Dependency lock rewrite |

### Commit 2 — `Add DalamudApiLevel=15, bump version, update changelog` (`7c23a75`)

PR #12 didn't update the plugin manifest. v15 requires `DalamudApiLevel` to be declared.

`PvpStats/PvpStats.json`:
```diff
 "ApplicableVersion": "any",
+"DalamudApiLevel": 15,
-"Changelog": "* Text alignment changes.",
+"Changelog": "* Updated for Dalamud API 15.",
```

`PvpStats/PvpStats.csproj`:
```diff
-<Version>2.6.1.1</Version>
+<Version>2.6.2.0</Version>
```

## Gap-check (everything PR #12 + commit 2 doesn't touch is clean)

For the record — every other v15 breaking-change pattern was grepped against the rest of the codebase:

| v15 change | PvpStats usage | Status |
|---|---|---|
| `ClientState.LocalContentId` removed | not used | clean |
| `ICharacter.Customize` byte[]→Span<byte> | not used | clean |
| `IPartyMember.ContentId` uint→ulong | not used (`IPartyList` injection commented out in `Plugin.cs`) | clean |
| Enum renames `BeastChakra`/`CardType`/`NamePlateKind`/`HoverActionKind` | none | clean |
| Chat tuple-handler API | not used | clean |
| Festival API | not used | clean |
| `if (ImRaii.Group/Tooltip/Disabled(...))` block-conditional pattern | none | clean |

`IAsyncDalamudPlugin` is **optional** in v15 (the v15 doc calls it "currently still experimental"). The synchronous `IDalamudPlugin` continues to work; PvpStats stays on it.

## Build environment

- **.NET 10 SDK** required (the Dalamud v15 SDK targets `net10.0`). On Windows, install from https://dotnet.microsoft.com/download/dotnet/10.0 or via `winget install Microsoft.DotNet.SDK.10`. Confirmed working with `10.0.203`.
- **.NET 8 runtime** stays present for other tools but isn't enough on its own — the SDK is what matters.
- **LiteDB** 5.0.16 — vendored via NuGet, no separate install.

## Build steps

From the repo root (`C:\Users\Jacob\ccstats\PvpStats` in this session):

```powershell
git checkout -b api15-fix master
git fetch origin pull/12/head:dalamud-api-15
git merge --no-ff dalamud-api-15 -m "Merge PR #12: Dalamud API 15 compatibility"
# manually apply the manifest patch from commit 7c23a75 if reproducing from scratch
& "C:\Program Files\dotnet\dotnet.exe" build PvpStats.sln -c Release
```

Output lands at:
- `PvpStats\bin\x64\Release\PvpStats.dll` — the loose plugin assembly
- `PvpStats\bin\x64\Release\PvpStats\latest.zip` — the packaged distribution zip (used by the third-party repo install path below)

The build emits 164 warnings, all pre-existing nullable-reference warnings in the upstream code. The `_ccMatchEndHook never assigned` family is also expected — those fields are populated at runtime via `[Signature]` attributes and `IGameInteropProvider.InitializeFromAttributes(this)`.

## Install for local dev (devPlugin)

```powershell
$dest = "$env:APPDATA\XIVLauncher\devPlugins\PvpStats"
New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item -Recurse -Force "C:\Users\Jacob\ccstats\PvpStats\PvpStats\bin\x64\Release\*" $dest
```

Then in-game: `/xlsettings` → Experimental → "Dev Plugin Locations" → add the full path to the DLL:

```
C:\Users\Jacob\AppData\Roaming\XIVLauncher\devPlugins\PvpStats\PvpStats.dll
```

Reload plugins (or restart the game). `/xllog` should show:

```
[LocalPlugin] Loading PvpStats.dll
[LocalPlugin] Creating plugin instance for "PvpStats" (async=False)
[LocalPlugin] Finished loading "PvpStats"
[PvpStats] PvP Tracker initialized.
```

## Install for end users (third-party repo)

Anyone can install the fix without building it themselves by adding a custom Dalamud plugin repo:

1. `/xlsettings` → Experimental → "Custom Plugin Repositories"
2. Add the URL of the `pluginmaster.json` file in this fork's repo (see the README for the exact link)
3. Save → Custom repos refresh automatically; PvP Tracker now appears in `/xlplugins` under the repo's source

Make sure the repo manifest's `DalamudApiLevel` matches the running Dalamud version, otherwise the plugin won't surface.

## In-game verification (what passed)

1. Plugin loads cleanly — `Finished loading "PvpStats"` in `/xllog`, no `SignatureException`, no `Failed to initialize`
2. All four slash commands work: `/pvpstats`, `/ccstats`, `/flstats`, `/rwstats`
3. One Crystalline Conflict match played end-to-end, populated correctly in `/ccstats` with scoreboard data
4. The only warning during the match was a `[HITCH] Long "UiBuilder(PvP Tracker)" detected, 100.4447ms > 100ms` — pre-existing perf nag in the CC tracker render path, not a v15 regression

## Known risks (still applicable after this fix)

- **Action-effect hook against FFXIVClientStructs 7.5.** PR #12 replaces the old signature scan with a CS-resolved `ActionEffectHandler.MemberFunctionPointers.Receive` pointer. More stable than a sig scan, but a future CS struct-layout drift can still silently break the kill feed. Symptom: scoreboard parses fine, kill rows are empty.
- **LiteDB multi-client lock ([Issue #8](https://github.com/wrath16/PvpStats/issues/8)).** Pre-existing. Two FFXIV clients hitting the same `data.db` can trigger LiteDB file-lock issues on some Windows setups. A subsequent rewrite with a different DB engine is the planned fix.
- **Vestigial dependencies.** `IPartyList` and `ISigScanner` are still injected into `Plugin.cs` but barely used after PR #12. Cosmetic.
