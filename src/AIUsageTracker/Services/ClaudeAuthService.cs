using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using AIUsageTracker.Models;

namespace AIUsageTracker.Services;

public sealed class ClaudeAuthService : IDisposable
{
    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromSeconds(15)
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> RefreshLocks = new(StringComparer.OrdinalIgnoreCase);

    private readonly SettingsService _settingsService;
    private FileSystemWatcher? _watcher;
    private string? _watchedCredentialsPath;
    private DateTimeOffset _lastAutoLoginAttemptUtc = DateTimeOffset.MinValue;
    private readonly object _loginLock = new();
    private readonly EventHandler<AppSettings> _settingsChangedHandler;
    private readonly SemaphoreSlim _autoLoginGate = new(1, 1);
    private bool _disposed;
    private volatile bool _authenticationRejected;

    public event EventHandler? CredentialsChanged;
    public event EventHandler<bool>? TokenExpiredStateChanged;

    public bool IsExpired { get; private set; }

    public ClaudeAuthService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        _settingsChangedHandler = (_, _) => SetupWatcher();
        SetupWatcher();
        _settingsService.SettingsChanged += _settingsChangedHandler;
        IsExpired = CheckTokenExpired();
    }

    private void SetupWatcher()
    {
        if (_disposed) return;
        string credPath = _settingsService.Current.GetEffectiveClaudeCredentialsPath();
        if (_watcher != null && string.Equals(_watchedCredentialsPath, credPath, StringComparison.OrdinalIgnoreCase))
            return;
        if (!string.Equals(_watchedCredentialsPath, credPath, StringComparison.OrdinalIgnoreCase))
            _authenticationRejected = false;
        _watchedCredentialsPath = credPath;
        _watcher?.Dispose();
        _watcher = null;

        try
        {
            string? dir = Path.GetDirectoryName(credPath);
            string fileName = Path.GetFileName(credPath);

            if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
            {
                _watcher = new FileSystemWatcher(dir, fileName)
                {
                    NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                    EnableRaisingEvents = true
                };

                _watcher.Changed += OnFileChanged;
                _watcher.Created += OnFileChanged;
                _watcher.Renamed += OnFileChanged;
                _watcher.Deleted += OnFileChanged;
                _watcher.Error += OnWatcherError;
            }
        }
        catch
        {
            // Ignore watcher failure
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _watcher)) return;
        _authenticationRejected = false;
        UpdateTokenExpiredStatus();
        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        if (_disposed || !ReferenceEquals(sender, _watcher)) return;
        _watcher?.Dispose();
        _watcher = null;
        SetupWatcher();
    }

    // A revoked OAuth token can return HTTP 401 long before its expiresAt timestamp.
    // The polling service publishes the new state after provider requests finish.
    public void MarkTokenRejected() => _authenticationRejected = true;

    public bool CheckTokenExpired()
    {
        return _authenticationRejected || IsTokenExpiredStatic(_settingsService.Current);
    }

    public bool UpdateTokenExpiredStatus()
    {
        if (_watcher == null) SetupWatcher();
        bool expired = CheckTokenExpired();
        if (expired != IsExpired)
        {
            IsExpired = expired;
            TokenExpiredStateChanged?.Invoke(this, expired);
        }
        return expired;
    }

    public static bool IsTokenExpiredStatic(AppSettings settings)
    {
        string credPath = settings.GetEffectiveClaudeCredentialsPath();
        if (!File.Exists(credPath))
        {
            return true;
        }

        try
        {
            string json = File.ReadAllText(credPath);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauthEl))
            {
                if (oauthEl.TryGetProperty("accessToken", out JsonElement tokEl))
                {
                    string? tok = tokEl.GetString();
                    if (string.IsNullOrWhiteSpace(tok))
                    {
                        return true;
                    }
                }
                else
                {
                    return true;
                }

                if (oauthEl.TryGetProperty("expiresAt", out JsonElement expEl) &&
                    expEl.TryGetInt64(out long expiresAt))
                {
                    if (expiresAt <= 0) return true;
                    long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                    return now > expiresAt;
                }
            }
        }
        catch
        {
            return true;
        }

        return true;
    }

    public static bool IsTokenExpiringSoonStatic(AppSettings settings, TimeSpan buffer)
    {
        string credPath = settings.GetEffectiveClaudeCredentialsPath();
        if (!File.Exists(credPath)) return true;

        try
        {
            string json = File.ReadAllText(credPath);
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauthEl))
            {
                if (!oauthEl.TryGetProperty("accessToken", out JsonElement tokenEl) ||
                    tokenEl.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(tokenEl.GetString()))
                    return true;

                if (oauthEl.TryGetProperty("expiresAt", out JsonElement expEl) &&
                    expEl.TryGetInt64(out long expiresAt))
                {
                    if (expiresAt <= 0) return true;
                    long threshold = DateTimeOffset.UtcNow.Add(buffer).ToUnixTimeMilliseconds();
                    return threshold >= expiresAt;
                }
            }
        }
        catch
        {
            return true;
        }

        return true;
    }

    public static async Task<bool> TrySilentTokenRefreshAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        string credPath = settings.GetEffectiveClaudeCredentialsPath();
        if (!File.Exists(credPath)) return false;

        var refreshLock = RefreshLocks.GetOrAdd(Path.GetFullPath(credPath), _ => new SemaphoreSlim(1, 1));
        string? initialJson;
        try { initialJson = await File.ReadAllTextAsync(credPath, cancellationToken); }
        catch (IOException) { return false; }
        await refreshLock.WaitAsync(cancellationToken);

        try
        {
            string json = await File.ReadAllTextAsync(credPath, cancellationToken);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("claudeAiOauth", out JsonElement oauthEl))
            {
                return false;
            }

            if (!oauthEl.TryGetProperty("refreshToken", out JsonElement rtEl) ||
                string.IsNullOrWhiteSpace(rtEl.GetString()))
            {
                return false;
            }

            string refreshToken = rtEl.GetString()!;
            // Another caller may have refreshed this token while we waited for the lock.
            if (initialJson != json && oauthEl.TryGetProperty("accessToken", out JsonElement existingToken) &&
                existingToken.ValueKind == JsonValueKind.String &&
                !string.IsNullOrWhiteSpace(existingToken.GetString()) &&
                oauthEl.TryGetProperty("expiresAt", out JsonElement existingExpiry) &&
                existingExpiry.TryGetInt64(out long existingExpiresAt) &&
                existingExpiresAt > DateTimeOffset.UtcNow.AddMinutes(5).ToUnixTimeMilliseconds())
                return true;

            var scopes = new List<string>();
            if (oauthEl.TryGetProperty("scopes", out JsonElement scEl) && scEl.ValueKind == JsonValueKind.Array)
            {
                foreach (JsonElement s in scEl.EnumerateArray())
                {
                    string? str = s.GetString();
                    if (!string.IsNullOrEmpty(str)) scopes.Add(str);
                }
            }

            string scopeStr = scopes.Count > 0
                ? string.Join(" ", scopes)
                : "user:file_upload user:inference user:mcp_servers user:plugins user:profile user:sessions:claude_code";

            var payload = new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = "9d1c250a-e61b-44d9-88ed-5944d1962f5e",
                ["scope"] = scopeStr
            };

            string requestJson = JsonSerializer.Serialize(payload);
            using var req = new HttpRequestMessage(HttpMethod.Post, "https://platform.claude.com/v1/oauth/token");
            req.Content = new StringContent(requestJson, System.Text.Encoding.UTF8, "application/json");
            req.Headers.UserAgent.ParseAdd("claude-code/2.0.0");

            using var resp = await Http.SendAsync(req, cancellationToken);
            if (!resp.IsSuccessStatusCode)
            {
                return false;
            }

            string respContent = await resp.Content.ReadAsStringAsync(cancellationToken);
            using var respDoc = JsonDocument.Parse(respContent);
            JsonElement respRoot = respDoc.RootElement;

            if (respRoot.TryGetProperty("access_token", out JsonElement atEl) &&
                !string.IsNullOrWhiteSpace(atEl.GetString()))
            {
                string newAccessToken = atEl.GetString()!;
                string newRefreshToken = respRoot.TryGetProperty("refresh_token", out JsonElement nrtEl) && !string.IsNullOrWhiteSpace(nrtEl.GetString())
                    ? nrtEl.GetString()!
                    : refreshToken;

                long expiresInSec = respRoot.TryGetProperty("expires_in", out JsonElement expInEl) && expInEl.TryGetInt64(out long expSec)
                    ? expSec
                    : 28800;

                long rtExpiresInSec = respRoot.TryGetProperty("refresh_token_expires_in", out JsonElement rtexpEl) && rtexpEl.TryGetInt64(out long rtexp)
                    ? rtexp
                    : 2383930;

                long nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                long newExpiresAt = nowMs + (expiresInSec * 1000);
                long newRtExpiresAt = nowMs + (rtExpiresInSec * 1000);

                // Never overwrite a newer login or a refresh performed by another process.
                string latestJson = await File.ReadAllTextAsync(credPath, cancellationToken);
                if (latestJson != json)
                    return IsTokenExpiredStatic(settings) == false;

                var updatedDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(latestJson) ?? new();
                var oauthDict = updatedDict.TryGetValue("claudeAiOauth", out JsonElement curOauth)
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(curOauth.GetRawText()) ?? new()
                    : new();

                oauthDict["accessToken"] = newAccessToken;
                oauthDict["refreshToken"] = newRefreshToken;
                oauthDict["expiresAt"] = newExpiresAt;
                oauthDict["refreshTokenExpiresAt"] = newRtExpiresAt;
                updatedDict["claudeAiOauth"] = JsonSerializer.SerializeToElement(oauthDict);

                string newJson = JsonSerializer.Serialize(updatedDict, new JsonSerializerOptions { WriteIndented = true });
                string tempPath = Path.Combine(Path.GetDirectoryName(credPath)!, $".{Path.GetFileName(credPath)}.{Guid.NewGuid():N}.tmp");
                try
                {
                    await File.WriteAllTextAsync(tempPath, newJson, cancellationToken);
                    // Other processes may replace the credentials file during the network call.
                    if (await File.ReadAllTextAsync(credPath, cancellationToken) != latestJson)
                        return !IsTokenExpiredStatic(settings);
                    File.Move(tempPath, credPath, overwrite: true);
                }
                finally
                {
                    if (File.Exists(tempPath)) File.Delete(tempPath);
                }
                return true;
            }
        }
        catch
        {
            return false;
        }
        finally
        {
            refreshLock.Release();
        }

        return false;
    }

    public bool TryAutoLogin(bool force = false)
    {
        _ = RunAutoLoginSafelyAsync(force);
        return true;
    }

    private async Task RunAutoLoginSafelyAsync(bool force)
    {
        try { await TryAutoLoginAsync(force); }
        catch (Exception ex) { Debug.WriteLine($"Claude login failed: {ex}"); }
    }

    public async Task<bool> TryAutoLoginAsync(bool force = false)
    {
        if (!await _autoLoginGate.WaitAsync(0)) return false;
        try
        {
        if (!force)
        {
            if (!CheckTokenExpired())
            {
                return false;
            }

            // 1. First attempt silent token refresh in background
            bool refreshed = await TrySilentTokenRefreshAsync(_settingsService.Current);
            if (refreshed)
            {
                _authenticationRejected = false;
                UpdateTokenExpiredStatus();
                CredentialsChanged?.Invoke(this, EventArgs.Empty);
                return true;
            }

            lock (_loginLock)
            {
                if (DateTimeOffset.UtcNow - _lastAutoLoginAttemptUtc < TimeSpan.FromMinutes(3))
                {
                    return false;
                }
            }
        }

        lock (_loginLock)
        {
            _lastAutoLoginAttemptUtc = DateTimeOffset.UtcNow;
            return LaunchInteractiveLogin(_settingsService.Current);
        }
        }
        finally { _autoLoginGate.Release(); }
    }

    public static bool LaunchInteractiveLogin(AppSettings? settings = null)
    {
        try
        {
            string? configDir = null;
            if (settings != null)
            {
                string effectivePath = settings.GetEffectiveClaudeCredentialsPath();
                if (!Path.GetFileName(effectivePath).Equals(".credentials.json", StringComparison.OrdinalIgnoreCase))
                {
                    Debug.WriteLine("Claude CLI login requires a .credentials.json filename.");
                    return false;
                }
                configDir = Path.GetDirectoryName(Path.GetFullPath(effectivePath));
            }
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start \"Claude Code Login\" cmd.exe /k \"echo ======================================================== && echo       AI Usage Tracker - Reconexao Claude Code && echo ======================================================== && echo. && echo Iniciando autenticacao... && claude auth login && echo. && echo [OK] Login concluido com sucesso! && timeout /t 3 && exit\"",
                UseShellExecute = configDir == null
            };

            if (configDir != null)
                psi.Environment["CLAUDE_CONFIG_DIR"] = configDir;
            return Process.Start(psi) != null;
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start claude auth login: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        _disposed = true;
        _settingsService.SettingsChanged -= _settingsChangedHandler;
        _watcher?.Dispose();
    }
}
