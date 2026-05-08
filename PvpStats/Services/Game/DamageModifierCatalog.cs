using System.Collections.Generic;

namespace PvpStats.Services.Game;

/// <summary>
/// Direction the modifier applies on. Outgoing affects damage the bearer DEALS,
/// Incoming affects damage the bearer TAKES.
/// </summary>
internal enum ModifierDirection {
    Outgoing,
    Incoming,
}

/// <summary>
/// One status that multiplies outgoing or incoming damage by Factor while active.
/// Factor &lt; 1.0 reduces damage, Factor &gt; 1.0 amplifies it. Multiplicative
/// stacking with other active modifiers.
/// </summary>
internal sealed class DamageModifier {
    public required uint StatusId;
    public required string Name;
    public required ModifierDirection Direction;
    public required double Factor;
    /// <summary>
    /// Optional restriction. PhysicalOnly modifiers (e.g. Feint) only apply to
    /// physical damage; MagicalOnly (e.g. Addle) only to magic. Layer 3 v1
    /// doesn't yet split damage by physical/magical (the IINACT log doesn't
    /// expose this cleanly), so for now both are applied — over-counts mit
    /// slightly but is still directionally accurate.
    /// </summary>
    public DamageDomain Domain = DamageDomain.Any;
}

internal enum DamageDomain {
    Any,
    PhysicalOnly,
    MagicalOnly,
}

/// <summary>
/// Curated catalog of damage-modifying statuses relevant to Crystalline Conflict.
/// First-pass coverage: the universal raid-and-PvP modifiers (Embolden, Reprisal,
/// Feint, Addle, Heart of Light, Dark Mind, Bloodwhetting) plus a few CC-specific
/// statuses. Easy to extend — `Modifiers.Add(new ... { ... })`.
///
/// Magnitudes use displayed in-game values; CC-specific tuning may differ slightly
/// from PvE values but the directional sign is correct. Tune as you observe
/// log-vs-scoreboard discrepancies.
/// </summary>
internal static class DamageModifierCatalog {
    public static readonly List<DamageModifier> Modifiers = new() {
        // ---- Outgoing amplifiers (party-wide damage-up buffs) ----
        new() { StatusId = 0x04DA, Name = "Embolden",         Direction = ModifierDirection.Outgoing, Factor = 1.05 },
        new() { StatusId = 0x04C5, Name = "Battle Litany",    Direction = ModifierDirection.Outgoing, Factor = 1.10 },
        new() { StatusId = 0x04C0, Name = "Brotherhood",      Direction = ModifierDirection.Outgoing, Factor = 1.05 },
        new() { StatusId = 0x07AD, Name = "Devotion",         Direction = ModifierDirection.Outgoing, Factor = 1.05 },
        new() { StatusId = 0x0E2B, Name = "Searing Light",    Direction = ModifierDirection.Outgoing, Factor = 1.03 },
        new() { StatusId = 0x0DD7, Name = "Technical Finish", Direction = ModifierDirection.Outgoing, Factor = 1.05 },
        new() { StatusId = 0x0DD2, Name = "Standard Finish",  Direction = ModifierDirection.Outgoing, Factor = 1.05 },

        // ---- Incoming damage reducers (mits) ----
        new() { StatusId = 0x07A0, Name = "Reprisal",                 Direction = ModifierDirection.Incoming, Factor = 0.90 },
        new() { StatusId = 0x07A4, Name = "Feint (target)",           Direction = ModifierDirection.Incoming, Factor = 0.90, Domain = DamageDomain.PhysicalOnly },
        new() { StatusId = 0x06AC, Name = "Addle (target)",           Direction = ModifierDirection.Incoming, Factor = 0.90, Domain = DamageDomain.MagicalOnly },
        new() { StatusId = 0x07B5, Name = "Heart of Light",           Direction = ModifierDirection.Incoming, Factor = 0.90, Domain = DamageDomain.MagicalOnly },
        new() { StatusId = 0x0747, Name = "Dark Mind",                Direction = ModifierDirection.Incoming, Factor = 0.80, Domain = DamageDomain.MagicalOnly },
        new() { StatusId = 0x0E70, Name = "Bloodwhetting",            Direction = ModifierDirection.Incoming, Factor = 0.90 },
        new() { StatusId = 0x09BB, Name = "Riddle of Earth",          Direction = ModifierDirection.Incoming, Factor = 0.90 },
        new() { StatusId = 0x07F4, Name = "Nascent Flash",            Direction = ModifierDirection.Incoming, Factor = 0.90 },
        new() { StatusId = 0x0E5F, Name = "Oblation",                 Direction = ModifierDirection.Incoming, Factor = 0.90 },
        new() { StatusId = 0x0DCE, Name = "Shake It Off",             Direction = ModifierDirection.Incoming, Factor = 0.85 },
        new() { StatusId = 0x07BC, Name = "Tactician",                Direction = ModifierDirection.Incoming, Factor = 0.90 },
        new() { StatusId = 0x07BD, Name = "Troubadour",               Direction = ModifierDirection.Incoming, Factor = 0.90 },
        new() { StatusId = 0x07AB, Name = "Sentinel",                 Direction = ModifierDirection.Incoming, Factor = 0.70 },
        new() { StatusId = 0x07A1, Name = "Rampart",                  Direction = ModifierDirection.Incoming, Factor = 0.80 },
        new() { StatusId = 0x07AC, Name = "Vengeance",                Direction = ModifierDirection.Incoming, Factor = 0.70 },

        // ---- Vulnerability up (incoming amp) — common PvP vulns ----
        new() { StatusId = 0x0257, Name = "Vulnerability Up",         Direction = ModifierDirection.Incoming, Factor = 1.10 },
        new() { StatusId = 0x0E37, Name = "Vulnerability Up (PvP)",   Direction = ModifierDirection.Incoming, Factor = 1.10 },
    };

    /// <summary>Comma-separated decimal IDs for SQL IN-clauses.</summary>
    public static string SqlInList() {
        var sb = new System.Text.StringBuilder();
        var first = true;
        foreach (var m in Modifiers) {
            if (!first) sb.Append(',');
            sb.Append(m.StatusId);
            first = false;
        }
        return sb.ToString();
    }

    public static IReadOnlyDictionary<uint, DamageModifier> ById {
        get {
            var dict = new Dictionary<uint, DamageModifier>(Modifiers.Count);
            foreach (var m in Modifiers) dict[m.StatusId] = m;
            return dict;
        }
    }
}
