using System.Text;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Forum;

namespace Qinglan.Tests;

/// <summary>M4 论坛引擎：Halo 列表/文章 API、RSS 兜底、渲染页正文、HTML 转纯文本。</summary>
internal static class ForumSuite
{
    public static void Run()
    {
        T.Group("M4 论坛列表 API");
        ListApi().GetAwaiter().GetResult();
        T.Group("M4 论坛 RSS 兜底");
        Rss().GetAwaiter().GetResult();
        T.Group("M4 论坛文章与渲染页");
        Articles().GetAwaiter().GetResult();
        T.Group("M4 论坛 HTML 转文本");
        HtmlToText();
    }

    private const string ListJson = """
        {"page":1,"size":20,"total":2,"items":[
          {"metadata":{"name":"post-a"},
           "spec":{"title":"甲帖","publishTime":"2026-11-01T10:00:00Z","pinned":true},
           "status":{"excerpt":"摘要一","permalink":"/archives/post-a"},
           "owner":{"displayName":"Alice"},
           "stats":{"visit":12,"upvote":3}},
          {"metadata":{"name":"post-b"},
           "spec":{"title":"乙帖"},
           "status":{"permalink":"/archives/post-b"},
           "owner":{},"stats":{}}
        ]}
        """;

    private static async Task ListApi()
    {
        await using var srv = await MiniHttpServer.StartAsync(Fx.TempDir());
        srv.AddFile("/apis/api.content.halo.run/v1alpha1/posts", Encoding.UTF8.GetBytes(ListJson));
        QlDeployment.Set(new QlDeploymentConfig
        {
            Forum = new QlEndpointDeployment { BaseUrl = srv.BaseUrl },
        });

        var posts = await QlForum.FetchPostsAsync(srv.BaseUrl, 1, 20);
        T.CheckEq("列表两篇", 2, posts.Count);
        var first = posts[0];
        T.CheckEq("帖子名", "post-a", first.Name);
        T.CheckEq("标题", "甲帖", first.Title);
        T.CheckEq("作者", "Alice", first.Author!);
        T.CheckEq("发布时间", "2026-11-01T10:00:00Z", first.PublishTime!);
        T.CheckEq("摘要", "摘要一", first.Excerpt);
        T.CheckEq("永久链接", "/archives/post-a", first.Permalink);
        T.Check("置顶标记", first.Pinned);
        T.CheckEq("浏览数", 12, first.Visits);
        T.CheckEq("点赞数", 3, first.Upvotes);
        T.Check("列表来源不是 RSS", !first.FromRss);
        T.CheckEq("绝对地址使用论坛站点", srv.BaseUrl.TrimEnd('/') + "/archives/post-a", first.Url);

        T.CheckEq("缺字段标题为空", "乙帖", posts[1].Title);
        T.Check("缺作者为 null", posts[1].Author is null);
        T.CheckEq("缺统计回落 0", 0, posts[1].Visits);

        // HTTP 200 的错误信封必须转成 IOException（内容 API 实际故障形态）。
        srv.AddFile("/apis/api.content.halo.run/v1alpha1/posts", Encoding.UTF8.GetBytes(
            """{"detail":"服务器内部发生错误","status":500}"""));
        bool envelopeRejected = false;
        try
        {
            await QlForum.FetchPostsAsync(srv.BaseUrl, 1, 20);
        }
        catch (IOException ex)
        {
            envelopeRejected = ex.Message.Contains("错误信封", StringComparison.Ordinal);
        }
        T.Check("错误信封 → IOException", envelopeRejected);
    }

    private const string RssXml = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0" xmlns:dc="http://purl.org/dc/elements/1.1/">
          <channel>
            <title>轻蓝论坛</title><link>http://forum.example</link><description>feed</description>
            <item>
              <title>RSS 帖</title>
              <link>http://localhost:8090/archives/abc-123</link>
              <dc:creator>Bob</dc:creator>
              <pubDate>Mon, 03 Nov 2025 08:00:00 GMT</pubDate>
              <description><![CDATA[<p>第一段</p><p>第二段</p>]]></description>
            </item>
            <item><title>无链接帖</title><link></link></item>
          </channel>
        </rss>
        """;

    private static async Task Rss()
    {
        await using var srv = await MiniHttpServer.StartAsync(Fx.TempDir());
        srv.AddFile("/rss.xml", Encoding.UTF8.GetBytes(RssXml));

        var posts = await QlForum.FetchPostsFromRssAsync(srv.BaseUrl);
        T.CheckEq("空链接条目被跳过", 1, posts.Count);
        var post = posts[0];
        T.CheckEq("RSS 标题", "RSS 帖", post.Title);
        T.CheckEq("绝对链接修剪为站内路径", "/archives/abc-123", post.Permalink);
        T.CheckEq("RSS 帖子名取 slug", "abc-123", post.Name);
        T.CheckEq("dc:creator 作者", "Bob", post.Author!);
        T.CheckEq("RFC1123 日期格式化", "2025-11-03", post.PublishTime!);
        T.Check("摘要保留段落文字", post.Excerpt.Contains("第一段", StringComparison.Ordinal)
            && post.Excerpt.Contains("第二段", StringComparison.Ordinal));
        T.Check("摘要不含标签", !post.Excerpt.Contains('<'));
        T.Check("标记 RSS 来源", post.FromRss);
        T.Check("RSS 无统计字段", post.Visits == -1 && post.Upvotes == -1);

        srv.AddFile("/rss.xml", Encoding.UTF8.GetBytes("这不是 XML"));
        bool rejected = false;
        try
        {
            await QlForum.FetchPostsFromRssAsync(srv.BaseUrl);
        }
        catch (IOException ex)
        {
            rejected = ex.Message.Contains("RSS", StringComparison.Ordinal);
        }
        T.Check("坏 RSS → IOException", rejected);
    }

    private const string ArticleJson = """
        {"metadata":{"name":"post-a"},
         "spec":{"title":"甲帖","publishTime":"2026-11-01T10:00:00Z"},
         "status":{"permalink":"/archives/post-a"},
         "owner":{"displayName":"Alice"},
         "content":{"content":"<h1>标题</h1><p>正文 &amp; 实体</p>"}}
        """;

    private const string ArticlePageHtml = """
        <!DOCTYPE html><html><head><title>页面标题 - 轻蓝论坛</title></head>
        <body><article id="content" class="prose"><h2>小标题</h2><p>正文 &lt;x&gt; 与 <a href="/a">链接</a></p></article></body>
        </html>
        """;

    private static async Task Articles()
    {
        await using var srv = await MiniHttpServer.StartAsync(Fx.TempDir());
        srv.AddFile("/apis/api.content.halo.run/v1alpha1/posts/post-a",
            Encoding.UTF8.GetBytes(ArticleJson));
        srv.AddFile("/archives/page-slug", Encoding.UTF8.GetBytes(ArticlePageHtml));

        var article = await QlForum.FetchArticleAsync(srv.BaseUrl, "post-a");
        T.CheckEq("文章标题", "甲帖", article.Title);
        T.CheckEq("文章作者", "Alice", article.Author!);
        T.CheckEq("文章正文 HTML", "<h1>标题</h1><p>正文 &amp; 实体</p>", article.Html);
        T.CheckEq("文章永久链接", "/archives/post-a", article.Permalink);

        var page = await QlForum.FetchArticleFromPageAsync(srv.BaseUrl, "/archives/page-slug");
        T.CheckEq("渲染页标题去掉站点后缀", "页面标题", page.Title);
        T.Check("渲染页提取 article#content", page.Html.Contains("<h2>小标题</h2>", StringComparison.Ordinal));
        T.CheckEq("渲染页帖子名", "page-slug", page.Name);
        T.CheckEq("渲染页链接", "/archives/page-slug", page.Permalink);

        bool badPermalink = false;
        try
        {
            await QlForum.FetchArticleFromPageAsync(srv.BaseUrl, "/other/slug");
        }
        catch (IOException ex)
        {
            badPermalink = ex.Message.Contains("/archives/", StringComparison.Ordinal);
        }
        T.Check("非 /archives/ 链接被拒绝", badPermalink);

        // RSS 来源的帖子直接走渲染页；接口故障的非 RSS 帖子也会回落。
        var rssPost = new QlForumPost("page-slug", "RSS 帖", null, null, "", "/archives/page-slug",
            false, -1, -1, true);
        var fallback = await QlForum.FetchArticleForPostAsync(srv.BaseUrl, rssPost);
        T.Check("RSS 帖子正文走渲染页", fallback.Html.Contains("小标题", StringComparison.Ordinal));

        srv.FailPath("/apis/api.content.halo.run/v1alpha1/posts/post-a", 500);
        var apiPost = new QlForumPost("post-a", "甲帖", null, null, "", "/archives/page-slug",
            false, 0, 0, false);
        var apiFallback = await QlForum.FetchArticleForPostAsync(srv.BaseUrl, apiPost);
        T.Check("内容 API 故障时回落渲染页", apiFallback.Permalink == "/archives/page-slug");
    }

    private static void HtmlToText()
    {
        string text = QlForum.HtmlToText(
            "<h2>标题</h2><p>第一段<br/>换行 &amp; 实体</p><ul><li>项目</li></ul><blockquote>引用</blockquote>");
        T.Check("标题保留", text.Contains("标题", StringComparison.Ordinal));
        T.Check("br 转换行且解码实体", text.Contains("第一段\n换行 & 实体", StringComparison.Ordinal));
        T.Check("列表项带圆点", text.Contains("• 项目", StringComparison.Ordinal));
        T.Check("引用保留", text.Contains("引用", StringComparison.Ordinal));
        T.Check("不残留标签", !text.Contains('<'));
        T.Check("空 HTML 返回空串", QlForum.HtmlToText("") == "");
        T.Check("连续空行收敛", !QlForum.HtmlToText("<p>a</p><p></p><p></p><p>b</p>")
            .Contains("\n\n\n", StringComparison.Ordinal));
    }
}

