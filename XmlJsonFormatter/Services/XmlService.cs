using System.IO;
using System.Text;
using System.Xml;
using System.Xml.XPath;
using XmlJsonFormatter.Models;

namespace XmlJsonFormatter.Services;

public static class XmlService
{
    /// <summary>
    /// Pretty-prints the given XML string with 4-space indentation.
    /// </summary>
    public static string FormatXml(string xml)
    {
        var doc = new XmlDocument { PreserveWhitespace = false };
        doc.LoadXml(xml);

        var settings = new XmlWriterSettings
        {
            Indent = true,
            IndentChars = "    ",
            NewLineChars = "\r\n",
            NewLineHandling = NewLineHandling.Replace,
            OmitXmlDeclaration = false
        };

        var sb = new StringBuilder();
        using (var writer = XmlWriter.Create(sb, settings))
        {
            doc.Save(writer);
        }
        return sb.ToString();
    }

    /// <summary>
    /// Parses the XML and returns the XPath of the element whose opening tag
    /// is on or immediately before <paramref name="targetLine"/>.
    /// Returns null if the XML is invalid or no node is found.
    /// </summary>
    public static string? GetXPathAtLine(string xmlText, int targetLine, int targetColumn = 0)
    {
        try
        {
            return ScanForXPathAtLine(xmlText, targetLine, targetColumn);
        }
        catch (XmlException)
        {
            // XML is not currently valid — can't determine XPath
            return null;
        }
    }

    // XmlDocument nodes in .NET 8 do not implement IXmlLineInfo, so we scan
    // directly with XmlReader (which does implement IXmlLineInfo) instead.
    private static string? ScanForXPathAtLine(string xmlText, int targetLine, int targetColumn)
    {
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };

        // Element entries: (path segments, line)
        var allElements = new List<(List<(string name, int idx)> segs, int line)>();
        // Attribute entries: (element path segments, attrName, line, col)
        var allAttrs = new List<(List<(string name, int idx)> elemSegs, string attrName, int line, int col)>();

        var depthStack = new Stack<(string name, int idx)>();
        var childCounters = new Stack<Dictionary<string, int>>();
        childCounters.Push(new Dictionary<string, int>());

        using (var reader = XmlReader.Create(new StringReader(xmlText), settings))
        {
            var lineInfo = (IXmlLineInfo)reader;
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.Element)
                {
                    string name = reader.Name;
                    bool isEmpty = reader.IsEmptyElement;
                    int line = lineInfo.LineNumber;

                    var counters = childCounters.Peek();
                    counters.TryGetValue(name, out int idx);
                    counters[name] = ++idx;

                    var segs = depthStack.Reverse()
                                         .Concat(new[] { (name, idx) })
                                         .ToList();
                    allElements.Add((segs, line));

                    // Collect attributes with their positions
                    if (reader.HasAttributes)
                    {
                        reader.MoveToFirstAttribute();
                        do
                        {
                            allAttrs.Add((segs, reader.Name, lineInfo.LineNumber, lineInfo.LinePosition));
                        } while (reader.MoveToNextAttribute());
                        reader.MoveToElement();
                    }

                    if (!isEmpty)
                    {
                        depthStack.Push((name, idx));
                        childCounters.Push(new Dictionary<string, int>());
                    }
                }
                else if (reader.NodeType == XmlNodeType.EndElement)
                {
                    if (depthStack.Count > 0) depthStack.Pop();
                    if (childCounters.Count > 1) childCounters.Pop();
                }
            }
        }

        // Find the element with the highest line number that is still <= targetLine
        List<(string name, int idx)>? bestSegs = null;
        int bestLine = 0;
        foreach (var (segs, line) in allElements)
        {
            if (line <= targetLine && line > bestLine)
            {
                bestLine = line;
                bestSegs = segs;
            }
        }

        // Determine max sibling indices so we can omit [n] for unique names
        var maxIndices = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (segs, _) in allElements)
        {
            for (int i = 0; i < segs.Count; i++)
            {
                var (name, idx) = segs[i];
                string parentKey = i == 0
                    ? string.Empty
                    : string.Join("/", segs.Take(i).Select(s => $"{s.name}[{s.idx}]"));
                string key = parentKey + "|" + name;
                if (!maxIndices.TryGetValue(key, out int max) || idx > max)
                    maxIndices[key] = idx;
            }
        }

        // Check for attribute match when column is provided:
        // find the attribute on targetLine whose start col is closest to (but not past) targetColumn.
        if (targetColumn > 0)
        {
            var attrMatch = allAttrs
                .Where(a => a.line == targetLine && a.col <= targetColumn)
                .OrderByDescending(a => a.col)
                .FirstOrDefault();

            if (attrMatch != default)
                return BuildDisplayPath(attrMatch.elemSegs, maxIndices) + "/@" + attrMatch.attrName;
        }

        if (bestSegs is null) return null;
        return BuildDisplayPath(bestSegs, maxIndices);
    }

    private static string BuildDisplayPath(List<(string name, int idx)> segs, Dictionary<string, int> maxIndices)
    {
        var parts = new List<string>(segs.Count);
        for (int i = 0; i < segs.Count; i++)
        {
            var (name, idx) = segs[i];
            string parentKey = i == 0
                ? string.Empty
                : string.Join("/", segs.Take(i).Select(s => $"{s.name}[{s.idx}]"));
            string key = parentKey + "|" + name;
            int maxIdx = maxIndices.TryGetValue(key, out int m) ? m : 1;
            parts.Add(maxIdx > 1 ? $"{name}[{idx}]" : name);
        }
        return "/" + string.Join("/", parts);
    }

    /// <summary>
    /// Executes an XPath expression against the XML string and returns
    /// a list of result items carrying the resolved XPath and line numbers.
    /// </summary>
    public static List<XPathResultItem> ExecuteXPath(string xmlText, string xpathExpression)
    {
        var results = new List<XPathResultItem>();

        var doc = new XmlDocument();
        doc.LoadXml(xmlText);

        XmlNodeList? nodeList;
        try
        {
            nodeList = doc.SelectNodes(xpathExpression);
        }
        catch (XPathException ex)
        {
            throw new InvalidOperationException($"Invalid XPath expression: {ex.Message}", ex);
        }

        if (nodeList is null) return results;

        // XmlDocument nodes in .NET 8 don't implement IXmlLineInfo, so build a
        // separate indexed-xpath → line map using XmlReader (which does).
        var lineMap = BuildIndexedXPathLineMap(xmlText);

        foreach (XmlNode node in nodeList)
        {
            string indexedXPath = BuildIndexedXPath(node);
            lineMap.TryGetValue(indexedXPath, out int lineNum);

            results.Add(new XPathResultItem
            {
                XPath = BuildXPath(node),
                Preview = BuildPreview(node),
                LineNumber = lineNum
            });
        }

        return results;
    }

    // Scan with XmlReader to build a map of fully-indexed-xpath → line number.
    // E.g. "/root[1]/item[2]" → 5
    private static Dictionary<string, int> BuildIndexedXPathLineMap(string xmlText)
    {
        var map = new Dictionary<string, int>(StringComparer.Ordinal);
        var settings = new XmlReaderSettings { DtdProcessing = DtdProcessing.Ignore };

        var depthStack = new Stack<(string name, int idx)>();
        var childCounters = new Stack<Dictionary<string, int>>();
        childCounters.Push(new Dictionary<string, int>());

        using var reader = XmlReader.Create(new StringReader(xmlText), settings);
        var lineInfo = (IXmlLineInfo)reader;
        while (reader.Read())
        {
            if (reader.NodeType == XmlNodeType.Element)
            {
                string name = reader.Name;
                bool isEmpty = reader.IsEmptyElement;
                int line = lineInfo.LineNumber;

                var counters = childCounters.Peek();
                counters.TryGetValue(name, out int idx);
                counters[name] = ++idx;

                var segs = depthStack.Reverse().Concat(new[] { (name, idx) });
                string key = "/" + string.Join("/", segs.Select(s => $"{s.name}[{s.idx}]"));
                map[key] = line;

                if (!isEmpty)
                {
                    depthStack.Push((name, idx));
                    childCounters.Push(new Dictionary<string, int>());
                }
            }
            else if (reader.NodeType == XmlNodeType.EndElement)
            {
                if (depthStack.Count > 0) depthStack.Pop();
                if (childCounters.Count > 1) childCounters.Pop();
            }
        }

        return map;
    }

    // Build a fully-indexed XPath from an XmlDocument node, e.g. /root[1]/item[2]
    // Used to look up the node in the XmlReader-derived line map.
    private static string BuildIndexedXPath(XmlNode node)
    {
        var parts = new Stack<string>();
        var current = node;
        while (current is not null and not XmlDocument)
        {
            if (current.NodeType == XmlNodeType.Element)
            {
                int idx = 1;
                var sibling = current.ParentNode?.FirstChild;
                int count = 0;
                while (sibling is not null)
                {
                    if (sibling.NodeType == XmlNodeType.Element && sibling.Name == current.Name)
                    {
                        count++;
                        if (sibling == current) idx = count;
                    }
                    sibling = sibling.NextSibling;
                }
                parts.Push($"{current.Name}[{idx}]");
            }
            current = current.ParentNode;
        }
        return "/" + string.Join("/", parts);
    }

    // ──────────────────────────────────── helpers ────────────────────────────────────

#pragma warning disable CS0618 // XmlTextReader is obsolete but is the only way to get IXmlLineInfo on nodes after doc.Load()
    private static XmlDocument LoadWithLineInfo(string xmlText)
    {
        var doc = new XmlDocument();
        using var reader = new XmlTextReader(new StringReader(xmlText));
        doc.Load(reader);
        return doc;
    }
#pragma warning restore CS0618

    /// <summary>
    /// Builds an absolute XPath string for the given node,
    /// e.g. /catalog/book[2]/title
    /// </summary>
    public static string BuildXPath(XmlNode node)
    {
        var parts = new Stack<string>();
        var current = node;

        while (current is not null and not XmlDocument)
        {
            parts.Push(GetXPathSegment(current));
            current = current.ParentNode;
        }

        return "/" + string.Join("/", parts);
    }

    private static string GetXPathSegment(XmlNode node)
    {
        if (node is XmlAttribute attr)
            return "@" + attr.Name;

        if (node.NodeType != XmlNodeType.Element)
            return node.Name;

        var parent = node.ParentNode;
        if (parent is null or XmlDocument)
            return node.Name;

        int index = 0, count = 0;
        foreach (XmlNode sibling in parent.ChildNodes)
        {
            if (sibling.NodeType == XmlNodeType.Element && sibling.Name == node.Name)
            {
                count++;
                if (sibling == node) index = count;
            }
        }

        return count > 1 ? $"{node.Name}[{index}]" : node.Name;
    }

    private static string BuildPreview(XmlNode node)
    {
        if (node is XmlElement element)
        {
            var sb = new StringBuilder();
            sb.Append('<').Append(element.Name);
            foreach (XmlAttribute a in element.Attributes)
                sb.Append($" {a.Name}=\"{a.Value}\"");

            if (!element.HasChildNodes)
            {
                sb.Append(" />");
            }
            else if (element.ChildNodes.Count == 1 && element.FirstChild is XmlText txt)
            {
                var val = txt.Value?.Trim() ?? string.Empty;
                if (val.Length > 50) val = val[..50] + "…";
                sb.Append($">{val}</{element.Name}>");
            }
            else
            {
                sb.Append($">…</{element.Name}>");
            }
            return sb.ToString();
        }

        if (node is XmlAttribute a2)
            return $"@{a2.Name} = \"{a2.Value}\"";

        if (node is XmlText t)
        {
            var v = t.Value?.Trim() ?? string.Empty;
            if (v.Length > 70) v = v[..70] + "…";
            return $"[text: {v}]";
        }

        var outer = node.OuterXml;
        return outer.Length > 100 ? outer[..100] + "…" : outer;
    }
}
