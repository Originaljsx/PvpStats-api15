# PvP Tracker — Dalamud API 15 fork

> **This is a community fork of [wrath16/PvpStats](https://github.com/wrath16/PvpStats)** that restores compatibility with Dalamud API 15 (broken on 2026-04-29 by the v15 SDK release). All credit for the plugin goes to the original author SaMo. See [`V15_FIX.md`](V15_FIX.md) for the exact list of changes — the bulk of the work is the merge of upstream [PR #12](https://github.com/wrath16/PvpStats/pull/12) plus a manifest fix.

## Install (third-party repo)

In FFXIV with Dalamud loaded:

1. `/xlsettings` → **Experimental** tab
2. Under **Custom Plugin Repositories**, add this URL:
   ```
   https://raw.githubusercontent.com/Originaljsx/PvpStats-api15/master/pluginmaster.json
   ```
3. Click the `+` to confirm, then **Save and Close**
4. Open `/xlplugins` → search for **PvP Tracker (API 15)** → install

The plugin updates from the same URL automatically; pulling new releases is just a matter of letting Dalamud refresh.

If/when upstream merges PR #12, switch to the official Dalamud repo and uninstall this fork.

---

<img src="https://raw.githubusercontent.com/Originaljsx/PvpStats-api15/master/images/icon.png" width="256" height="256">

Final Fantasy XIV Dalamud plugin for recording PvP match history.

## Examples
![image](https://raw.githubusercontent.com/wrath16/PvpStats/master/images/example1.PNG)
![image](https://raw.githubusercontent.com/wrath16/PvpStats/master/images/example2.PNG)
![image](https://raw.githubusercontent.com/wrath16/PvpStats/master/images/example3.PNG)

## Usage Instructions
* Install from main Dalamud repo.
* Matches are recorded automatically.
* Enter `/ccstats` to open the Crystalline Conflict stats window.
* Enter `/flstats` to open the Frontline stats window.
* Enter `/rwstats` to open the Rival Wings stats window.
* Enter `/lastmatch` to open the most recent match details window of any game mode.
* Enter `/pvpstatsconfig` or press the gear on the plugin description to access various settings.

## Known Issues
* Spectated Crystalline Conflict matches are not recorded.
* Rematches in Crystalline Conflict custom matches are not recorded.
* Crystalline Conflict matches that you reload into just as they are ending are not recorded.
* Rival Wings matches that end between 14:51 and 14:59 have skewed match timeline timestamps by a few seconds.
* Rival Wings matches recorded before v2.3.0.0 may have incorrect merc counts.
* Rival Wings matches recorded prior to game version 7.0 may have incorrect ceruleum counts for players with >255.
* Rival Wings matches recorded during game version 7.2 and prior to v2.3.4.1 have incorrect alliance Soaring stacks.
* Text may be clipped in some cases if using non-standard font settings.

## Feature Roadmap
May or may not eventually get implemented:
* More stats.
* More localization.
* UI improvements.
* Performance and reliability improvements.
