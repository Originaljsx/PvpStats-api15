using System.Collections.Generic;

namespace PvpStats.Services.Game;

/// <summary>
/// Catalog of FFXIV statuses that apply a damage-absorbing shield, with
/// emphasis on those reachable in Crystalline Conflict. Sourced from FFXIV's
/// Status data (Lumina excel sheet `Status`) cross-referenced with the cactbot
/// status notes and observed-in-the-wild PvP buffs.
///
/// Layer 2 (v2.6.7.0) uses this purely as an allow-list to compute
/// shield-specific rollup metrics. Layer 3 will layer per-status formulas on
/// top (initial shield HP, % of heal potency, etc.).
///
/// Status IDs are taken from FFXIV's Status sheet (decimal RowId). Names are
/// for human-readability only — never trust them at runtime since localization
/// can vary; the IDs are canonical.
/// </summary>
internal static class ShieldCatalog {
    /// <summary>
    /// Status IDs that apply a damage-absorbing shield. Curated from the CC
    /// PvP status set + universal shield-applying statuses still reachable in
    /// PvP. Conservative — false negatives (missing a niche shield) are better
    /// than false positives (counting non-shields as shields).
    /// </summary>
    public static readonly HashSet<uint> ShieldStatusIds = new() {
        // Scholar
        0x0C0F,  // Galvanize
        0x0C10,  // Catalyze
        // Sage
        0x0C22,  // Eukrasian Diagnosis (single)
        0x0C23,  // Eukrasian Prognosis (party)
        0x0C24,  // (Eukrasian variant — shielded)
        0x07A2,  // Haima (shield stacks)
        0x07A3,  // Panhaima (party shield stacks)
        // White Mage
        0x0746,  // Divine Benison
        0x0BA8,  // Aquaveil
        // Astrologian
        0x07AC,  // Aspected Helios (shield portion)
        0x0BA1,  // Exaltation
        0x0F23,  // Macrocosmos (delayed heal/shield)
        // Paladin (PvP-relevant shielding)
        0x09BE,  // Sheltron block
        0x07AA,  // Holy Sheltron
        0x0DCF,  // Divine Veil (party absorb)
        // Warrior
        0x0E70,  // Bloodwhetting (regen-on-hit; counted as defensive even if not pure shield)
        // Dark Knight
        0x0747,  // Dark Mind (magic damage reduction; not pure shield but defensive)
        0x0E5F,  // Oblation
        // Gunbreaker
        0x07B5,  // Heart of Light (magic damage reduction)
        0x07B4,  // Heart of Stone (single-target shield)
        0x09F7,  // Heart of Corundum
        0x09F8,  // Clarity of Corundum
        0x09F9,  // Catharsis of Corundum
    };

    public static bool IsShield(uint statusId) => ShieldStatusIds.Contains(statusId);

    /// <summary>
    /// SQL-friendly comma-separated list of shield status IDs (decimal) for use
    /// in IN-clauses. Recomputed once per process; not perf-sensitive.
    /// </summary>
    public static string SqlInList() {
        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var id in ShieldStatusIds) {
            if (!first) sb.Append(',');
            sb.Append(id);
            first = false;
        }
        return sb.ToString();
    }
}
