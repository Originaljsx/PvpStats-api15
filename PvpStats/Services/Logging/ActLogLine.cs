using System;
using System.Globalization;

namespace PvpStats.Services.Logging;

/// <summary>
/// Parser for IINACT network log lines.
///
/// Format: DecimalType|ISO8601Timestamp|field1|field2|...|hash
/// Example: 01|2026-03-06T10:31:51.9220000-08:00|40A|Cloud Nine|595002a1bfbea018
///
/// Ported from xivrecorder/src/parsing/ACTLogLine.ts. Indices into Field()
/// match the TS sibling so the existing parsing knowledge transfers cleanly.
/// </summary>
internal sealed class ActLogLine {
    public string Raw { get; }
    private readonly string[] _fields;

    /// <summary>Decimal event type — e.g. 1 ChangeZone, 21 NetworkAbility, 25 NetworkDeath.</summary>
    public int Type { get; }

    /// <summary>Parsed UTC timestamp (best-effort — falls back to MinValue on parse failure).</summary>
    public DateTime Timestamp { get; }

    public ActLogLine(string raw) {
        Raw = raw ?? string.Empty;
        _fields = Raw.Split('|');

        if (_fields.Length > 0 && int.TryParse(_fields[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var t)) {
            Type = t;
        } else {
            Type = -1;
        }

        if (_fields.Length > 1 && DateTime.TryParse(
                _fields[1],
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                out var ts)) {
            Timestamp = ts;
        } else {
            Timestamp = DateTime.MinValue;
        }
    }

    /// <summary>Get a data field by index. Index 0 is the first field after the timestamp (i.e. _fields[2]).</summary>
    public string Field(int index) {
        var actual = index + 2;
        return (actual >= 0 && actual < _fields.Length) ? _fields[actual] : string.Empty;
    }

    public uint FieldHex(int index) {
        var s = Field(index);
        return uint.TryParse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var v) ? v : 0u;
    }

    public int FieldInt(int index) {
        var s = Field(index);
        return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0;
    }

    public long FieldLong(int index) {
        var s = Field(index);
        return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : 0L;
    }

    public double FieldFloat(int index) {
        var s = Field(index);
        return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0d;
    }

    /// <summary>Total number of data fields (excluding type, timestamp, and hash).</summary>
    public int DataFieldCount => Math.Max(0, _fields.Length - 3);

    public override string ToString() => Raw;
}
