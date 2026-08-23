using System.Net;
using System.Text;
using Kkindle.Infrastructure;

namespace Kkindle.Tests;

public sealed class DoubanMetadataTests
{
    [Fact]
    public async Task SearchParsesEmbeddedCandidateData()
    {
        using var service = new DoubanMetadataService(new TestHelpers.StubHttpMessageHandler(request =>
        {
            Assert.Equal("book.douban.com", request.RequestUri?.Host);
            Assert.Contains("subject_search", request.RequestUri?.AbsolutePath);
            return HtmlResponse("""
                <html><script>
                window.__DATA__ = {"count":1,"items":[{"abstract":"[美] 莫提默·艾德勒 / 商务印书馆 / 2004","cover_url":"https://img.example/cover.jpg","id":1013208,"rating":{"count":81342,"value":8.3},"title":"如何阅读一本书","url":"https://book.douban.com/subject/1013208/"}]}; window.__USER__ = {};
                </script></html>
                """);
        }), TimeSpan.Zero);

        var results = await service.SearchAsync("如何阅读一本书", "艾德勒");

        var result = Assert.Single(results);
        Assert.Equal(1013208, result.SubjectId);
        Assert.Equal("如何阅读一本书", result.Title);
        Assert.Equal(8.3, result.Rating);
        Assert.Equal(81342, result.RatingCount);
    }

    [Fact]
    public async Task SearchParsesEveryReturnedItemWithoutTruncation()
    {
        // A page carries ~15 items (count:15, total:300+); an old cap kept only
        // the first 10, which read as "incomplete results" in the picker.
        var items = string.Join(",", Enumerable.Range(1, 15).Select(index =>
            "{\"abstract\":\"简介 " + index + "\",\"cover_url\":\"https://img.example/" + index +
            ".jpg\",\"id\":" + (1000 + index) + ",\"title\":\"书名" + index +
            "\",\"url\":\"https://book.douban.com/subject/" + (1000 + index) + "/\"}"));
        using var service = new DoubanMetadataService(new TestHelpers.StubHttpMessageHandler(_ => HtmlResponse(
            $$"""
            <html><script>
            window.__DATA__ = {"count":15,"total":304,"items":[{{items}}]}; window.__USER__ = {};
            </script></html>
            """)), TimeSpan.Zero);

        var results = await service.SearchAsync("三体", "刘慈欣");

        Assert.Equal(15, results.Count);
        Assert.Equal("书名15", results[^1].Title);
    }

    [Fact]
    public async Task DetailsParsesStructuredDataInfoAndIntroduction()
    {
        using var service = new DoubanMetadataService(new TestHelpers.StubHttpMessageHandler(_ => HtmlResponse("""
            <html><head>
              <meta property="og:title" content="如何阅读一本书">
              <meta property="og:url" content="https://book.douban.com/subject/1013208/">
              <meta property="og:image" content="https://img.example/cover.jpg">
              <script type="application/ld+json">{"@type":"Book","name":"如何阅读一本书","isbn":"9787100040945","author":[{"@type":"Person","name":"莫提默·艾德勒"},{"@type":"Person","name":"查尔斯·范多伦"}]}</script>
            </head><body>
              <div id="info">
                <span class="pl">出版社:</span> 商务印书馆<br>
                <span class="pl">出版年:</span> 2004-1<br>
                <span class="pl">页数:</span> 376<br>
                <span class="pl">装帧:</span> 平装<br>
                <span class="pl">定价:</span> 38.00元<br>
                <span class="pl">丛书:</span> 汉译世界学术名著丛书<br>
              </div>
              <strong class="ll rating_num">8.3</strong><span property="v:votes">81342</span>
              <h2><span>内容简介</span></h2><div class="intro"><p>一本关于阅读方法的经典作品。</p><p>介绍四个阅读层次。</p></div>
            </body></html>
            """)), TimeSpan.Zero);
        var candidate = new DoubanBookCandidate(1013208, "如何阅读一本书", "", "", "https://book.douban.com/subject/1013208/", null, 0);

        var result = await service.GetDetailsAsync(candidate);

        Assert.Equal("如何阅读一本书", result.Title);
        Assert.Equal("莫提默·艾德勒 / 查尔斯·范多伦", result.Authors);
        Assert.Equal("商务印书馆", result.Publisher);
        Assert.Equal("9787100040945", result.Isbn);
        Assert.Equal("汉译世界学术名著丛书", result.Series);
        Assert.Contains("四个阅读层次", result.Description);
        Assert.Equal(8.3, result.Rating);
        Assert.Equal(81342, result.RatingCount);
    }

    [Fact]
    public async Task CoverDownloadUsesDoubanReferer()
    {
        var expected = new byte[] { 0xFF, 0xD8, 0xFF, 0xD9 };
        using var service = new DoubanMetadataService(new TestHelpers.StubHttpMessageHandler(request =>
        {
            Assert.Equal("https://book.douban.com/", request.Headers.Referrer?.AbsoluteUri);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(expected)
                {
                    Headers = { ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("image/jpeg") }
                }
            };
        }), TimeSpan.Zero);

        var result = await service.DownloadCoverAsync("https://img1.doubanio.com/cover.jpg");

        Assert.Equal(expected, result);
    }

    private static HttpResponseMessage HtmlResponse(string html) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(html, Encoding.UTF8, "text/html")
    };

}
