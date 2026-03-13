using XmlJsonFormatter.Services;

namespace XmlJsonFormatter.Tests;

public class XmlServiceTests
{
    private const string SampleXml =
        "<?xml version=\"1.0\" encoding=\"utf-16\"?>\n" +
        "<xml id=\"789abc\">\n" +
        "    <car>\n" +
        "        <wheel>17789</wheel>\n" +
        "        <color>blue</color>\n" +
        "    </car>\n" +
        "</xml>";

    // Line numbers (1-based):
    //  1: <?xml version="1.0" encoding="utf-16"?>
    //  2: <xml id="789abc">
    //  3:     <car>
    //  4:         <wheel>17789</wheel>
    //  5:         <color>blue</color>
    //  6:     </car>
    //  7: </xml>

    [Fact]
    public void GetXPathAtLine_ColorElement_ReturnsCorrectPath()
    {
        var result = XmlService.GetXPathAtLine(SampleXml, 5);
        Assert.Equal("/xml/car/color", result);
    }

    [Fact]
    public void GetXPathAtLine_WheelElement_ReturnsCorrectPath()
    {
        var result = XmlService.GetXPathAtLine(SampleXml, 4);
        Assert.Equal("/xml/car/wheel", result);
    }

    [Fact]
    public void GetXPathAtLine_CarElement_ReturnsCorrectPath()
    {
        var result = XmlService.GetXPathAtLine(SampleXml, 3);
        Assert.Equal("/xml/car", result);
    }

    [Fact]
    public void GetXPathAtLine_RootElement_ReturnsCorrectPath()
    {
        var result = XmlService.GetXPathAtLine(SampleXml, 2);
        Assert.Equal("/xml", result);
    }

    [Fact]
    public void GetXPathAtLine_SiblingElements_IncludeIndex()
    {
        const string xml =
            "<root>\n" +
            "    <item>a</item>\n" +
            "    <item>b</item>\n" +
            "    <item>c</item>\n" +
            "</root>";

        // Line 3 → second <item>
        var result = XmlService.GetXPathAtLine(xml, 3);
        Assert.Equal("/root/item[2]", result);
    }

    // ── Attribute ──────────────────────────────────────────────────────────

    // SampleXml line 2: <xml id="789abc">
    // Characters:        1234567890...
    //  col 1 = '<', 2='x', 3='m', 4='l', 5=' ', 6='i'  → "id" starts at col 6
    [Theory]
    [InlineData(6)]   // on the 'i' of "id"
    [InlineData(7)]   // on the 'd' of "id"
    [InlineData(9)]   // on the '"' opening the value
    [InlineData(12)]  // inside the value "789abc"
    public void GetXPathAtLine_AttributeOnLine_ReturnsAttributePath(int col)
    {
        var result = XmlService.GetXPathAtLine(SampleXml, targetLine: 2, targetColumn: col);
        Assert.Equal("/xml/@id", result);
    }

    [Fact]
    public void GetXPathAtLine_NoColumnProvided_ReturnsElementPath()
    {
        // Column 0 means "no column info" — should return element, not attribute
        var result = XmlService.GetXPathAtLine(SampleXml, targetLine: 2, targetColumn: 0);
        Assert.Equal("/xml", result);
    }

    [Fact]
    public void GetXPathAtLine_ColumnBeforeAttribute_ReturnsElementPath()
    {
        // Column 1 is '<', before the 'id' attribute at col 6
        var result = XmlService.GetXPathAtLine(SampleXml, targetLine: 2, targetColumn: 1);
        Assert.Equal("/xml", result);
    }
}
