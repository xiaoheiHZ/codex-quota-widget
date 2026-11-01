using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace CodexQuotaWidget;

/// <summary>Only initializes and reads account/rateLimits/read. No inference, credentials, or public listener.</summary>
public sealed class CodexClient : IAsyncDisposable
{
    readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> pending = new();
    readonly SemaphoreSlim writeGate = new(1, 1);
    Process? process;
    int nextId;
    public string? ExecutablePath { get; private set; }

    public static string? FindExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_QUOTA_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) return Path.GetFullPath(configured);
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OpenAI", "Codex", "bin");
        if (Directory.Exists(root))
        {
            var installed = Directory.EnumerateDirectories(root).Select(d => Path.Combine(d, "codex.exe"))
                .Where(File.Exists).OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault();
            if (installed != null) return installed;
        }
        foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            try { var candidate = Path.Combine(dir.Trim('"'), "codex.exe"); if (File.Exists(candidate)) return candidate; }
            catch (ArgumentException) { }
        }
        return null;
    }

    public async Task StartAsync(CancellationToken token)
    {
        ExecutablePath = FindExecutable() ?? throw new QuotaReadException("未找到 Codex", "请先安装并登录 Codex 桌面版或 CLI，然后点击刷新。");
        var info = new ProcessStartInfo(ExecutablePath)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardInput = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardInputEncoding = new UTF8Encoding(false), StandardOutputEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
        info.ArgumentList.Add("app-server"); info.ArgumentList.Add("--listen"); info.ArgumentList.Add("stdio://");
        // Keep diagnostics from ever containing account identifiers, bearer tokens, or private chats.
        info.Environment["RUST_LOG"] = "off";
        process = Process.Start(info) ?? throw new IOException("Cannot start Codex");
        _ = DrainErrorsAsync(process);
        _ = ReadLoopAsync(process);
        await RequestAsync("initialize", new { clientInfo = new { name = "codex_quota_widget", title = "Codex Quota Widget", version = "1.0.0" } }, token);
        await SendAsync(new { method = "initialized", @params = new { } }, token);
    }

    public async Task<QuotaSnapshot> ReadQuotaAsync(CancellationToken token)
    {
        var result = await RequestAsync("account/rateLimits/read", null, token);
        return QuotaSnapshot.Parse(result);
    }

    async Task<JsonElement> RequestAsync(string method, object? parameters, CancellationToken token)
    {
        var id = Interlocked.Increment(ref nextId);
        var tcs = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = tcs;
        try
        {
            await SendAsync(new { id, method, @params = parameters }, token);
            return await tcs.Task.WaitAsync(token);
        }
        finally { pending.TryRemove(id, out _); }
    }

    async Task SendAsync(object message, CancellationToken token)
    {
        await writeGate.WaitAsync(token);
        try
        {
            if (process is null || process.HasExited) throw new IOException("Codex disconnected");
            await process.StandardInput.WriteLineAsync(JsonSerializer.Serialize(message).AsMemory(), token);
            await process.StandardInput.FlushAsync(token);
        }
        finally { writeGate.Release(); }
    }

    async Task ReadLoopAsync(Process child)
    {
        try
        {
            while (await child.StandardOutput.ReadLineAsync() is { } line)
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var id) || !id.TryGetInt32(out var n) || !pending.TryGetValue(n, out var tcs)) continue;
                    if (root.TryGetProperty("result", out var result)) tcs.TrySetResult(result.Clone());
                    else if (root.TryGetProperty("error", out var error))
                    {
                        // Only classify upstream messages; never echo raw response bodies into UI or files.
                        var message = error.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
                        var authentication = message.Contains("auth", StringComparison.OrdinalIgnoreCase) || message.Contains("401");
                        tcs.TrySetException(authentication ? new QuotaReadException("需要 Codex 登录", "请在 Codex 中登录 ChatGPT 账号，再点击刷新。")
                            : new QuotaReadException("暂时无法同步", "请检查网络与 Codex 登录状态，稍后会自动重试。"));
                    }
                }
                catch (JsonException) { }
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
        finally { foreach (var entry in pending.Values) entry.TrySetException(new IOException("Codex disconnected")); }
    }

    static async Task DrainErrorsAsync(Process child)
    {
        try { while (await child.StandardError.ReadLineAsync() != null) { } }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or InvalidOperationException) { }
    }

    public async ValueTask DisposeAsync()
    {
        if (process is { } child)
        {
            process = null;
            try { child.StandardInput.Close(); if (!child.HasExited) child.Kill(entireProcessTree: true); }
            catch (Exception ex) when (ex is InvalidOperationException or System.ComponentModel.Win32Exception or IOException) { }
            try { await child.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(2)); } catch { }
            child.Dispose();
        }
        foreach (var entry in pending.Values) entry.TrySetCanceled();
    }
}

public sealed class QuotaReadException(string title, string hint) : Exception(title)
{
    public string Hint { get; } = hint;
}
