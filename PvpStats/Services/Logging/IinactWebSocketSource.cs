using System;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace PvpStats.Services.Logging;

/// <summary>
/// IINACT log-line event source via the OverlayPlugin-compatible WebSocket
/// (the same protocol cactbot consumes). Connects to <c>ws://127.0.0.1:10501/ws</c>
/// by default, subscribes to <c>LogLine</c> events, and re-emits each event as
/// an <see cref="ActLogLine"/> on <see cref="LineReceived"/>.
///
/// Replaces the file-tailing approach (<see cref="IinactLogWatcher"/>) which
/// failed to work reliably across OneDrive Documents redirection, file
/// encoding edge cases, and partial-line read boundaries. The WebSocket is
/// IINACT's documented integration path and what cactbot uses successfully.
///
/// Auto-reconnects on connection drop with a 5s backoff.
/// </summary>
internal sealed class IinactWebSocketSource : IDisposable, ILogEventSource {
    private readonly Plugin _plugin;
    private readonly Uri _wsUri;
    private readonly CancellationTokenSource _cts = new();
    private ClientWebSocket? _ws;
    private Task? _runTask;
    private long _linesRead;

    public event Action<ActLogLine>? LineReceived;

    public bool IsConnected { get; private set; }
    public long LinesRead => Interlocked.Read(ref _linesRead);

    public IinactWebSocketSource(Plugin plugin, string wsUrl) {
        _plugin = plugin;
        _wsUri = new Uri(wsUrl);
    }

    public void Start() {
        if (_runTask != null) return;
        _runTask = Task.Run(RunAsync);
    }

    public void Dispose() {
        try { _cts.Cancel(); } catch { }
        try { _ws?.Abort(); } catch { }
        try { _ws?.Dispose(); } catch { }
    }

    private async Task RunAsync() {
        var attempt = 0;
        while (!_cts.IsCancellationRequested) {
            attempt++;
            try {
                _ws = new ClientWebSocket();
                _plugin.Log.Information($"[IinactWS] Connecting to {_wsUri} (attempt {attempt})...");
                await _ws.ConnectAsync(_wsUri, _cts.Token).ConfigureAwait(false);
                IsConnected = true;
                attempt = 0;
                _plugin.Log.Information($"[IinactWS] Connected.");

                // Subscribe to LogLine events using the OverlayPlugin protocol that
                // IINACT speaks. cactbot uses the same call shape.
                var subscribe = """{"call":"subscribe","events":["LogLine"]}""";
                await SendAsync(subscribe).ConfigureAwait(false);
                _plugin.Log.Information("[IinactWS] Subscribed to LogLine events.");

                await ReceiveLoopAsync().ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            } catch (Exception ex) {
                _plugin.Log.Warning(ex, $"[IinactWS] Connection error (attempt {attempt}); retrying in 5s.");
            } finally {
                IsConnected = false;
                try { _ws?.Dispose(); } catch { }
                _ws = null;
            }

            try {
                await Task.Delay(TimeSpan.FromSeconds(5), _cts.Token).ConfigureAwait(false);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    private async Task SendAsync(string text) {
        if (_ws == null) return;
        var bytes = Encoding.UTF8.GetBytes(text);
        await _ws.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, _cts.Token)
            .ConfigureAwait(false);
    }

    private async Task ReceiveLoopAsync() {
        if (_ws == null) return;
        var buffer = new byte[64 * 1024];
        var sb = new StringBuilder();

        while (_ws.State == WebSocketState.Open && !_cts.IsCancellationRequested) {
            sb.Clear();
            WebSocketReceiveResult result;
            do {
                result = await _ws.ReceiveAsync(new ArraySegment<byte>(buffer), _cts.Token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close) {
                    try {
                        await _ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "server closed", _cts.Token).ConfigureAwait(false);
                    } catch { }
                    return;
                }
                if (result.Count > 0) {
                    sb.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));
                }
            } while (!result.EndOfMessage && !_cts.IsCancellationRequested);

            if (sb.Length == 0) continue;
            HandleMessage(sb.ToString());
        }
    }

    private void HandleMessage(string json) {
        try {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeProp)) return;
            var msgType = typeProp.ValueKind == JsonValueKind.String ? typeProp.GetString() : null;
            if (msgType != "LogLine") return;

            // IINACT/OverlayPlugin sends both "rawLine" (full pipe-delimited string)
            // and "line" (pre-split array). We use rawLine since our existing
            // ActLogLine parser already handles the pipe-delimited format.
            string? raw = null;
            if (root.TryGetProperty("rawLine", out var rawProp) && rawProp.ValueKind == JsonValueKind.String) {
                raw = rawProp.GetString();
            } else if (root.TryGetProperty("line", out var lineProp) && lineProp.ValueKind == JsonValueKind.Array) {
                // Fallback: reconstruct from the array form.
                var parts = new System.Collections.Generic.List<string>(lineProp.GetArrayLength());
                foreach (var el in lineProp.EnumerateArray()) {
                    parts.Add(el.ValueKind == JsonValueKind.String ? (el.GetString() ?? "") : el.ToString());
                }
                raw = string.Join("|", parts);
            }

            if (string.IsNullOrEmpty(raw)) return;

            ActLogLine line;
            try { line = new ActLogLine(raw!); }
            catch (Exception ex) {
                _plugin.Log.Warning($"[IinactWS] ActLogLine parse failure ({ex.GetType().Name}: {ex.Message}) on line: '{(raw!.Length > 120 ? raw[..120] + "..." : raw)}'");
                return;
            }

            var n = Interlocked.Increment(ref _linesRead);
            if (n == 1) {
                _plugin.Log.Information($"[IinactWS] First line received (type={line.Type}). Pipeline is live.");
            } else if (n % 5000 == 0) {
                _plugin.Log.Information($"[IinactWS] {n} lines received.");
            }

            try {
                LineReceived?.Invoke(line);
            } catch (Exception ex) {
                _plugin.Log.Warning(ex, "[IinactWS] Subscriber threw on line.");
            }
        } catch (JsonException) {
            // Non-JSON or non-event payload — ignore.
        } catch (Exception ex) {
            _plugin.Log.Warning(ex, "[IinactWS] HandleMessage error.");
        }
    }
}
