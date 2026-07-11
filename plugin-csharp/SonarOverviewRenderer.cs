using System.Net;
using System.Text;

namespace StreamDockSonar;

public static class SonarOverviewRenderer
{
    public static string BuildImageDataUrl(IReadOnlyList<SonarOverviewState> states)
    {
        var cells = states.Count <= 1 ? 1 : states.Count <= 2 ? 2 : states.Count <= 4 ? 4 : 6;
        var columns = cells == 1 ? 1 : 2;
        var rows = (int)Math.Ceiling(cells / (double)columns);
        const int width = 144;
        const int height = 144;
        var cellWidth = width / columns;
        var cellHeight = height / rows;
        var labelSize = states.Count <= 2 ? 20 : 15;
        var valueSize = states.Count <= 2 ? 34 : 24;

        var svg = new StringBuilder();
        svg.Append($"""<svg xmlns="http://www.w3.org/2000/svg" width="{width}" height="{height}" viewBox="0 0 {width} {height}">""");
        svg.Append("""<rect width="144" height="144" fill="#16191f"/>""");

        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            var column = index % columns;
            var row = index / columns;
            var x = column * cellWidth;
            var y = row * cellHeight;
            var centerX = x + cellWidth / 2;
            var labelY = y + Math.Max(17, cellHeight * 0.32);
            var valueY = y + Math.Max(40, cellHeight * 0.72);
            var color = state.Error != null ? "#f2c94c" : state.Muted == true ? "#ff5c6c" : "#f5f7fb";

            if (column > 0) svg.Append($"""<line x1="{x}" y1="{y + 8}" x2="{x}" y2="{y + cellHeight - 8}" stroke="#303846" stroke-width="1"/>""");
            if (row > 0 && column == 0) svg.Append($"""<line x1="8" y1="{y}" x2="136" y2="{y}" stroke="#303846" stroke-width="1"/>""");
            svg.Append($"""<text x="{centerX}" y="{labelY}" text-anchor="middle" fill="#9aa6b2" font-family="Arial, sans-serif" font-size="{labelSize}" font-weight="700">{Escape(state.ShortLabel)}</text>""");
            svg.Append($"""<text x="{centerX}" y="{valueY}" text-anchor="middle" fill="{color}" font-family="Arial, sans-serif" font-size="{valueSize}" font-weight="800">{Escape(state.ValueText)}</text>""");
        }

        svg.Append("</svg>");
        return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(svg.ToString()));
    }

    private static string Escape(string value)
    {
        return WebUtility.HtmlEncode(value);
    }
}
