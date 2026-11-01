using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace CodexQuotaWidget;

public sealed record QuotaWindow(double UsedPercent, int? DurationMinutes, DateTimeOffset? ResetsAt)
{
    public double RemainingPercent => Math.Clamp(100 - UsedPercent, 0, 100);
    public string Label => DurationMinutes switch
    {
        10080 => "每周额度", 1440 => "每日额度", 300 => "5 小时额度",
        > 0 when DurationMinutes % 1440 == 0 => $"{DurationMinutes / 1440} 天额度",
        > 0 when DurationMinutes % 60 == 0 => $"{DurationMinutes / 60} 小时额度",
        > 0 => $"{DurationMinutes} 分钟额度", _ => "当前额度"
    };
    public string ExactReset => ResetsAt?.ToLocalTime().ToString("M月d日 HH:mm:ss", CultureInfo.InvariantCulture) + " 重置";
    public bool ResetDue(DateTimeOffset now) => ResetsAt is { } reset && reset <= now;
}

public sealed record QuotaBucket(string Id, string Name, string? Plan, List<QuotaWindow> Windows, string? CreditBalance, bool UnlimitedCredits)
{
    public string PlanLabel => Plan?.ToLowerInvariant() switch
    {
        "plus" => "PLUS", "pro" => "PRO", "free" => "FREE", "team" => "TEAM",
        "business" => "BUSINESS", "enterprise" => "ENTERPRISE", "edu" => "EDU",
        null or "" => "CODEX", _ => Plan.ToUpperInvariant()
    };
    public override string ToString() => Name;
}

public sealed record QuotaSnapshot(DateTimeOffset SampledAt, List<QuotaBucket> Buckets, bool? OrdinaryUsageAllowed)
{
    public static QuotaSnapshot Parse(JsonElement result, DateTimeOffset? sampledAt = null)
    {
        var buckets = new List<QuotaBucket>();
        if (result.ValueKind != JsonValueKind.Object) return new(sampledAt ?? DateTimeOffset.UtcNow, buckets, null);
        if (result.TryGetProperty("rateLimitsByLimitId", out var multi) && multi.ValueKind == JsonValueKind.Object)
            foreach (var entry in multi.EnumerateObject()) AddBucket(entry.Value, entry.Name, buckets);
        if (buckets.Count == 0 && result.TryGetProperty("rateLimits", out var single) && single.ValueKind == JsonValueKind.Object)
            AddBucket(single, "codex", buckets);
        buckets = buckets.OrderBy(b => b.Id == "codex" ? 0 : 1).ToList();
        bool? allowed = result.TryGetProperty("ordinaryUsageAllowed", out var permission) && permission.ValueKind is JsonValueKind.True or JsonValueKind.False ? permission.GetBoolean() : null;
        return new(sampledAt ?? DateTimeOffset.UtcNow, buckets, allowed);
    }

    static void AddBucket(JsonElement source, string fallbackId, List<QuotaBucket> buckets)
    {
        if (source.ValueKind != JsonValueKind.Object) return;
        var id = String(source, "limitId") ?? fallbackId;
        var windows = new List<QuotaWindow>();
        foreach (var key in new[] { "primary", "secondary" })
        {
            if (!source.TryGetProperty(key, out var win) || win.ValueKind != JsonValueKind.Object) continue;
            if (!win.TryGetProperty("usedPercent", out var used) || used.ValueKind != JsonValueKind.Number || !used.TryGetDouble(out var value) || !double.IsFinite(value)) continue;
            int? duration = win.TryGetProperty("windowDurationMins", out var mins) && mins.ValueKind == JsonValueKind.Number && mins.TryGetInt32(out var n) && n > 0 ? n : null;
            DateTimeOffset? reset = null;
            if (win.TryGetProperty("resetsAt", out var stamp) && stamp.ValueKind == JsonValueKind.Number && stamp.TryGetInt64(out var unix))
                try { reset = DateTimeOffset.FromUnixTimeSeconds(unix); } catch (ArgumentOutOfRangeException) { }
            windows.Add(new(value, duration, reset));
        }
        string? balance = null;
        var unlimited = false;
        if (source.TryGetProperty("credits", out var credits) && credits.ValueKind == JsonValueKind.Object)
        {
            balance = String(credits, "balance");
            unlimited = credits.TryGetProperty("unlimited", out var u) && u.ValueKind == JsonValueKind.True;
        }
        buckets.Add(new(id, String(source, "limitName") ?? (id == "codex" ? "Codex" : id), String(source, "planType"),
            windows.OrderBy(w => w.DurationMinutes ?? int.MaxValue).ToList(), balance, unlimited));
    }

    static string? String(JsonElement obj, string key) => obj.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
}

public static class Countdown
{
    public static string Format(DateTimeOffset? reset, DateTimeOffset now)
    {
        if (reset is null) return "时间待提供";
        var seconds = Math.Ceiling((reset.Value - now).TotalSeconds);
        if (seconds <= 0) return "正在确认重置";
        var span = TimeSpan.FromSeconds(seconds);
        return span.Days > 0 ? $"{span.Days}天 {span.Hours:00}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{(int)span.TotalHours:00}:{span.Minutes:00}:{span.Seconds:00}";
    }
}
