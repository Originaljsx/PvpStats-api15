using System;

namespace PvpStats.Services.Logging;

/// <summary>
/// Abstraction over "something that produces parsed IINACT log lines" so the
/// downstream <see cref="CcEventIngestor"/> doesn't care whether they came from
/// a tailed file or a WebSocket subscription.
/// </summary>
internal interface ILogEventSource {
    event Action<ActLogLine>? LineReceived;
}
