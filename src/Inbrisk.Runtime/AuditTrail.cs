using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Inbrisk.Core;

namespace Inbrisk.Runtime;

/// <summary>
/// Dedicated tamper-evident audit stream (F32) — one hash-chained JSONL
/// record per executed action, kept apart from telemetry.jsonl so capture/
/// diff/vision noise can never bury the security trail. Fed by
/// JsonlTelemetrySink.Emit, the funnel every Executor-run action passes
/// through.
///
/// Location: %ProgramData%\Inbrisk\audit\audit.jsonl via AuditLog.Path —
/// ACL'd to SYSTEM + Administrators + owning user when writable, falling
/// back to the data dir; a byte-identical mirror sits beside telemetry so
/// mirror-vs-primary divergence is itself a tamper signal. Telemetry is
/// unaffected if any of this fails — every call is best-effort.
///
/// Chain format — same "prev"+"h" SHA-256 link as AuditLog.ChainLine, so
/// AuditLog.VerifyChain validates a segment end-to-end. When a per-install
/// secret is available each record additionally carries
/// "sig" = HMAC-SHA256(key, record incl. prev, excl. h+sig): the sha256
/// chain detects surgical edits and mid-file deletions, the sig detects a
/// full rewrite-and-rechain by an attacker who never read the key. An
/// agent with full same-user access can read the key too — the ProgramData
/// ACL is what keeps it out of less-privileged hands; sigs are additive
/// hardening, not the primary defense.
///
/// Rotation: past 5 MB the segment becomes audit.jsonl.old and the new
/// segment opens with a {"category":"audit_segment","continues":<old tail
/// hash>} header — the tail hash carried forward, so history linkage
/// survives segment boundaries while each segment stays self-verifiable.
/// Verify per segment via AuditLog.VerifyChain, then check each segment
/// header's "continues" against the previous segment's tail hash.
/// A process restart continues the open segment (tail re-read once at
/// init); concurrent writers sharing the file fork the chain — itself a
/// detectable anomaly.
/// </summary>
public static class AuditTrail
{
    private const long MaxFileBytes = 5 * 1024 * 1024; // 5 MB
    private const string KeyEnvVar = "INBRISK_AUDIT_KEY";
    private static readonly object Gate = new();

    private static string? _path;
    private static string? _mirror;
    private static string _prevHash = AuditLog.GenesisHash;
    private static long _bytes;
    private static bool _keyLoaded;
    private static byte[]? _key;

    /// <summary>Append one tamper-evident record for an executed action —
    /// {at, seq, run, step, act, kind, target, el, backend, method, ok,
    /// code, err, verify, ms} + chain fields. run/step/act carry the
    /// actor attribution (RunId/StepId/ActionId) and join back to
    /// telemetry.jsonl via seq/act. Never throws — the audit trail must
    /// not break the action it records.</summary>
    public static void Action(ActionTelemetry t)
    {
        try
        {
            var record = JsonSerializer.SerializeToNode(new
            {
                at = t.At,
                category = "action",
                seq = t.Sequence,
                run = t.RunId,
                step = t.StepId,
                act = t.ActionId,
                kind = t.Intent.ToString(),
                target = t.TargetDescription,
                el = t.ElementId,
                backend = t.BackendUsed?.ToString(),
                method = t.Method,
                ok = t.Success,
                code = t.Error == ErrorCode.None ? null : t.Error.ToString(),
                err = t.ErrorMessage,
                verify = t.Verification == VerifyResult.NotRequested
                    ? null : t.Verification.ToString(),
                ms = Math.Round(t.Duration.TotalMilliseconds, 2),
            })!.AsObject();
            lock (Gate)
            {
                EnsureInitLocked();
                if (_bytes > MaxFileBytes) RotateLocked();
                WriteLocked(record);
            }
        }
        catch { /* audit is additive evidence — never fatal */ }
    }

    /// <summary>Resolve paths and re-anchor on the open segment's tail
    /// (process restart continues the segment; a fresh/empty file opens
    /// with a genesis segment header).</summary>
    private static void EnsureInitLocked()
    {
        if (_path != null) return;
        var path = AuditLog.Path("audit.jsonl");
        var mirror = AuditLog.MirrorPath("audit.jsonl");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        _path = path;               // assigned last: a throw above retries
        _mirror = mirror;           // init on the next call
        _bytes = File.Exists(_path) ? new FileInfo(_path).Length : 0;
        _prevHash = _bytes > 0
            ? AuditLog.LastChainHash(_path)
            : AuditLog.GenesisHash;
        if (_bytes == 0) SegmentHeaderLocked(null);
    }

    /// <summary>Close the segment: move primary and mirror to .old, then
    /// open a new segment whose header carries the rotated tail hash.</summary>
    private static void RotateLocked()
    {
        var oldPath = _path! + ".old";
        var tail = _prevHash;
        try { if (File.Exists(oldPath)) File.Delete(oldPath); } catch { }
        try { File.Move(_path!, oldPath); }
        catch { return; }   // move failed — keep appending past the limit
        if (_mirror != null)
        {
            try
            {
                var oldMirror = _mirror + ".old";
                if (File.Exists(oldMirror)) File.Delete(oldMirror);
                if (File.Exists(_mirror)) File.Move(_mirror, oldMirror);
            }
            catch { /* best-effort mirror */ }
        }
        SegmentHeaderLocked(tail);
    }

    /// <summary>First record of every segment — "continues" binds the new
    /// file to the rotated-out tail hash (null for the first segment), so
    /// truncating history at a segment boundary is detectable.</summary>
    private static void SegmentHeaderLocked(string? continues)
    {
        _bytes = 0;
        _prevHash = AuditLog.GenesisHash;
        WriteLocked(new JsonObject
        {
            ["at"] = DateTimeOffset.UtcNow,
            ["category"] = "audit_segment",
            ["file"] = Path.GetFileName(_path),
            ["continues"] = continues,
        });
    }

    /// <summary>Chain (prev → sig → h) and append one line. "prev" comes
    /// from in-memory state, so an external append since our last write
    /// surfaces as a chain break — the tamper signal we want.</summary>
    private static void WriteLocked(JsonObject record)
    {
        record["prev"] = _prevHash;
        if (KeyLocked() is { } key)
            record["sig"] = Convert.ToHexString(HMACSHA256.HashData(key,
                Encoding.UTF8.GetBytes(record.ToJsonString())))
                .ToLowerInvariant();
        var h = Convert.ToHexString(SHA256.HashData(
            Encoding.UTF8.GetBytes(record.ToJsonString()))).ToLowerInvariant();
        record["h"] = h;
        _prevHash = h;
        var line = record.ToJsonString() + "\n";
        _bytes += line.Length;
        File.AppendAllText(_path!, line);
        try { if (_mirror != null) File.AppendAllText(_mirror, line); }
        catch { /* best-effort mirror */ }
    }

    /// <summary>Per-install HMAC secret: INBRISK_AUDIT_KEY (hex) wins, else
    /// a generated .auditkey file beside the log — it inherits the audit
    /// dir ACL (SYSTEM/Administrators/owner when ProgramData resolved).
    /// Unavailable ⇒ records go unsigned; the sha256 chain still detects
    /// surgical edits.</summary>
    private static byte[]? KeyLocked()
    {
        if (_keyLoaded) return _key;
        _keyLoaded = true;
        try
        {
            var env = Environment.GetEnvironmentVariable(KeyEnvVar);
            if (!string.IsNullOrWhiteSpace(env))
                return _key = Convert.FromHexString(env.Trim());
            var keyPath = Path.Combine(Path.GetDirectoryName(_path!)!, ".auditkey");
            if (File.Exists(keyPath) && new FileInfo(keyPath).Length >= 16)
                return _key = File.ReadAllBytes(keyPath);
            _key = RandomNumberGenerator.GetBytes(32);
            File.WriteAllBytes(keyPath, _key);
            return _key;
        }
        catch { return _key = null; }
    }
}
