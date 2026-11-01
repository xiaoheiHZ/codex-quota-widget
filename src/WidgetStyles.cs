using System.Linq;

namespace CodexQuotaWidget;

public sealed record WidgetStyle(string Id, string Name, double Width);

public static class WidgetStyles
{
    public static readonly WidgetStyle[] All =
    [
        new("panel", "经典面板", 356),
        new("strip", "横向状态条", 574),
        new("clock", "数字时钟", 356),
        new("orbit", "迷你仪表", 356)
    ];
    public static WidgetStyle Resolve(string? id) => All.FirstOrDefault(s => s.Id == id) ?? All[0];
}
