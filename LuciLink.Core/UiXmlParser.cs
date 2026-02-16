using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace LuciLink.Core;

/// <summary>
/// uiautomator dump XML 내 개별 UI 요소 정보.
/// </summary>
public class UiElementInfo
{
    public string ResourceId { get; set; } = "";
    public string ClassName { get; set; } = "";
    public string Text { get; set; } = "";
    public string ContentDesc { get; set; } = "";
    public string PackageName { get; set; } = "";
    public int Left { get; set; }
    public int Top { get; set; }
    public int Right { get; set; }
    public int Bottom { get; set; }
    public int Width => Right - Left;
    public int Height => Bottom - Top;
    public int Area => Width * Height;
    public string BoundsString => $"[{Left},{Top}][{Right},{Bottom}]";

    /// <summary>원본 XML 노드의 속성 전체 (리포트용)</summary>
    public string RawAttributes { get; set; } = "";

    public bool ContainsPoint(int x, int y) =>
        x >= Left && x <= Right && y >= Top && y <= Bottom;

    public override string ToString() =>
        $"{ClassName} ({ResourceId}) {BoundsString}";
}

/// <summary>
/// uiautomator dump 출력 XML을 파싱하여 UI 요소 리스트로 변환하고,
/// 좌표 기반으로 특정 UI 요소를 검색하는 파서.
/// </summary>
public class UiXmlParser
{
    // bounds="[left,top][right,bottom]" 파싱용 정규식
    private static readonly Regex BoundsRegex = new(
        @"\[(\d+),(\d+)\]\[(\d+),(\d+)\]",
        RegexOptions.Compiled);

    /// <summary>
    /// XML 전체를 파싱하여 모든 UI 요소 리스트를 반환합니다.
    /// </summary>
    public List<UiElementInfo> Parse(string xml)
    {
        // 1차: XML 정리 후 XDocument.Parse 시도
        var sanitized = SanitizeXml(xml);
        try
        {
            return ParseWithXDocument(sanitized);
        }
        catch
        {
            // 2차: 정규식 기반 폴백 파서
            return ParseWithRegex(xml);
        }
    }

    /// <summary>비표준 XML 정리 (uiautomator dump 호환)</summary>
    private static string SanitizeXml(string xml)
    {
        // 이스케이프 안 된 & 처리 (&amp; &lt; &gt; &quot; &apos; 제외)
        xml = Regex.Replace(xml, @"&(?!amp;|lt;|gt;|quot;|apos;|#)", "&amp;");
        // 제어 문자 제거 (탭, 줄바꿈 제외)
        xml = Regex.Replace(xml, @"[\x00-\x08\x0B\x0C\x0E-\x1F]", "");
        return xml;
    }

    private List<UiElementInfo> ParseWithXDocument(string xml)
    {
        var elements = new List<UiElementInfo>();
        var doc = XDocument.Parse(xml);
        foreach (var node in doc.Descendants("node"))
        {
            var boundsStr = node.Attribute("bounds")?.Value ?? "";
            var match = BoundsRegex.Match(boundsStr);
            if (!match.Success) continue;

            var element = new UiElementInfo
            {
                ResourceId = node.Attribute("resource-id")?.Value ?? "",
                ClassName = node.Attribute("class")?.Value ?? "",
                Text = node.Attribute("text")?.Value ?? "",
                ContentDesc = node.Attribute("content-desc")?.Value ?? "",
                PackageName = node.Attribute("package")?.Value ?? "",
                Left = int.Parse(match.Groups[1].Value),
                Top = int.Parse(match.Groups[2].Value),
                Right = int.Parse(match.Groups[3].Value),
                Bottom = int.Parse(match.Groups[4].Value),
                RawAttributes = string.Join(", ",
                    node.Attributes().Select(a => $"{a.Name}=\"{a.Value}\""))
            };

            if (element.Area > 0)
                elements.Add(element);
        }
        return elements;
    }

    // 정규식 폴백: <node ... /> 패턴에서 속성 추출
    private static readonly Regex NodeRegex = new(
        @"<node\s([^>]+?)/?>" ,
        RegexOptions.Compiled | RegexOptions.Singleline);
    private static readonly Regex AttrRegex = new(
        @"(\w[\w\-]*)=""([^""]*?)""",
        RegexOptions.Compiled);

    private List<UiElementInfo> ParseWithRegex(string xml)
    {
        var elements = new List<UiElementInfo>();
        foreach (Match nodeMatch in NodeRegex.Matches(xml))
        {
            var attrs = new Dictionary<string, string>();
            foreach (Match attrMatch in AttrRegex.Matches(nodeMatch.Groups[1].Value))
                attrs[attrMatch.Groups[1].Value] = attrMatch.Groups[2].Value;

            if (!attrs.TryGetValue("bounds", out var boundsStr)) continue;
            var bm = BoundsRegex.Match(boundsStr);
            if (!bm.Success) continue;

            var element = new UiElementInfo
            {
                ResourceId = attrs.GetValueOrDefault("resource-id", ""),
                ClassName = attrs.GetValueOrDefault("class", ""),
                Text = attrs.GetValueOrDefault("text", ""),
                ContentDesc = attrs.GetValueOrDefault("content-desc", ""),
                PackageName = attrs.GetValueOrDefault("package", ""),
                Left = int.Parse(bm.Groups[1].Value),
                Top = int.Parse(bm.Groups[2].Value),
                Right = int.Parse(bm.Groups[3].Value),
                Bottom = int.Parse(bm.Groups[4].Value),
                RawAttributes = nodeMatch.Groups[1].Value.Trim()
            };

            if (element.Area > 0)
                elements.Add(element);
        }
        return elements;
    }

    /// <summary>
    /// 안드로이드 좌표 기준으로 해당 위치에 있는 UI 요소를 검색합니다.
    /// 여러 요소가 겹칠 경우, 면적이 가장 작은 요소(가장 구체적인 leaf node)를 반환합니다.
    /// </summary>
    public UiElementInfo? FindElementAt(List<UiElementInfo> elements, int x, int y)
    {
        UiElementInfo? best = null;

        foreach (var element in elements)
        {
            if (!element.ContainsPoint(x, y)) continue;

            if (best == null || element.Area < best.Area)
                best = element;
        }

        return best;
    }

    /// <summary>
    /// 특정 좌표에 포함되는 모든 UI 요소를 면적 오름차순으로 반환합니다.
    /// (leaf node 부터 root 까지)
    /// </summary>
    public List<UiElementInfo> FindAllElementsAt(List<UiElementInfo> elements, int x, int y)
    {
        return elements
            .Where(e => e.ContainsPoint(x, y))
            .OrderBy(e => e.Area)
            .ToList();
    }
}
