using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core.Forum;

/// <summary>论坛帖子列表项。</summary>
public sealed record QlForumPost(string Name, string Title, string? Author, string? PublishTime,
    string Excerpt, string Permalink, bool Pinned, int Visits, int Upvotes, bool FromRss)
{
    /// <summary>帖子在论坛站点上的绝对地址。</summary>
    public string Url => QlForum.BaseUrl + Permalink;
}

/// <summary>一篇论坛文章的完整内容。</summary>
public sealed record QlForumArticle(string Name, string Title, string? Author, string? PublishTime,
    string Html, string Permalink)
{
    /// <summary>文章在论坛站点上的绝对地址。</summary>
    public string Url => QlForum.BaseUrl + Permalink;
}

/// <summary>
/// 轻蓝轨交论坛（Halo CMS）匿名客户端：帖子列表、RSS 兜底、文章正文。
/// 语义对齐 QL-HMCL 的 <c>QLForum</c>：内容 API 可能整体 500，此时列表切 RSS、
/// 正文切渲染页的 <c>article#content</c>；所有请求走普通 HTTP 并重试。
/// </summary>
public static class QlForum
{
    /// <summary>论坛根地址（HTTPS 不可用，保留 HTTP）。</summary>
    public const string BaseUrl = "http://forum.example.com";

    private const string ListApiPath = "/apis/api.content.halo.run/v1alpha1/posts";

    /// <summary>抓取一页帖子（最新在前）。</summary>
    /// <param name="page">1 起页码。</param>
    /// <param name="size">每页数量。</param>
    public static async Task<IReadOnlyList<QlForumPost>> FetchPostsAsync(int page, int size,
        CancellationToken ct = default)
        => await FetchPostsAsync(BaseUrl, page, size, ct).ConfigureAwait(false);

    /// <summary>抓取一页帖子（测试可指定 base URL）。</summary>
    public static async Task<IReadOnlyList<QlForumPost>> FetchPostsAsync(string baseUrl, int page,
        int size, CancellationToken ct = default)
    {
        string url = Normalize(baseUrl) + ListApiPath + "?size=" + size + "&page=" + page +
                     "&sort=publishTime,desc";
        string body = await QlDirectHttp.GetTextAsync(url, ct).ConfigureAwait(false);
        using var doc = ParseDocument(body, "post list");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("items", out var items) ||
            items.ValueKind != JsonValueKind.Array)
            throw new IOException("论坛列表请求失败：服务器返回错误信封");

        var posts = new List<QlForumPost>();
        foreach (var item in items.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var metadata = Child(item, "metadata");
            var spec = Child(item, "spec");
            var status = Child(item, "status");
            var owner = Child(item, "owner");
            var stats = Child(item, "stats");
            string name = Str(metadata, "name");
            if (string.IsNullOrEmpty(name)) continue;

            posts.Add(new QlForumPost(
                name,
                Str(spec, "title"),
                string.IsNullOrEmpty(Str(owner, "displayName")) ? null : Str(owner, "displayName"),
                string.IsNullOrEmpty(Str(spec, "publishTime")) ? null : Str(spec, "publishTime"),
                Str(status, "excerpt"),
                Str(status, "permalink", "/"),
                Bool(spec, "pinned"),
                Int(stats, "visit"),
                Int(stats, "upvote"),
                false));
        }
        return posts;
    }

    /// <summary>从 RSS 抓取全部帖子（内容 API 故障时的兜底）。</summary>
    public static async Task<IReadOnlyList<QlForumPost>> FetchPostsFromRssAsync(
        CancellationToken ct = default)
        => await FetchPostsFromRssAsync(BaseUrl, ct).ConfigureAwait(false);

    /// <summary>从指定站点的 RSS 抓取帖子（测试可指定 base URL）。</summary>
    public static async Task<IReadOnlyList<QlForumPost>> FetchPostsFromRssAsync(string baseUrl,
        CancellationToken ct = default)
    {
        string body = await QlDirectHttp.GetTextAsync(Normalize(baseUrl) + "/rss.xml", ct)
            .ConfigureAwait(false);
        XDocument document;
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
            };
            using var reader = XmlReader.Create(new StringReader(body), settings);
            document = XDocument.Load(reader);
        }
        catch (Exception e) when (e is XmlException or InvalidOperationException)
        {
            throw new IOException("收到的论坛 RSS 无法解析", e);
        }

        var posts = new List<QlForumPost>();
        foreach (var item in document.Descendants("item"))
        {
            string title = item.Element("title")?.Value ?? "";
            string link = item.Element("link")?.Value ?? "";
            if (string.IsNullOrWhiteSpace(title) || string.IsNullOrWhiteSpace(link)) continue;

            string permalink;
            if (link.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                link.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                int schemeLength = link.StartsWith("https", StringComparison.OrdinalIgnoreCase) ? 8 : 7;
                int pathAt = link.IndexOf('/', schemeLength);
                permalink = pathAt < 0 ? "/" : link[pathAt..];
            }
            else
            {
                permalink = link.Trim();
            }

            string? author = item.Elements()
                .FirstOrDefault(e => e.Name.LocalName == "creator")?.Value;
            if (string.IsNullOrWhiteSpace(author)) author = null;

            string description = item.Element("description")?.Value ?? "";
            description = Regex.Replace(description, "(?i)<img[^>]*plugins/feed[^>]*>", " ");
            string excerpt = StripExcerpt(description);

            string name = permalink.StartsWith("/archives/", StringComparison.Ordinal)
                ? permalink["/archives/".Length..]
                : permalink;

            posts.Add(new QlForumPost(name, title.Trim(), author,
                FormatDate(item.Element("pubDate")?.Value ?? ""), excerpt, permalink,
                false, -1, -1, true));
        }
        return posts;
    }

    /// <summary>按 Halo 内容 API 的 name 抓取文章。</summary>
    public static async Task<QlForumArticle> FetchArticleAsync(string name, CancellationToken ct = default)
        => await FetchArticleAsync(BaseUrl, name, ct).ConfigureAwait(false);

    /// <summary>按 Halo 内容 API 的 name 抓取文章（测试可指定 base URL）。</summary>
    public static async Task<QlForumArticle> FetchArticleAsync(string baseUrl, string name,
        CancellationToken ct = default)
    {
        string body = await QlDirectHttp.GetTextAsync(
            Normalize(baseUrl) + ListApiPath + "/" + name, ct).ConfigureAwait(false);
        using var doc = ParseDocument(body, "article");
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("content", out var content) ||
            content.ValueKind != JsonValueKind.Object)
            throw new IOException("论坛文章请求失败：服务器返回错误信封");

        var metadata = Child(root, "metadata");
        var spec = Child(root, "spec");
        var status = Child(root, "status");
        var owner = Child(root, "owner");
        return new QlForumArticle(
            name,
            Str(spec, "title"),
            string.IsNullOrEmpty(Str(owner, "displayName")) ? null : Str(owner, "displayName"),
            string.IsNullOrEmpty(Str(spec, "publishTime")) ? null : Str(spec, "publishTime"),
            Str(content, "content"),
            Str(status, "permalink", "/"));
    }

    /// <summary>从渲染页的 <c>article#content</c> 抓取正文（RSS 帖子或 API 故障时使用）。</summary>
    /// <param name="permalink">相对地址，必须落在 <c>/archives/</c> 下。</param>
    public static async Task<QlForumArticle> FetchArticleFromPageAsync(string permalink,
        CancellationToken ct = default)
        => await FetchArticleFromPageAsync(BaseUrl, permalink, ct).ConfigureAwait(false);

    /// <summary>从指定站点的渲染页抓取正文（测试可指定 base URL）。</summary>
    public static async Task<QlForumArticle> FetchArticleFromPageAsync(string baseUrl,
        string permalink, CancellationToken ct = default)
    {
        if (!permalink.StartsWith("/archives/", StringComparison.Ordinal))
            throw new IOException("论坛文章 permalink 不是 /archives/ 地址：" + permalink);

        string body = await QlDirectHttp.GetTextAsync(Normalize(baseUrl) + permalink, ct)
            .ConfigureAwait(false);
        var article = Regex.Match(body, "(?s)<article\\b[^>]*id=\"content\"[^>]*>(.*?)</article>");
        if (!article.Success || string.IsNullOrWhiteSpace(article.Groups[1].Value))
            throw new IOException("论坛文章页没有内容容器");

        var title = Regex.Match(body, "(?is)<title>(.*?)</title>");
        string pageTitle = title.Success ? WebUtility.HtmlDecode(title.Groups[1].Value) : "";
        int suffixAt = pageTitle.IndexOf(" - ", StringComparison.Ordinal);
        if (suffixAt > 0) pageTitle = pageTitle[..suffixAt];

        return new QlForumArticle(permalink["/archives/".Length..], pageTitle,
            null, null, article.Groups[1].Value, permalink);
    }

    /// <summary>按帖子抓正文：先试内容 API，失败或 RSS 来源时退回渲染页。</summary>
    public static async Task<QlForumArticle> FetchArticleForPostAsync(QlForumPost post,
        CancellationToken ct = default)
        => await FetchArticleForPostAsync(BaseUrl, post, ct).ConfigureAwait(false);

    /// <summary>按帖子抓正文（测试可指定 base URL）。</summary>
    public static async Task<QlForumArticle> FetchArticleForPostAsync(string baseUrl,
        QlForumPost post, CancellationToken ct = default)
    {
        if (!post.FromRss)
        {
            try
            {
                return await FetchArticleAsync(baseUrl, post.Name, ct).ConfigureAwait(false);
            }
            catch (IOException)
            {
                // 内容 API 故障：继续走渲染页兜底。
            }
            catch (HttpRequestException)
            {
                // 同上。
            }
        }
        return await FetchArticleFromPageAsync(baseUrl, post.Permalink, ct).ConfigureAwait(false);
    }

    /// <summary>把 Halo 渲染的 HTML 压成带换行的纯文本（标题/段落/引用/列表保留结构）。</summary>
    public static string HtmlToText(string html)
    {
        if (string.IsNullOrEmpty(html)) return "";
        string text = Regex.Replace(html, "(?is)<br\\s*/?>", "\n");
        text = Regex.Replace(text, "(?is)<li[^>]*>", "• ");
        text = Regex.Replace(text, "(?is)</(p|div|h[1-6]|li|blockquote|tr)>", "\n");
        text = Regex.Replace(text, "(?is)<[^>]*>", "");
        text = WebUtility.HtmlDecode(text);
        text = text.Replace("\r\n", "\n").Replace('\r', '\n');
        text = Regex.Replace(text, "[\\t \\u00A0]+", " ");
        text = Regex.Replace(text, "\\n{3,}", "\n\n");
        return text.Trim();
    }

    // ─────────────────────────── 解析工具 ───────────────────────────

    /// <summary>容许调用方传入带或不带末尾斜杠的 base URL（测试服务器以斜杠结尾）。</summary>
    private static string Normalize(string baseUrl) => baseUrl.TrimEnd('/');

    private static System.Text.Json.JsonDocument ParseDocument(string body, string what)
    {
        try
        {
            return System.Text.Json.JsonDocument.Parse(body);
        }
        catch (System.Text.Json.JsonException e)
        {
            throw new IOException("收到的论坛 " + what + " 不是合法 JSON", e);
        }
    }

    private static System.Text.Json.JsonElement Child(System.Text.Json.JsonElement parent, string name)
        => parent.ValueKind == System.Text.Json.JsonValueKind.Object &&
           parent.TryGetProperty(name, out var child) &&
           child.ValueKind == System.Text.Json.JsonValueKind.Object
            ? child
            : default;

    private static string Str(System.Text.Json.JsonElement obj, string name, string fallback = "")
        => obj.ValueKind == System.Text.Json.JsonValueKind.Object &&
           obj.TryGetProperty(name, out var value) &&
           value.ValueKind == System.Text.Json.JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;

    private static int Int(System.Text.Json.JsonElement obj, string name)
        => obj.ValueKind == System.Text.Json.JsonValueKind.Object &&
           obj.TryGetProperty(name, out var value) && value.TryGetInt32(out int parsed)
            ? parsed
            : 0;

    private static bool Bool(System.Text.Json.JsonElement obj, string name)
        => obj.ValueKind == System.Text.Json.JsonValueKind.Object &&
           obj.TryGetProperty(name, out var value) &&
           value.ValueKind == System.Text.Json.JsonValueKind.True;

    private static string? FormatDate(string pubDate)
    {
        if (DateTimeOffset.TryParse(pubDate, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var time))
            return time.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        return null;
    }

    private static string StripExcerpt(string description)
    {
        string text = HtmlToText(Regex.Replace(description, "(?is)<br\\s*/?>", " ")
            .Replace("</p>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</h1>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</h2>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</h3>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</li>", " ", StringComparison.OrdinalIgnoreCase)
            .Replace("</blockquote>", " ", StringComparison.OrdinalIgnoreCase));
        text = Regex.Replace(text, "\\s+", " ").Trim();
        return text.Length > 300 ? text[..300] : text;
    }
}
