using XmlJsonFormatter.Services;

namespace XmlJsonFormatter.Tests;

public class JsonServiceTests
{
    // Line numbers (1-based):
    //  1: {
    //  2:     "store": {
    //  3:         "name": "John",
    //  4:         "age": 30,
    //  5:         "books": [
    //  6:             {
    //  7:                 "title": "foo"
    //  8:             },
    //  9:             {
    // 10:                 "title": "bar"
    // 11:             }
    // 12:         ]
    // 13:     }
    // 14: }
    private const string SampleJson =
        "{\n" +
        "    \"store\": {\n" +
        "        \"name\": \"John\",\n" +
        "        \"age\": 30,\n" +
        "        \"books\": [\n" +
        "            {\n" +
        "                \"title\": \"foo\"\n" +
        "            },\n" +
        "            {\n" +
        "                \"title\": \"bar\"\n" +
        "            }\n" +
        "        ]\n" +
        "    }\n" +
        "}";

    [Fact]
    public void GetJsonPathAtLine_RootObject_ReturnsDollar()
    {
        var result = JsonService.GetJsonPathAtLine(SampleJson, 1);
        Assert.Equal("$", result);
    }

    [Fact]
    public void GetJsonPathAtLine_TopLevelProperty_ReturnsPath()
    {
        var result = JsonService.GetJsonPathAtLine(SampleJson, 2);
        Assert.Equal("$.store", result);
    }

    [Fact]
    public void GetJsonPathAtLine_NestedProperty_ReturnsPath()
    {
        var result = JsonService.GetJsonPathAtLine(SampleJson, 3);
        Assert.Equal("$.store.name", result);
    }

    [Fact]
    public void GetJsonPathAtLine_NumberProperty_ReturnsPath()
    {
        var result = JsonService.GetJsonPathAtLine(SampleJson, 4);
        Assert.Equal("$.store.age", result);
    }

    [Fact]
    public void GetJsonPathAtLine_ArrayItem_ReturnsPath()
    {
        var result = JsonService.GetJsonPathAtLine(SampleJson, 7);
        Assert.Equal("$.store.books[0].title", result);
    }

    [Fact]
    public void GetJsonPathAtLine_SecondArrayItem_ReturnsPath()
    {
        var result = JsonService.GetJsonPathAtLine(SampleJson, 10);
        Assert.Equal("$.store.books[1].title", result);
    }

    // ── Root array ─────────────────────────────────────────────────────────

    // Line numbers (1-based):
    //  1: [
    //  2:     {
    //  3:         "title": "foo"
    //  4:     },
    //  5:     {
    //  6:         "title": "bar"
    //  7:     }
    //  8: ]
    private const string RootArrayJson =
        "[\n" +
        "    {\n" +
        "        \"title\": \"foo\"\n" +
        "    },\n" +
        "    {\n" +
        "        \"title\": \"bar\"\n" +
        "    }\n" +
        "]";

    [Fact]
    public void GetJsonPathAtLine_RootArray_ReturnsDollar()
    {
        var result = JsonService.GetJsonPathAtLine(RootArrayJson, 1);
        Assert.Equal("$", result);
    }

    [Fact]
    public void GetJsonPathAtLine_RootArrayFirstElement_ReturnsIndexedPath()
    {
        var result = JsonService.GetJsonPathAtLine(RootArrayJson, 2);
        Assert.Equal("$[0]", result);
    }

    [Fact]
    public void GetJsonPathAtLine_RootArrayFirstElementProperty_ReturnsIndexedPath()
    {
        var result = JsonService.GetJsonPathAtLine(RootArrayJson, 3);
        Assert.Equal("$[0].title", result);
    }

    [Fact]
    public void GetJsonPathAtLine_RootArraySecondElementProperty_ReturnsIndexedPath()
    {
        var result = JsonService.GetJsonPathAtLine(RootArrayJson, 6);
        Assert.Equal("$[1].title", result);
    }
}
