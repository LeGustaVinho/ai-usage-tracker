using System;
using System.Collections.Generic;
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

    private readonly SettingsService _settingsService;
    private FileSystemWatcher? _watcher;
    private DateTimeOffset _lastAutoLoginAttemptUtc = DateTimeOffset.MinValue;
    private readonly object _loginLock = new();

    public event EventHandler? CredentialsChanged;
    public event EventHandler<bool>? TokenExpiredStateChanged;

    public bool IsExpired { get; private set; }

    public ClaudeAuthService(SettingsService settingsService)
    {
        _settingsService = settingsService;
        SetupWatcher();
        _settingsService.SettingsChanged += (_, _) => SetupWatcher();
        IsExpired = CheckTokenExpired();
    }

    private void SetupWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;

        try
        {
            string credPath = _settingsService.Current.GetEffectiveClaudeCredentialsPath();
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
            }
        }
        catch
        {
            // Ignore watcher failure
        }
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        UpdateTokenExpiredStatus();
        CredentialsChanged?.Invoke(this, EventArgs.Empty);
    }

    public bool CheckTokenExpired()
    {
        return IsTokenExpiredStatic(_settingsService.Current);
    }

    public bool UpdateTokenExpiredStatus()
    {
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

                var updatedDict = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json) ?? new();
                var oauthDict = updatedDict.TryGetValue("claudeAiOauth", out JsonElement curOauth)
                    ? JsonSerializer.Deserialize<Dictionary<string, object?>>(curOauth.GetRawText()) ?? new()
                    : new();

                oauthDict["accessToken"] = newAccessToken;
                oauthDict["refreshToken"] = newRefreshToken;
                oauthDict["expiresAt"] = newExpiresAt;
                oauthDict["refreshTokenExpiresAt"] = newRtExpiresAt;
                updatedDict["claudeAiOauth"] = JsonSerializer.SerializeToElement(oauthDict);

                string newJson = JsonSerializer.Serialize(updatedDict, new JsonSerializerOptions { WriteIndented = true });
                await File.WriteAllTextAsync(credPath, newJson, cancellationToken);
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    public bool TryAutoLogin(bool force = false)
    {
        _ = TryAutoLoginAsync(force);
        return true;
    }

    public async Task<bool> TryAutoLoginAsync(bool force = false)
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
            LaunchInteractiveLogin();
            return true;
        }
    }

    public static void LaunchInteractiveLogin()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c start \"Claude Code Login\" cmd.exe /k \"echo ======================================================== && echo       AI Usage Tracker - Reconexao Claude Code && echo ======================================================== && echo. && echo Iniciando autenticacao... && claude auth login && echo. && echo [OK] Login concluido com sucesso! && timeout /t 3 && exit\"",
                UseShellExecute = true
            };

            Process.Start(psi);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to start claude auth login: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
    }
}
