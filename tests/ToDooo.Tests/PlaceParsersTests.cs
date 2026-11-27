using ToDooo.Services;
using Xunit;

namespace ToDooo.Tests;

public class PlaceParsersTests
{
    [Fact]
    public void Baidu_ParsesDollarDelimitedSuggestions()
    {
        const string json = """
        {"q":"温州医科大学","p":false,"t":0,"s":[
          "温州市$瓯海区$$温州医科大学(茶山校区)-同仁楼$178$",
          "$$$星巴克$0$",
          "温州市$瓯海区$$温州医科大学(茶山校区)-同仁楼$178$"
        ]}
        """;

        var hits = PlaceParsers.ParseBaidu(json, limit: 6);

        Assert.Equal(2, hits.Count);
        Assert.Equal("温州医科大学(茶山校区)-同仁楼", hits[0].Suggestion.Name);
        Assert.Equal("温州医科大学(茶山校区)-同仁楼,温州市瓯海区", hits[0].Suggestion.DisplayName);
        Assert.Equal("温州市瓯海区", hits[0].Suggestion.Subtitle);
        Assert.Equal("星巴克", hits[1].Suggestion.Name);
        Assert.Equal("星巴克", hits[1].Suggestion.DisplayName);
    }

    [Fact]
    public void Nominatim_ParsesNameAddressAndCoordinates()
    {
        const string json = """
        [
          {"display_name":"温州医科大学, 茶山街道, 瓯海区, 温州市, 浙江省, 中国",
           "lat":"27.9021","lon":"120.6935"},
          {"display_name":"温州站, 鹿城区, 温州市, 浙江省, 中国","lat":"27.9833","lon":"120.6833"}
        ]
        """;

        var hits = PlaceParsers.ParseNominatim(json);

        Assert.Equal(2, hits.Count);
        Assert.Equal("温州医科大学", hits[0].Suggestion.Name);
        Assert.Equal(27.9021, hits[0].Lat!.Value, 4);
        Assert.Equal(120.6935, hits[0].Lon!.Value, 4);
        Assert.Empty(PlaceParsers.ParseNominatim("{}"));
    }

    [Fact]
    public void Photon_ReadsGeoJsonFeatures()
    {
        const string json = """
        {"type":"FeatureCollection","features":[
          {"type":"Feature",
           "properties":{"name":"温州站","city":"温州市","state":"浙江省","country":"中国"},
           "geometry":{"type":"Point","coordinates":[120.6833,27.9833]}}
        ]}
        """;

        var hits = PlaceParsers.ParsePhoton(json);

        var hit = Assert.Single(hits);
        Assert.Equal("温州站", hit.Suggestion.Name);
        Assert.Contains("温州市", hit.Suggestion.DisplayName);
        Assert.Equal(27.9833, hit.Lat!.Value, 4);
        Assert.Equal(120.6833, hit.Lon!.Value, 4);
    }

    [Fact]
    public void Photon_SkipsFeaturesWithoutAnyNameParts()
    {
        const string json = """{"features":[{"properties":{},"geometry":{"coordinates":[1,2]}}]}""";

        Assert.Empty(PlaceParsers.ParsePhoton(json));
    }

    [Fact]
    public void Amap_ReadsPoisAndSwapsCoordinateOrder()
    {
        const string json = """
        {"status":"1","info":"OK","infocode":"10000","count":"1","pois":[
          {"name":"温州站","address":"温州大道","cityname":"温州市","adname":"鹿城区",
           "location":"120.6833,27.9833"}
        ]}
        """;

        var (hits, error) = PlaceParsers.ParseAmap(json);

        Assert.Null(error);
        var hit = Assert.Single(hits);
        Assert.Equal("温州站", hit.Suggestion.Name);
        Assert.Contains("温州市", hit.Suggestion.DisplayName);
        // 高德是「经度,纬度」，别写反
        Assert.Equal(27.9833, hit.Lat!.Value, 4);
        Assert.Equal(120.6833, hit.Lon!.Value, 4);
    }

    [Fact]
    public void Amap_SurfacesRejectionReasonInsteadOfEmptyList()
    {
        var (hits, error) = PlaceParsers.ParseAmap("""{"status":"0","info":"INVALID_USER_KEY","infocode":"10001"}""");

        Assert.Empty(hits);
        Assert.Equal("Key 无效，检查是否复制完整", error);
    }

    [Fact]
    public void Amap_UnknownInfoIsPassedThrough()
    {
        var (_, error) = PlaceParsers.ParseAmap("""{"status":"0","info":"SOMETHING_NEW"}""");

        Assert.Equal("SOMETHING_NEW", error);
    }

    [Fact]
    public void Urls_EscapeKeywordsAndClampLimit()
    {
        var nominatim = PlaceParsers.NominatimUrl("温州 医科大学", 99);
        Assert.Contains("q=%E6%B8%A9%E5%B7%9E%20%E5%8C%BB%E7%A7%91%E5%A4%A7%E5%AD%A6", nominatim);
        Assert.Contains("limit=10", nominatim);
        Assert.DoesNotContain("viewbox", nominatim);

        Assert.Contains("viewbox=1,2,3,4", PlaceParsers.NominatimUrl("x", 3, "1,2,3,4"));
        // 0 条会被夹到下限 1，别让服务端收到非法参数
        Assert.Contains("limit=1", PlaceParsers.PhotonUrl("x", 0));
        Assert.Contains("offset=10", PlaceParsers.AmapUrl("x", 50, "k"));
    }

    [Fact]
    public void AmapUrl_CarriesTheKeyAndKeepsItEscaped()
    {
        var url = PlaceParsers.AmapUrl("温州站", 4, "abc/def+=");

        Assert.Contains("key=abc%2Fdef%2B%3D", url);
        Assert.Contains("offset=4", url);
        Assert.DoesNotContain("city=", url);
    }

    [Fact]
    public void PhotonUrl_AddsLocationBiasOnlyWhenKnown()
    {
        Assert.DoesNotContain("lat=", PlaceParsers.PhotonUrl("x", 3));
        var biased = PlaceParsers.PhotonUrl("x", 3, 27.9, 120.6);
        Assert.Contains("lat=27.9", biased);
        Assert.Contains("lon=120.6", biased);
    }

    [Theory]
    [InlineData("温州站, 鹿城区, 温州市", "温州站")]
    [InlineData("温州站，鹿城区", "温州站")]
    [InlineData("温州站", "温州站")]
    [InlineData("  ", "")]
    public void FirstPart_TakesTheLeadingName(string display, string expected)
    {
        Assert.Equal(expected, PlaceParsers.FirstPart(display));
    }
}
