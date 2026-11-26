using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Inbrisk.Core;
using Inbrisk.Platform.Windows;
using Inbrisk.Runtime;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace Inbrisk.Mcp;

/// <summary>
/// Default implementation of the isolated Ghost desktop session lifecycle manager.
/// Coordinates headless loopback RDP connections, user provisioning, and Windows Terminal Services interop.
/// </summary>
public class GhostSessionManager : IGhostSessionManager, IDisposable
{
    private readonly object _gate = new();
    private GhostSessionConfig _currentConfig = new();
    private int? _currentSessionId;
    private int? _currentProcessId;
    private string? _currentUsername;
    private DateTime? _startedAtUtc;
    private int _disposed;

    /// <summary>Gets the current ghost session configuration.</summary>
    public GhostSessionConfig CurrentConfig
    {
        get { lock (_gate) return _currentConfig; }
        private set { lock (_gate) _currentConfig = value; }
    }

    /// <summary>Gets the current Terminal Services session ID if active.</summary>
    public int? CurrentSessionId
    {
        get { lock (_gate) return _currentSessionId; }
    }

    /// <summary>
    /// Ensures the dedicated ghost user account exists and has proper permissions.
    /// </summary>
    public async Task<GhostUserProvisionResult> EnsureProvisionedAsync(CancellationToken ct = default)
    {
        string username = CurrentConfig.Username;
        return await GhostUserProvisioner.EnsureGhostUserReadyAsync(username).ConfigureAwait(false);
    }

    /// <summary>
    /// Launches or connects to a headless ghost desktop session with the specified configuration.
    /// </summary>
    public async Task<GhostSessionStatus> StartSessionAsync(
        GhostSessionConfig? config = null,
        CancellationToken ct = default)
    {
        return await StartSessionAsync(config, password: null, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Launches or connects to a headless ghost desktop session with explicit password override.
    /// </summary>
    public async Task<GhostSessionStatus> StartSessionAsync(
        GhostSessionConfig? config,
        string? password,
        CancellationToken ct = default)
    {
        var targetConfig = config ?? CurrentConfig;
        CurrentConfig = targetConfig;

        if (targetConfig.AutoProvision)
        {
            var provResult = await EnsureProvisionedAsync(ct).ConfigureAwait(false);
            if (!provResult.Success)
            {
                Inbrisk.Core.GhostTelemetry.RecordSessionFail();
                return new GhostSessionStatus(
                    IsActive: false,
                    SessionId: null,
                    Username: targetConfig.Username,
                    Uptime: TimeSpan.Zero,
                    State: $"ProvisionFailed: {provResult.Error}",
                    ProcessId: null);
            }
        }

        try
        {
            string effectivePassword = string.IsNullOrEmpty(password)
                ? GhostUserProvisioner.DefaultGhostPassword
                : password;

            var rdpSession = await GhostRdpConnector.StartHeadlessRdpSessionAsync(
                targetConfig,
                password: effectivePassword,
                ct: ct).ConfigureAwait(false);

            lock (_gate)
            {
                _currentSessionId = rdpSession.SessionId;
                _currentProcessId = rdpSession.ProcessId;
                _currentUsername = rdpSession.Username;
                _startedAtUtc = DateTime.UtcNow;
            }

            return new GhostSessionStatus(
                IsActive: true,
                SessionId: rdpSession.SessionId,
                Username: rdpSession.Username,
                Uptime: TimeSpan.Zero,
                State: "Active",
                ProcessId: rdpSession.ProcessId);
        }
        catch (Exception ex)
        {
            Inbrisk.Core.GhostTelemetry.RecordSessionFail();
            return new GhostSessionStatus(
                IsActive: false,
                SessionId: null,
                Username: targetConfig.Username,
                Uptime: TimeSpan.Zero,
                State: $"StartFailed: {ex.Message}",
                ProcessId: null);
        }
    }

    /// <summary>
    /// Stops the target ghost session or the current active session if sessionId is omitted.
    /// </summary>
    public async Task<bool> StopSessionAsync(int? sessionId = null, CancellationToken ct = default)
    {
        int targetSessionId;
        lock (_gate)
        {
            targetSessionId = sessionId ?? _currentSessionId ?? 0;
            if (targetSessionId == 0)
            {
                return true;
            }
        }

        bool stopped = await GhostRdpConnector.StopHeadlessRdpSessionAsync(targetSessionId).ConfigureAwait(false);

        lock (_gate)
        {
            if (_currentSessionId == targetSessionId || sessionId == null)
            {
                _currentSessionId = null;
                _currentProcessId = null;
                _currentUsername = null;
                _startedAtUtc = null;
            }
        }

        return stopped;
    }

    /// <summary>
    /// Queries the current status of the ghost session.
    /// </summary>
    public Task<GhostSessionStatus> GetStatusAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (!_currentSessionId.HasValue)
            {
                var existing = GhostWtsInterop.FindSessionByUsername(CurrentConfig.Username);
                if (existing != null && (existing.State == WTS_CONNECTSTATE_CLASS.WTSActive || existing.State == WTS_CONNECTSTATE_CLASS.WTSConnected))
                {
                    return Task.FromResult(new GhostSessionStatus(
                        IsActive: true,
                        SessionId: existing.SessionId,
                        Username: existing.UserName ?? CurrentConfig.Username,
                        Uptime: TimeSpan.Zero,
                        State: existing.State.ToString(),
                        ProcessId: null));
                }

                return Task.FromResult(GhostSessionStatus.Inactive);
            }

            var all = GhostWtsInterop.GetAllSessions();
            var matched = all.FirstOrDefault(s => s.SessionId == _currentSessionId.Value);

            bool isActive = matched != null &&
                (matched.State == WTS_CONNECTSTATE_CLASS.WTSActive || matched.State == WTS_CONNECTSTATE_CLASS.WTSConnected);

            var uptime = (_startedAtUtc.HasValue && isActive)
                ? (DateTime.UtcNow - _startedAtUtc.Value)
                : TimeSpan.Zero;

            string state = matched?.State.ToString() ?? (isActive ? "Active" : "Disconnected");

            return Task.FromResult(new GhostSessionStatus(
                IsActive: isActive,
                SessionId: _currentSessionId,
                Username: _currentUsername,
                Uptime: uptime,
                State: state,
                ProcessId: _currentProcessId));
        }
    }

    /// <summary>
    /// Disposes the session manager and cleanly terminates tracked sessions.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        int targetId = 0;
        lock (_gate)
        {
            if (_currentSessionId.HasValue)
            {
                targetId = _currentSessionId.Value;
                _currentSessionId = null;
            }
        }

        if (targetId > 0)
        {
            try
            {
                GhostRdpConnector.StopHeadlessRdpSessionAsync(targetId).GetAwaiter().GetResult();
            }
            catch { }
        }
    }
}
