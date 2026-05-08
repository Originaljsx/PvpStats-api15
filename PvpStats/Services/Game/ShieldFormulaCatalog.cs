using System.Collections.Generic;

namespace PvpStats.Services.Game;

/// <summary>
/// How a shield-applying ability's HP value is derived from its log line.
/// </summary>
internal enum ShieldFormulaKind {
    /// <summary>
    /// Shield HP equals the heal effect in the same NetworkAbility line, times
    /// <see cref="ShieldFormula.Factor"/>. Used by Adloquium-family abilities
    /// where the cast both heals and applies a shield based on the heal.
    /// </summary>
    HealEffectMultiple,
    /// <summary>
    /// Shield HP equals the target's max HP × <see cref="ShieldFormula.Factor"/>.
    /// Used by abilities like Eukrasian Diagnosis where the shield is a flat
    /// percentage of max HP.
    /// </summary>
    MaxHpFraction,
    /// <summary>
    /// Shield HP is a fixed value (in <see cref="ShieldFormula.FlatValue"/>).
    /// Used by abilities like Sheltron that grant a fixed-block-amount shield.
    /// </summary>
    FlatValue,
}

internal sealed class ShieldFormula {
    public required uint AbilityId;
    public required string Name;
    public required ShieldFormulaKind Kind;
    /// <summary>Multiplier applied to the heal or max-HP basis. Ignored for FlatValue.</summary>
    public double Factor = 1.0;
    /// <summary>Fixed HP value. Used only for FlatValue.</summary>
    public int FlatValue;
    /// <summary>
    /// The status that gets applied alongside this ability. Used to attribute
    /// later absorbed hits back to this shield.
    /// </summary>
    public uint? AppliesStatusId;
}

/// <summary>
/// Maps shield-applying abilities to formulas that estimate the shield's HP at
/// cast time. Conservative: when in doubt, use Factor=1.0 over a healing or
/// max-HP basis. CC PvP magnitudes can differ from PvE — adjust Factor as you
/// observe log-vs-scoreboard discrepancies.
///
/// This is the v2.6.9.0 catalog focused specifically on the "shielding done"
/// metric. Layer 4 of phase B.
/// </summary>
internal static class ShieldFormulaCatalog {
    public static readonly List<ShieldFormula> Formulas = new() {
        // Scholar — Adloquium applies Galvanize equal to its heal amount.
        // In PvP CC, Adloquium has reduced healing potency but still applies
        // a shield equal to the heal portion. Factor=1.0 is the conservative
        // default; tune if scoreboard suggests otherwise.
        new() { AbilityId = 0x7230, Name = "Adloquium",          Kind = ShieldFormulaKind.HealEffectMultiple, Factor = 1.0, AppliesStatusId = 0x0C0F /* Galvanize */ },
        // Sage — Eukrasian Diagnosis: shield ≈ 30% of target max HP in PvE.
        // PvP-specific magnitudes vary; tune as needed.
        new() { AbilityId = 0x726B, Name = "Eukrasian Diagnosis", Kind = ShieldFormulaKind.MaxHpFraction, Factor = 0.30, AppliesStatusId = 0x0C22 /* Eukrasian Diagnosis status */ },
        new() { AbilityId = 0x726C, Name = "Eukrasian Prognosis", Kind = ShieldFormulaKind.MaxHpFraction, Factor = 0.30, AppliesStatusId = 0x0C23 /* Eukrasian Prognosis status */ },
        // Sage — Haima/Panhaima are stack-based shields (5 stacks of ~20% max HP each).
        new() { AbilityId = 0x7507, Name = "Haima",               Kind = ShieldFormulaKind.MaxHpFraction, Factor = 1.00, AppliesStatusId = 0x07A2 /* total of 5 stacks */ },
        new() { AbilityId = 0x7510, Name = "Panhaima",            Kind = ShieldFormulaKind.MaxHpFraction, Factor = 1.00, AppliesStatusId = 0x07A3 },
        // White Mage — Divine Benison: shield ≈ 50% of target max HP.
        new() { AbilityId = 0x1D80, Name = "Divine Benison",      Kind = ShieldFormulaKind.MaxHpFraction, Factor = 0.50, AppliesStatusId = 0x0746 },
        // White Mage — Aquaveil: smaller, ~20% mit shield-ish.
        new() { AbilityId = 0x21CE, Name = "Aquaveil",            Kind = ShieldFormulaKind.MaxHpFraction, Factor = 0.20, AppliesStatusId = 0x0BA8 },
        // Astrologian — Exaltation: shield component ≈ 10% max HP.
        new() { AbilityId = 0x21D2, Name = "Exaltation",          Kind = ShieldFormulaKind.MaxHpFraction, Factor = 0.10, AppliesStatusId = 0x0BA1 },
        // Paladin — Holy Sheltron / Sheltron: flat-block shield.
        new() { AbilityId = 0x21CD, Name = "Holy Sheltron",       Kind = ShieldFormulaKind.MaxHpFraction, Factor = 0.50, AppliesStatusId = 0x07AA },
        // Gunbreaker — Heart of Stone (single target): magic mit shield.
        new() { AbilityId = 0x4070, Name = "Heart of Stone",      Kind = ShieldFormulaKind.MaxHpFraction, Factor = 0.15, AppliesStatusId = 0x07B4 },
        new() { AbilityId = 0x270C, Name = "Heart of Corundum",   Kind = ShieldFormulaKind.MaxHpFraction, Factor = 0.15, AppliesStatusId = 0x09F7 },
    };

    public static IReadOnlyDictionary<uint, ShieldFormula> ByAbilityId {
        get {
            var dict = new Dictionary<uint, ShieldFormula>(Formulas.Count);
            foreach (var f in Formulas) dict[f.AbilityId] = f;
            return dict;
        }
    }
}
