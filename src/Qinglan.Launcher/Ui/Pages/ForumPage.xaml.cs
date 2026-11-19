using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qinglan.Launcher.Core;
using Qinglan.Launcher.Core.Forum;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>
/// 论坛页：左侧帖子列表、右侧站内阅读，对齐 QL-HMCL 的交互。
/// <para>Halo 内容 API 可能整体 500：列表自动切到 RSS 备份，正文改抓渲染页的
/// <c>article#content</c>，并在页面上明确提示；每篇文章都保留浏览器入口。</para>
/// </summary>
public partial class ForumPage : UserControl
{
    private const int PageSize = 20;

    private readonly List<QlForumPost> _posts = new();
    private CancellationTokenSource? _cts;
    private QlForumPost? _selected;
    private bool _rssMode;
    private bool _loading;
    private bool _loaded;
    private int _page = 1;
    private int _articleGeneration;

    public ForumPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = new CancellationTokenSource();

            if (!QlDeployment.HasForum)
            {
                ShowUnconfigured();
                return;
            }

            BtnForumRefresh.IsEnabled = true;
            if (!_loaded) _ = LoadPageAsync(reset: true);
        };
        Unloaded += (_, _) =>
        {
            _loading = false;
            _cts?.Cancel();
            _cts?.Dispose();
            _cts = null;
        };
    }

    // ─────────────────────────── 列表 ───────────────────────────

    private void OnRefresh(object sender, RoutedEventArgs e)
    {
        if (!QlDeployment.HasForum)
        {
            ShowUnconfigured();
            return;
        }
        _ = LoadPageAsync(reset: true);
    }

    private void OnLoadMore(object sender, RoutedEventArgs e)
    {
        if (!QlDeployment.HasForum)
        {
            ShowUnconfigured();
            return;
        }
        _ = LoadPageAsync(reset: false);
    }

    /// <summary>
    /// 读取一页帖子。内容 API 失败且手上一无所有时切换 RSS；RSS 一次给全部条目，
    /// 因此不再显示“加载更多”。
    /// </summary>
    private async Task LoadPageAsync(bool reset)
    {
        if (_loading) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        _loading = true;
        BtnForumRefresh.IsEnabled = false;
        if (reset)
        {
            _rssMode = false;
            ForumNote.Visibility = Visibility.Collapsed;
        }
        if (_posts.Count == 0) ForumListStatus.Text = "加载帖子中…";
        try
        {
            if (!_rssMode)
            {
                try
                {
                    var posts = await QlForum.FetchPostsAsync(reset ? 1 : _page, PageSize, ct);
                    if (reset) _posts.Clear();
                    if (posts.Count == 0)
                    {
                        ForumListStatus.Text = "没有更多帖子了。";
                    }
                    else
                    {
                        _posts.AddRange(posts);
                        _page = (reset ? 1 : _page) + 1;
                        ForumListStatus.Text = "共 " + _posts.Count + " 篇帖子";
                    }
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    return; // 离页取消：不要切 RSS、不要写失败提示。
                }
                catch (Exception ex) when (_posts.Count == 0)
                {
                    // 列表 API 整体不可用：切 RSS 备份，语义与 QL-HMCL 一致。
                    Globals.Log("论坛列表 API 失败，切换到 RSS: " + ex.Message);
                    var rss = await QlForum.FetchPostsFromRssAsync(ct);
                    _posts.Clear();
                    _posts.AddRange(rss);
                    _rssMode = true;
                    ForumNoteText.Text = "接口服务端故障，列表已切换到 RSS 备份"
                                         + "（仅标题与摘要，正文仍可站内阅读）。";
                    ForumNote.Visibility = Visibility.Visible;
                    ForumListStatus.Text = "RSS 备份 · 共 " + _posts.Count + " 篇帖子";
                }
            }
            else
            {
                var rss = await QlForum.FetchPostsFromRssAsync(ct);
                _posts.Clear();
                _posts.AddRange(rss);
                ForumListStatus.Text = "RSS 备份 · 共 " + _posts.Count + " 篇帖子";
            }

            _loaded = true;
            RenderList(selectFirst: reset || _selected is null);
            BtnMore.Visibility = !_rssMode && _posts.Count > 0 && _posts.Count % PageSize == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
            if (_posts.Count == 0) ForumListStatus.Text = "论坛暂时没有可显示的帖子。";
            ForumStatusText.Text = (_rssMode ? "RSS 备份" : "已加载") + " · "
                + DateTime.Now.ToString("HH:mm:ss");
            MainWindow.Instance.SetStatus("论坛：" + _posts.Count + " 篇帖子"
                + (_rssMode ? "（RSS 备份）" : ""));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 离页取消：保留已加载的列表与正文。
        }
        catch (Exception ex)
        {
            ForumListStatus.Text = "论坛加载失败，请检查网络后重试。";
            ForumStatusText.Text = "加载失败";
            MainWindow.Instance.SetStatus("论坛加载失败");
            if (_posts.Count == 0)
            {
                ArticleBody.Children.Clear();
                ArticleBody.Children.Add(Notice("论坛加载失败：" + ex.Message));
            }
            Globals.Log("论坛加载失败: " + ex);
        }
        finally
        {
            _loading = false;
            BtnForumRefresh.IsEnabled = QlDeployment.HasForum;
        }
    }

    /// <summary>论坛根地址为空：统一占位，不发 API/RSS 请求。</summary>
    private void ShowUnconfigured()
    {
        _posts.Clear();
        _loaded = false;
        _selected = null;
        _rssMode = false;
        PostList.Items.Clear();
        BtnForumRefresh.IsEnabled = false;
        BtnMore.Visibility = Visibility.Collapsed;
        BtnOpenPost.IsEnabled = false;
        ForumNote.Visibility = Visibility.Collapsed;
        ForumListStatus.Text = "论坛未配置";
        ForumStatusText.Text = "未配置";
        ArticleTitle.Text = "论坛文章";
        ArticleMeta.Text = "";
        ArticleBody.Children.Clear();
        ArticleBody.Children.Add(ConfigNotice.Build(ConfigNotice.Forum));
        MainWindow.Instance.SetStatus("论坛：未配置");
        Globals.Log("论坛页未配置：deployment.forum.baseUrl 为空");
    }

    private void RenderList(bool selectFirst)
    {
        PostList.Items.Clear();
        foreach (var post in _posts)
            PostList.Items.Add(PostItem(post));

        if (selectFirst && _posts.Count > 0)
        {
            PostList.SelectedIndex = 0;
        }
        else if (_selected is not null)
        {
            foreach (var item in PostList.Items)
            {
                if (item is ListBoxItem row && row.Tag is QlForumPost post &&
                    post.Permalink == _selected.Permalink)
                {
                    PostList.SelectedItem = row;
                    break;
                }
            }
        }
    }

    private ListBoxItem PostItem(QlForumPost post)
    {
        var panel = new StackPanel();
        var titleLine = new WrapPanel();
        if (post.Pinned) titleLine.Children.Add(Pill("置顶", "Accent"));
        titleLine.Children.Add(Text(post.Title, 13.5, FontWeights.SemiBold, wrap: true));
        panel.Children.Add(titleLine);

        var meta = new List<string>();
        if (!string.IsNullOrEmpty(post.Author)) meta.Add(post.Author!);
        if (!string.IsNullOrEmpty(post.PublishTime)) meta.Add(post.PublishTime!);
        if (post.Visits >= 0) meta.Add(post.Visits + " 浏览");
        if (post.Upvotes >= 0) meta.Add(post.Upvotes + " 点赞");
        if (meta.Count > 0)
            panel.Children.Add(Text(string.Join(" · ", meta), 10.5, FontWeights.Normal,
                "Microsoft YaHei UI", "TextSecondary", 2, ellipsis: true));

        string excerpt = post.Excerpt.Replace('\n', ' ').Trim();
        if (excerpt.Length > 90) excerpt = excerpt[..90] + "…";
        if (excerpt.Length > 0)
            panel.Children.Add(Text(excerpt, 11.5, FontWeights.Normal, "Microsoft YaHei UI",
                "TextSecondary", 4, ellipsis: true));

        return new ListBoxItem { Content = panel, Tag = post };
    }

    private void OnPostSelected(object sender, SelectionChangedEventArgs e)
    {
        if (PostList.SelectedItem is not ListBoxItem row || row.Tag is not QlForumPost post)
        {
            _selected = null;
            BtnOpenPost.IsEnabled = false;
            return;
        }
        _selected = post;
        BtnOpenPost.IsEnabled = true;
        ShowPost(post);
    }

    // ─────────────────────────── 正文 ───────────────────────────

    private void ShowPost(QlForumPost post)
    {
        int generation = ++_articleGeneration;
        ArticleTitle.Text = post.Title;
        ArticleMeta.Text = PostMeta(post);
        ArticleBody.Children.Clear();
        ArticleBody.Children.Add(Notice("加载文章中…"));
        _ = LoadArticleAsync(post, generation);
    }

    private async Task LoadArticleAsync(QlForumPost post, int generation)
    {
        var ct = _cts?.Token ?? CancellationToken.None;
        try
        {
            var article = await QlForum.FetchArticleForPostAsync(post, ct);
            if (generation != _articleGeneration) return;

            if (!string.IsNullOrWhiteSpace(article.Title) && post.FromRss)
                ArticleTitle.Text = article.Title;
            if (!string.IsNullOrWhiteSpace(article.Html))
            {
                string text = QlForum.HtmlToText(article.Html);
                ArticleBody.Children.Clear();
                if (text.Length == 0)
                {
                    ArticleBody.Children.Add(Notice("这篇文章没有内容。"));
                    return;
                }
                var body = Text(text, 13, FontWeights.Normal, wrap: true);
                body.LineHeight = 22;
                ArticleBody.Children.Add(body);
            }
            else
            {
                ArticleBody.Children.Clear();
                ArticleBody.Children.Add(Notice("这篇文章没有内容。"));
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // 离页取消：保留“加载文章中…”占位，下次进入重新加载。
        }
        catch (Exception ex)
        {
            if (generation != _articleGeneration) return;
            ArticleBody.Children.Clear();
            ArticleBody.Children.Add(Notice("文章加载失败：" + ex.Message));
            ArticleBody.Children.Add(Notice("可点击右上角“在浏览器打开”查看原页面。"));
            Globals.Log("论坛文章加载失败 " + post.Permalink + ": " + ex);
        }
    }

    private static string PostMeta(QlForumPost post)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(post.Author)) parts.Add(post.Author!);
        if (!string.IsNullOrEmpty(post.PublishTime)) parts.Add(post.PublishTime!);
        if (post.Visits >= 0) parts.Add(post.Visits + " 浏览");
        if (post.Upvotes >= 0) parts.Add(post.Upvotes + " 点赞");
        if (post.FromRss) parts.Add("RSS 备份");
        return string.Join(" · ", parts);
    }

    private void OnOpenPost(object sender, RoutedEventArgs e)
    {
        if (_selected is null) return;
        if (!Globals.OpenUrl(_selected.Url))
            ArticleBody.Children.Add(Notice("无法调起系统浏览器，请手动访问 " + _selected.Url));
    }

    // ─────────────────────────── 视觉工具 ───────────────────────────

    private TextBlock Text(string text, double size, FontWeight weight,
        string family = "Microsoft YaHei UI", string brushKey = "TextPrimary", double top = 0,
        bool ellipsis = false, bool wrap = false)
    {
        var block = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("BaseText"),
            FontSize = size,
            FontWeight = weight,
            FontFamily = new FontFamily(family),
            Foreground = (Brush)FindResource(brushKey),
            Margin = new Thickness(0, top, 0, 0),
        };
        if (ellipsis) block.TextTrimming = TextTrimming.CharacterEllipsis;
        if (wrap) block.TextWrapping = TextWrapping.Wrap;
        return block;
    }

    private TextBlock Notice(string text)
    {
        var notice = Text(text, 13, FontWeights.Normal, "Microsoft YaHei UI", "TextSecondary", 10,
            wrap: true);
        notice.HorizontalAlignment = HorizontalAlignment.Stretch;
        notice.TextAlignment = TextAlignment.Center;
        return notice;
    }

    private Border Pill(string text, string colorKey)
    {
        var pill = new Border
        {
            Background = colorKey switch
            {
                "Accent" => (Brush)FindResource("NavHover"),
                "OkGreen" => Frozen(0xE8, 0xF7, 0xEF),
                "Danger" => Frozen(0xFD, 0xEC, 0xEA),
                _ => Frozen(0xF0, 0xF4, 0xFB),
            },
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 1, 8, 1),
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        pill.Child = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("BaseText"),
            FontSize = 11,
            Foreground = colorKey switch
            {
                "Accent" => (Brush)FindResource("AccentDeep"),
                "OkGreen" => (Brush)FindResource("OkGreen"),
                "Danger" => (Brush)FindResource("Danger"),
                _ => (Brush)FindResource("TextSecondary"),
            },
        };
        return pill;
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
