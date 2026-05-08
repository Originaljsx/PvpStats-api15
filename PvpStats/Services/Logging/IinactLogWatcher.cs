using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace PvpStats.Services.Logging;

/// <summary>
/// Tails IINACT's daily Network_*.log files. Mirrors the polling+watch hybrid
/// from xivrecorder/src/parsing/IINACTLogWatcher.ts so behavior is consistent
/// across both apps.
///
/// Subscribers receive parsed <see cref="ActLogLine"/> instances on
/// <see cref="LineReceived"/>. Subscribers should be quick (callback runs on
/// the watcher thread) — heavy work belongs on a queue.
/// </summary>
internal sealed class IinactLogWatcher : IDisposable {
    private readonly Plugin _plugin;
    private string _logDir;
    private readonly object _stateLock = new();
    private readonly Dictionary<string, long> _state = new(StringComparer.OrdinalIgnoreCase);
    private string? _current;
    private Timer? _pollTimer;
    private FileSystemWatcher? _fsw;
    private long _linesRead;
    private int _isProcessing;

    public event Action<ActLogLine>? LineReceived;
    public bool IsActive => _pollTimer != null;
    public string LogDirectory => _logDir;
    public long LinesRead => Interlocked.Read(ref _linesRead);

    public IinactLogWatcher(Plugin plugin, string logDir) {
        _plugin = plugin;
        _logDir = logDir;
    }

    public void SetLogDirectory(string newDir) {
        if (string.Equals(newDir, _logDir, StringComparison.OrdinalIgnoreCase)) return;
        var wasActive = IsActive;
        Stop();
        _logDir = newDir;
        lock (_stateLock) {
            _state.Clear();
            _current = null;
        }
        if (wasActive) Start();
    }

    public void Start() {
        if (IsActive) return;
        if (!Directory.Exists(_logDir)) {
            _plugin.Log.Warning($"[IinactLogWatcher] Log directory does not exist yet: {_logDir} — watcher will retry every poll.");
        }

        SnapshotInitialFileSizes();
        _current = FindNewestLogFile();
        _plugin.Log.Information($"[IinactLogWatcher] Watching {_logDir} (current: {_current ?? "<none yet>"}, polling 1s).");

        _pollTimer = new Timer(_ => PollSafe(), null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));

        try {
            _fsw = new FileSystemWatcher(_logDir, "Network_*.log") {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                EnableRaisingEvents = true,
            };
            _fsw.Changed += (_, e) => OnFsEvent(e.Name);
            _fsw.Created += (_, e) => OnFsEvent(e.Name);
        } catch (Exception ex) {
            _plugin.Log.Warning(ex, "[IinactLogWatcher] FileSystemWatcher failed; polling-only mode.");
            _fsw = null;
        }
    }

    public void Stop() {
        _pollTimer?.Dispose();
        _pollTimer = null;
        if (_fsw != null) {
            try { _fsw.EnableRaisingEvents = false; _fsw.Dispose(); } catch { }
            _fsw = null;
        }
    }

    public void Dispose() {
        Stop();
    }

    private void OnFsEvent(string? fileName) {
        if (string.IsNullOrEmpty(fileName)) return;
        if (!fileName.StartsWith("Network_", StringComparison.OrdinalIgnoreCase)) return;
        if (!fileName.EndsWith(".log", StringComparison.OrdinalIgnoreCase)) return;

        lock (_stateLock) {
            if (!string.Equals(fileName, _current, StringComparison.OrdinalIgnoreCase)) {
                _plugin.Log.Information($"[IinactLogWatcher] New active log file: {fileName}");
                _current = fileName;
            }
        }
        // Defer actual read to the polling cycle — avoids re-entrancy from rapid notifications.
    }

    private void PollSafe() {
        // Ensure only one poll runs at a time.
        if (Interlocked.CompareExchange(ref _isProcessing, 1, 0) != 0) return;
        try { Poll(); } catch (Exception ex) {
            _plugin.Log.Warning(ex, "[IinactLogWatcher] Poll error.");
        } finally { Interlocked.Exchange(ref _isProcessing, 0); }
    }

    private int _pollsSinceLastLog;

    private void Poll() {
        if (!Directory.Exists(_logDir)) {
            if (++_pollsSinceLastLog % 30 == 0) {
                _plugin.Log.Warning($"[IinactLogWatcher] Log directory still missing: {_logDir}");
            }
            return;
        }

        // Detect rotation (midnight) — the newest Network_*.log file becomes current.
        var newestName = FindNewestLogFile();
        if (newestName != null && !string.Equals(newestName, _current, StringComparison.OrdinalIgnoreCase)) {
            _plugin.Log.Information($"[IinactLogWatcher] Rotated to {newestName}");
            _current = newestName;
        }

        if (string.IsNullOrEmpty(_current)) return;
        var fullPath = Path.Combine(_logDir, _current);

        long currentSize;
        try {
            var fi = new FileInfo(fullPath);
            if (!fi.Exists) return;
            currentSize = fi.Length;
        } catch { return; }

        long startPosition;
        lock (_stateLock) {
            _state.TryGetValue(fullPath, out startPosition);
        }

        var bytesToRead = currentSize - startPosition;
        if (bytesToRead <= 0) {
            // Periodic visibility for the case where the file is open but never grows
            // (could indicate IINACT logging is disabled, or watcher is on the wrong file).
            if (++_pollsSinceLastLog % 60 == 0) {
                _plugin.Log.Information($"[IinactLogWatcher] No new bytes in {Path.GetFileName(fullPath)} for ~60s (size {currentSize}, lines so far {Interlocked.Read(ref _linesRead)}).");
            }
            return;
        }

        ReadAndEmit(fullPath, startPosition, bytesToRead);

        lock (_stateLock) {
            _state[fullPath] = currentSize;
        }
    }

    private void ReadAndEmit(string fullPath, long position, long bytesToRead) {
        // Use FileShare.ReadWrite|Delete so we don't lock the file IINACT is appending to.
        try {
            using var fs = new FileStream(fullPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            fs.Seek(position, SeekOrigin.Begin);

            var capped = (int)Math.Min(bytesToRead, 4 * 1024 * 1024);
            var buffer = new byte[capped];
            int total = 0;
            while (total < capped) {
                var got = fs.Read(buffer, total, capped - total);
                if (got <= 0) break;
                total += got;
            }
            if (total == 0) {
                _plugin.Log.Information($"[IinactLogWatcher] Read 0 bytes at offset {position} from {Path.GetFileName(fullPath)} (size diff said {bytesToRead}).");
                return;
            }

            var text = System.Text.Encoding.UTF8.GetString(buffer, 0, total);
            var trailing = bytesToRead - total;
            if (trailing > 0) {
                lock (_stateLock) {
                    _state[fullPath] = position + total;
                }
            }

            var lines = text.Split('\n');
            int emitted = 0;
            for (var i = 0; i < lines.Length; i++) {
                var s = lines[i].TrimEnd('\r').Trim();
                if (s.Length == 0) continue;
                EmitLine(s);
                emitted++;
            }

            // Diagnostic: surface the first successful read per file, plus periodic
            // progress, so we can tell whether the watcher pipeline is actually running.
            if (Interlocked.Read(ref _linesRead) <= emitted) {
                _plugin.Log.Information($"[IinactLogWatcher] First read from {Path.GetFileName(fullPath)}: {emitted} lines, {total} bytes from offset {position}.");
            } else if (Interlocked.Read(ref _linesRead) % 5000 < emitted) {
                _plugin.Log.Information($"[IinactLogWatcher] Progress: {Interlocked.Read(ref _linesRead)} lines processed total, current file {Path.GetFileName(fullPath)}.");
            }
        } catch (IOException ioex) {
            // No longer silent — surface it so we can see if file-sharing is the issue.
            _plugin.Log.Warning(ioex, $"[IinactLogWatcher] IOException reading {Path.GetFileName(fullPath)} at offset {position}, {bytesToRead} bytes requested.");
        } catch (UnauthorizedAccessException uex) {
            _plugin.Log.Warning(uex, $"[IinactLogWatcher] Access denied reading {Path.GetFileName(fullPath)}.");
        } catch (Exception ex) {
            _plugin.Log.Warning(ex, $"[IinactLogWatcher] Unexpected read error at {fullPath}.");
        }
    }

    private int _parseFailures;
    private int _parseFailuresLogged;

    private void EmitLine(string raw) {
        ActLogLine line;
        try { line = new ActLogLine(raw); }
        catch (Exception ex) {
            Interlocked.Increment(ref _parseFailures);
            if (Interlocked.Increment(ref _parseFailuresLogged) <= 5) {
                var snippet = raw.Length > 120 ? raw[..120] + "..." : raw;
                _plugin.Log.Warning($"[IinactLogWatcher] ActLogLine parse failure ({ex.GetType().Name}: {ex.Message}) on line: '{snippet}'");
            }
            return;
        }

        var n = Interlocked.Increment(ref _linesRead);
        if (n == 1) {
            _plugin.Log.Information($"[IinactLogWatcher] First line emitted (type={line.Type}). Line tail: '{(raw.Length > 80 ? raw[..80] + "..." : raw)}'");
        }
        try {
            LineReceived?.Invoke(line);
        } catch (Exception ex) {
            _plugin.Log.Warning(ex, "[IinactLogWatcher] Subscriber threw on line.");
        }
    }

    private void SnapshotInitialFileSizes() {
        lock (_stateLock) {
            _state.Clear();
            if (!Directory.Exists(_logDir)) return;
            foreach (var path in Directory.EnumerateFiles(_logDir, "Network_*.log").OrderBy(p => p, StringComparer.OrdinalIgnoreCase)) {
                try {
                    var fi = new FileInfo(path);
                    _state[path] = fi.Length;
                } catch { }
            }
        }
    }

    private string? FindNewestLogFile() {
        if (!Directory.Exists(_logDir)) return null;
        try {
            return Directory.EnumerateFiles(_logDir, "Network_*.log")
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .Select(Path.GetFileName)
                .LastOrDefault();
        } catch { return null; }
    }
}
