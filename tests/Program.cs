using System.Text.Json;
using CodexQuotaWidget;

var checks = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAIL: " + name);
    checks++; Console.WriteLine("PASS " + name);
}
QuotaSnapshot Read(string json) { using var doc = JsonDocument.Parse(json); return QuotaSnapshot.Parse(doc.RootElement); }
var both = Read("""{"rateLimits":{"limitId":"codex","planType":"plus","primary":{"usedPercent":69,"windowDurationMins":300,"resetsAt":1793518167},"secondary":{"usedPercent":11,"windowDurationMins":10080,"resetsAt":1794104967}}}""");
Check(both.Buckets[0].Windows[0].RemainingPercent == 31, "used 69 means remaining 31");
Check(both.Buckets[0].Windows[1].RemainingPercent == 89, "weekly 89 percent remaining");
Check(both.Buckets[0].Windows[0].ResetsAt!.Value.ToUnixTimeSeconds() == 1793518167, "Unix seconds, not milliseconds");
var weekly = Read("""{"rateLimits":{"primary":{"usedPercent":31,"windowDurationMins":10080},"secondary":null}}""");
Check(weekly.Buckets[0].Windows.Single().Label == "每周额度", "weekly-only primary has weekly label");
Check(weekly.Buckets[0].Windows.Single().ResetsAt == null, "missing reset remains unknown");
var invalid = Read("""{"rateLimits":{"primary":{"windowDurationMins":300},"secondary":{"usedPercent":18,"resetsAt":9223372036854775807}}}""");
Check(invalid.Buckets[0].Windows.Count == 1, "missing percentage is not invented as zero");
Check(invalid.Buckets[0].Windows[0].ResetsAt == null, "out of range reset is unknown");
Check(new QuotaWindow(120, 300, null).RemainingPercent == 0, "overlimit clamps to zero");
Check(new QuotaWindow(-10, 300, null).RemainingPercent == 100, "negative used clamps to 100");
Check(new QuotaWindow(12.5, 15, null).RemainingPercent == 87.5, "decimal percentages preserved");
Check(new QuotaWindow(12.5, 15, null).Label == "15 分钟额度", "nonstandard window label");
var multi = Read("""{"rateLimitsByLimitId":{"model_special":{"primary":{"usedPercent":40}},"codex":{"primary":{"usedPercent":20}}},"rateLimits":{"primary":{"usedPercent":99}}}""");
Check(multi.Buckets.Count == 2 && multi.Buckets[0].Id == "codex", "multi bucket view and Codex preferred");
Check(multi.Buckets[0].Windows[0].RemainingPercent == 80, "multi view overrides legacy view");
Check(Read("""{"rateLimitsByLimitId":{},"rateLimits":{"primary":{"usedPercent":20}}}""").Buckets.Count == 1, "legacy fallback");
Check(Read("{}").Buckets.Count == 0, "empty response stays empty");
Check(Read("""{"ordinaryUsageAllowed":false}""").OrdinaryUsageAllowed == false, "account restriction preserved");
var now = DateTimeOffset.FromUnixTimeSeconds(1793510000);
Check(Countdown.Format(now.AddSeconds(3661), now) == "01:01:01", "second countdown");
Check(Countdown.Format(now.AddDays(2).AddHours(3).AddMinutes(4).AddSeconds(5), now) == "2天 03:04:05", "day countdown");
Check(Countdown.Format(null, now) == "时间待提供", "unknown reset label");
Check(Countdown.Format(now, now) == "正在确认重置", "reset boundary needs verification");
Check(Countdown.Format(now.AddSeconds(-1), now) == "正在确认重置", "expired snapshot does not show fresh quota");
Check(Countdown.Format(now.AddMilliseconds(1), now) == "00:00:01", "subsecond rounds up");
Check(new QuotaWindow(40, 300, now).ResetDue(now), "reset due exactly at boundary");
Check(!new QuotaWindow(40, 300, null).ResetDue(now), "unknown reset cannot expire locally");
Check(Read("""{"rateLimits":{"primary":{"usedPercent":null},"secondary":{"usedPercent":"unknown"}}}""").Buckets[0].Windows.Count == 0, "null and nonnumeric percentages stay unknown");
Check(Read("null").Buckets.Count == 0, "null RPC result stays unknown");
Check(Read("""{"rateLimits":{"primary":{"usedPercent":42,"windowDurationMins":null,"resetsAt":null}}}""").Buckets[0].Windows.Single().ResetsAt == null, "nullable duration and reset accepted");
Console.WriteLine($"{checks} checks passed.");
