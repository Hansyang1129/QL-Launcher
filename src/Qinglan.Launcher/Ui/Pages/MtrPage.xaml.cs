using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Qinglan.Launcher.Core.Mtr;

namespace Qinglan.Launcher.Ui.Pages;

/// <summary>
/// MTR 轨交页：车站时刻表（按站台 / 按线路）、线路时刻表与路径规划。
/// <para>数据语义对齐 QL-HMCL：静态网络来自 <c>stations-and-routes</c>，实时到站来自
/// <c>arrivals</c>，线路时刻表由 <c>departures</c> 的相对偏移展开；路径规划优先展示
/// 服务器 <c>directions</c> 结果，再附上本地缓存网络的最多 5 条备选。</para>
/// </summary>
public partial class MtrPage : UserControl
{
    private const int TrainsPerPlatform = 6;
    private const int MaxLocalPaths = 5;

    private QlMtrSnapshot? _snapshot;
    private QlMtrDepartures? _departures;
    private List<StationChoice> _stations = new();
    private List<QlMtrRoute> _routes = new();
    private Dictionary<string, string> _stationNames = new(StringComparer.Ordinal);

    private StationChoice? _station;
    private StationChoice? _from;
    private StationChoice? _to;

    private bool _loading;
    private bool _loaded;
    private bool _suppressSelection;
    private int _stationGeneration;
    private int _planGeneration;

    public MtrPage()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (!_loaded) _ = LoadAsync(false);
        };
    }

    // ─────────────────────────── 数据加载 ───────────────────────────

    /// <summary>抓取网络快照与时刻表包；<paramref name="force"/> 为 true 时重建缓存。</summary>
    private async Task LoadAsync(bool force)
    {
        if (_loading) return;
        if (_loaded && !force) return;
        _loading = true;
        BtnMtrRefresh.IsEnabled = false;
        MtrStatusText.Text = "查询中…";
        try
        {
            var snapshotTask = QlMtr.FetchSnapshotAsync();
            var departuresTask = FetchDeparturesSafeAsync();
            var snapshot = await snapshotTask;
            var departures = await departuresTask;

            _snapshot = snapshot;
            _departures = departures;
            _stations = BuildStationChoices(snapshot);
            _routes = snapshot.Routes
                .Where(route => !route.Hidden && route.Stops.Count >= 2)
                .OrderBy(route => QlMtr.PrimaryName(route.Name), StringComparer.CurrentCulture)
                .ToList();
            _stationNames = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var station in snapshot.Stations)
                _stationNames.TryAdd(station.Id, QlMtr.PrimaryName(station.Name));

            _loaded = true;
            PopulateStationList();
            PopulateRouteList();
            PopulatePlanPickers();
            RestoreSelection();

            MtrStatusText.Text = "已加载 " + _stations.Count + " 座车站 · " + _routes.Count + " 条线路"
                + (_departures is null ? "（时刻表不可用）" : " · " + DateTime.Now.ToString("HH:mm:ss"));
            MainWindow.Instance.SetStatus("MTR 轨交：" + _stations.Count + " 站 / " + _routes.Count + " 线");
        }
        catch (Exception ex)
        {
            MtrStatusText.Text = "MTR 地图 API 查询失败，请检查网络";
            ShowGlobalFailure(ex.Message);
            Globals.Log("MTR 数据加载失败: " + ex);
        }
        finally
        {
            _loading = false;
            BtnMtrRefresh.IsEnabled = true;
        }
    }

    /// <summary>时刻表是可选数据：抓取失败时车站/线路仍可浏览。</summary>
    private static async Task<QlMtrDepartures?> FetchDeparturesSafeAsync()
    {
        try
        {
            return await QlMtr.FetchDeparturesAsync();
        }
        catch (Exception ex)
        {
            Globals.Log("MTR 时刻表抓取失败（继续无时刻表浏览）: " + ex.Message);
            return null;
        }
    }

    private void OnRefresh(object sender, RoutedEventArgs e) => _ = LoadAsync(true);

    private void ShowGlobalFailure(string detail)
    {
        StationDetail.Children.Clear();
        StationDetail.Children.Add(Notice("MTR 地图 API 查询失败：" + detail));
        RouteDetail.Children.Clear();
        RouteDetail.Children.Add(Notice("MTR 地图 API 查询失败：" + detail));
        PlanResults.Children.Clear();
        PlanResults.Children.Add(Notice("MTR 地图 API 查询失败：" + detail));
    }

    /// <summary>刷新后保留已选车站 / 线路 / 起终点，避免用户重新挑选。</summary>
    private void RestoreSelection()
    {
        if (_station is not null)
        {
            SelectInList(StationList, _station);
            if (StationList.SelectedItem is not null) RenderStation();
        }
        if (_routes.Count > 0 && RouteList.SelectedItem is not null) RenderRoute();
        FillPlanCombo(FromCombo, FromFilter.Text, _from);
        FillPlanCombo(ToCombo, ToFilter.Text, _to);
    }

    // ─────────────────────────── 页签切换 ───────────────────────────

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (StationPanel is null) return; // InitializeComponent 期间的过早事件
        StationPanel.Visibility = TabStation.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        RoutePanel.Visibility = TabRoute.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        PlanPanel.Visibility = TabPlan.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    // ─────────────────────────── 车站时刻表 ───────────────────────────

    private void OnStationSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (StationList is null) return;
        PopulateStationList();
    }

    private void PopulateStationList()
    {
        if (_suppressSelection) return;
        _suppressSelection = true;
        try
        {
            string query = (StationSearchBox.Text ?? "").Trim();
            StationList.Items.Clear();
            foreach (var choice in _stations)
            {
                if (query.Length > 0 &&
                    !choice.Search.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                StationList.Items.Add(StationItem(choice));
            }
            if (_station is not null) SelectInList(StationList, _station);
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private static void SelectInList(ListBox list, StationChoice choice)
    {
        foreach (var item in list.Items)
        {
            if (item is ListBoxItem row && row.Tag is StationChoice candidate &&
                candidate.Primary == choice.Primary)
            {
                list.SelectedItem = row;
                return;
            }
        }
    }

    private ListBoxItem StationItem(StationChoice choice)
    {
        var panel = new StackPanel();
        panel.Children.Add(Text(choice.Display, 13.5, FontWeights.SemiBold));
        if (!string.IsNullOrEmpty(choice.Subtitle))
            panel.Children.Add(Text(choice.Subtitle, 11, FontWeights.Normal, "Microsoft YaHei UI",
                "TextSecondary", 1, ellipsis: true));
        return new ListBoxItem { Content = panel, Tag = choice };
    }

    private void OnStationSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (StationList.SelectedItem is not ListBoxItem row || row.Tag is not StationChoice choice)
        {
            _station = null;
            return;
        }
        _station = choice;
        StationModePanel.Visibility = Visibility.Visible;
        StationTitle.Text = choice.Display;
        StationSubtitle.Text = choice.Subtitle;
        RenderStation();
    }

    private void OnStationModeChanged(object sender, RoutedEventArgs e)
    {
        if (StationDetail is null || _station is null) return;
        RenderStation();
    }

    private void RenderStation()
    {
        var choice = _station;
        if (choice is null || _snapshot is null) return;
        if (ModeRoute.IsChecked == true)
        {
            RenderStationRoutes(choice);
            return;
        }

        int generation = ++_stationGeneration;
        StationDetail.Children.Clear();
        StationDetail.Children.Add(Notice("查询实时到站…"));
        _ = LoadArrivalsAsync(choice, generation);
    }

    /// <summary>按站台视图：一次请求该站全部站台的实时到站，再按站台分组。</summary>
    private async Task LoadArrivalsAsync(StationChoice choice, int generation)
    {
        try
        {
            var rows = await QlMtr.FetchArrivalsAsync(choice.Ids, TrainsPerPlatform);
            if (generation != _stationGeneration || !ReferenceEquals(choice, _station)) return;

            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            var trains = new Dictionary<string, List<QlMtrArrival>>(StringComparer.Ordinal);
            foreach (var arrival in rows)
            {
                if (arrival.Arrival < now - 60_000) continue;
                labels.TryAdd(arrival.PlatformId, string.IsNullOrEmpty(arrival.PlatformName)
                    ? "1" : arrival.PlatformName);
                if (!trains.TryGetValue(arrival.PlatformId, out var list))
                    trains[arrival.PlatformId] = list = new List<QlMtrArrival>();
                list.Add(arrival);
            }

            if (trains.Count == 0)
            {
                StationDetail.Children.Clear();
                StationDetail.Children.Add(Notice("暂无即将到站的车次。"));
                return;
            }

            var ordered = trains.ToList();
            ordered.Sort((a, b) => PlatformOrder(labels[a.Key]).CompareTo(PlatformOrder(labels[b.Key])));
            StationDetail.Children.Clear();
            foreach (var entry in ordered)
            {
                entry.Value.Sort((a, b) => a.Arrival.CompareTo(b.Arrival));
                var inner = new StackPanel();
                inner.Children.Add(Text("站台 " + labels[entry.Key], 13.5, FontWeights.Bold,
                    top: 0));
                foreach (var arrival in entry.Value)
                    inner.Children.Add(ArrivalRow(arrival, now));
                StationDetail.Children.Add(Card(inner));
            }
        }
        catch (Exception ex)
        {
            if (generation != _stationGeneration) return;
            StationDetail.Children.Clear();
            StationDetail.Children.Add(Notice("实时到站查询失败：" + ex.Message));
            Globals.Log("MTR 实时到站失败: " + ex);
        }
    }

    private static int PlatformOrder(string label)
    {
        var digits = new System.Text.StringBuilder();
        foreach (char c in label)
            if (char.IsDigit(c)) digits.Append(c);
        return digits.Length > 0 && int.TryParse(digits.ToString(), out int value)
            ? value
            : int.MaxValue;
    }

    private Border ArrivalRow(QlMtrArrival arrival, long now)
    {
        var row = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        if (arrival.RouteColor != 0) line.Children.Add(Swatch(arrival.RouteColor));
        string routeName = QlMtr.PrimaryName(arrival.RouteName);
        line.Children.Add(Text(string.IsNullOrEmpty(arrival.RouteNumber)
            ? routeName
            : arrival.RouteNumber + "  " + routeName, 13.5, FontWeights.SemiBold));
        row.Children.Add(line);
        row.Children.Add(Text("开往 " + QlMtr.PrimaryName(arrival.Destination), 12,
            FontWeights.Normal, "Microsoft YaHei UI", "TextSecondary", 2));

        var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        tags.Children.Add(Pill(MinutesText(arrival.Arrival, now) + "后到站", "Accent"));
        if (arrival.Realtime) tags.Children.Add(Pill("实时", "OkGreen"));
        if (arrival.Terminating) tags.Children.Add(Pill("终点站", "Danger"));
        tags.Children.Add(Pill("站台 " + arrival.PlatformName, null));
        row.Children.Add(tags);
        return Block(row);
    }

    /// <summary>按线路视图：找出途经该站的全部线路，列出站台与下一班。</summary>
    private void RenderStationRoutes(StationChoice choice)
    {
        StationDetail.Children.Clear();
        if (_snapshot is null)
        {
            StationDetail.Children.Add(Notice("MTR 地图 API 查询失败，请检查网络。"));
            return;
        }

        var serving = _routes
            .Where(route => route.Stops.Any(stop => choice.IdSet.Contains(stop.StationId)))
            .ToList();
        if (serving.Count == 0)
        {
            StationDetail.Children.Add(Notice("没有线路途经该车站。"));
            return;
        }

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        foreach (var route in serving)
        {
            var platforms = route.Stops
                .Where(stop => choice.IdSet.Contains(stop.StationId))
                .Select(stop => stop.PlatformName)
                .Where(name => !string.IsNullOrEmpty(name))
                .Distinct()
                .ToList();
            string next = NextDepartureText(route, choice.IdSet, now);
            var inner = new StackPanel();
            var line = new StackPanel { Orientation = Orientation.Horizontal };
            if (route.Color != 0) line.Children.Add(Swatch(route.Color));
            line.Children.Add(Text(QlMtr.PrimaryName(route.Name), 13.5, FontWeights.SemiBold));
            inner.Children.Add(line);
            string direction = QlMtr.RouteDirection(route.Name);
            if (!string.IsNullOrEmpty(direction))
                inner.Children.Add(Text(direction, 11.5, FontWeights.Normal,
                    "Microsoft YaHei UI", "TextSecondary", 1));
            var tags = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
            foreach (string platform in platforms)
                tags.Children.Add(Pill("站台 " + platform, null));
            tags.Children.Add(Pill(next, "Accent"));
            inner.Children.Add(tags);
            StationDetail.Children.Add(Card(inner));
        }
    }

    /// <summary>某线路在给定站台集合上的最早一班；没有时刻表时给出说明。</summary>
    private string NextDepartureText(QlMtrRoute route, IReadOnlySet<string> stationIds, long now)
    {
        if (_departures is null) return "暂无时刻表";
        long best = long.MaxValue;
        for (int i = 0; i < route.Stops.Count; i++)
        {
            if (!stationIds.Contains(route.Stops[i].StationId)) continue;
            foreach (var departure in QlMtr.NextAt(_departures, route, i, now, 1))
            {
                if (departure.ArrivalAt < best) best = departure.ArrivalAt;
            }
        }
        return best == long.MaxValue ? "暂无时刻表" : MinutesText(best, now) + "后到站";
    }

    // ─────────────────────────── 线路时刻表 ───────────────────────────

    private void OnRouteSearchChanged(object sender, TextChangedEventArgs e)
    {
        if (RouteList is null) return;
        PopulateRouteList();
    }

    private void PopulateRouteList()
    {
        if (_suppressSelection) return;
        _suppressSelection = true;
        try
        {
            string query = (RouteSearchBox.Text ?? "").Trim();
            var selected = (RouteList.SelectedItem as ListBoxItem)?.Tag as QlMtrRoute;
            RouteList.Items.Clear();
            foreach (var route in _routes)
            {
                string primary = QlMtr.PrimaryName(route.Name);
                string secondary = QlMtr.SecondaryName(route.Name) ?? "";
                string direction = QlMtr.RouteDirection(route.Name);
                string search = primary + " " + secondary + " " + direction + " " + route.Number;
                if (query.Length > 0 && !search.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                RouteList.Items.Add(RouteItem(route, secondary, direction));
            }
            if (selected is not null)
            {
                foreach (var item in RouteList.Items)
                {
                    if (item is ListBoxItem row && row.Tag is QlMtrRoute candidate &&
                        candidate.Id == selected.Id)
                    {
                        RouteList.SelectedItem = row;
                        break;
                    }
                }
            }
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private ListBoxItem RouteItem(QlMtrRoute route, string secondary, string direction)
    {
        var panel = new StackPanel();
        var line = new StackPanel { Orientation = Orientation.Horizontal };
        if (route.Color != 0) line.Children.Add(Swatch(route.Color));
        line.Children.Add(Text(QlMtr.PrimaryName(route.Name), 13.5, FontWeights.SemiBold));
        if (!string.IsNullOrEmpty(route.Number))
        {
            line.Children.Add(Text("  " + route.Number, 12, FontWeights.Normal,
                "Consolas", "TextSecondary"));
        }
        panel.Children.Add(line);
        string subtitle = direction.Length > 0
            ? (secondary.Length > 0 ? secondary + " · " + direction : direction)
            : secondary;
        if (!string.IsNullOrEmpty(subtitle))
            panel.Children.Add(Text(subtitle, 11, FontWeights.Normal, "Microsoft YaHei UI",
                "TextSecondary", 1, ellipsis: true));
        return new ListBoxItem { Content = panel, Tag = route };
    }

    private void OnRouteSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (RouteList.SelectedItem is ListBoxItem row && row.Tag is QlMtrRoute route)
        {
            RouteTitle.Text = QlMtr.PrimaryName(route.Name)
                + (string.IsNullOrEmpty(route.Number) ? "" : "  " + route.Number);
            string direction = QlMtr.RouteDirection(route.Name);
            RouteSubtitle.Text = (route.Stops.Count + " 站"
                + (string.IsNullOrEmpty(direction) ? "" : " · " + direction)
                + " · 按站序逐站展示最近一班与停靠站台");
            RenderRoute(route);
        }
        else
        {
            RouteTitle.Text = "请选择线路";
            RouteSubtitle.Text = "按站序逐站展示最近一班与停靠站台";
            RouteDetail.Children.Clear();
            RouteDetail.Children.Add(Notice("请选择左侧线路。"));
        }
    }

    private void RenderRoute()
    {
        if (RouteList.SelectedItem is ListBoxItem row && row.Tag is QlMtrRoute route)
            RenderRoute(route);
    }

    private void RenderRoute(QlMtrRoute route)
    {
        RouteDetail.Children.Clear();
        if (_snapshot is null)
        {
            RouteDetail.Children.Add(Notice("MTR 地图 API 查询失败，请检查网络。"));
            return;
        }
        if (_departures is null)
            RouteDetail.Children.Add(Notice("该线路暂无时刻表（服务器时刻表接口不可用）。"));

        long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (int i = 0; i < route.Stops.Count; i++)
        {
            var stop = route.Stops[i];
            string name = _stationNames.TryGetValue(stop.StationId, out string? stationName)
                ? stationName
                : stop.StationId;
            string next = "—";
            if (_departures is not null)
            {
                var departures = QlMtr.NextAt(_departures, route, i, now, 1);
                if (departures.Count > 0)
                    next = MinutesText(departures[0].ArrivalAt, now) + "后到站";
            }

            var row = new Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var index = Text((i + 1).ToString("00"), 11.5, FontWeights.Normal, "Consolas",
                "TextSecondary", 1);
            index.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(index);

            var middle = new StackPanel();
            middle.Children.Add(Text(name, 13.5, FontWeights.SemiBold));
            var tags = new WrapPanel { Margin = new Thickness(0, 3, 0, 0) };
            tags.Children.Add(Pill("站台 " + stop.PlatformName, null));
            if (route.CircularState is not ("NONE" or ""))
                tags.Children.Add(Pill("环线", null));
            middle.Children.Add(tags);
            Grid.SetColumn(middle, 1);
            row.Children.Add(middle);

            var nextText = Text(next, 12, FontWeights.Normal, "Microsoft YaHei UI", "TextSecondary",
                0, HorizontalAlignment.Right);
            nextText.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetColumn(nextText, 2);
            row.Children.Add(nextText);

            RouteDetail.Children.Add(Block(row));
        }
    }

    // ─────────────────────────── 路径规划 ───────────────────────────

    private void PopulatePlanPickers()
    {
        FillPlanCombo(FromCombo, FromFilter.Text, _from);
        FillPlanCombo(ToCombo, ToFilter.Text, _to);
    }

    private void OnFromFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (FromCombo is null) return;
        FillPlanCombo(FromCombo, FromFilter.Text, _from);
    }

    private void OnToFilterChanged(object sender, TextChangedEventArgs e)
    {
        if (ToCombo is null) return;
        FillPlanCombo(ToCombo, ToFilter.Text, _to);
    }

    /// <summary>筛选后重建下拉项并尽量保留已选车站。</summary>
    private void FillPlanCombo(ComboBox combo, string query, StationChoice? keep)
    {
        _suppressSelection = true;
        try
        {
            query = (query ?? "").Trim();
            combo.Items.Clear();
            ComboBoxItem? restored = null;
            foreach (var choice in _stations)
            {
                if (query.Length > 0 &&
                    !choice.Search.Contains(query, StringComparison.OrdinalIgnoreCase))
                    continue;
                var item = new ComboBoxItem { Content = choice.Display, Tag = choice };
                combo.Items.Add(item);
                if (keep is not null && keep.Primary == choice.Primary) restored = item;
            }
            combo.SelectedItem = restored;
        }
        finally
        {
            _suppressSelection = false;
        }
    }

    private void OnPlanSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_suppressSelection) return;
        if (ReferenceEquals(sender, FromCombo))
            _from = (FromCombo.SelectedItem as ComboBoxItem)?.Tag as StationChoice;
        else if (ReferenceEquals(sender, ToCombo))
            _to = (ToCombo.SelectedItem as ComboBoxItem)?.Tag as StationChoice;

        if (_from is not null && _to is not null && PlanResults.Children.Count == 1 &&
            PlanResults.Children[0] is TextBlock)
            PlanResults.Children.Clear();
    }

    private void OnSwap(object sender, RoutedEventArgs e)
    {
        (_from, _to) = (_to, _from);
        FillPlanCombo(FromCombo, FromFilter.Text, _from);
        FillPlanCombo(ToCombo, ToFilter.Text, _to);
    }

    private void OnPlanQuery(object sender, RoutedEventArgs e) => _ = RunPlanQueryAsync();

    private async Task RunPlanQueryAsync()
    {
        var snapshot = _snapshot;
        var from = _from;
        var to = _to;
        if (snapshot is null)
        {
            PlanResults.Children.Clear();
            PlanResults.Children.Add(Notice("MTR 地图 API 查询失败，请检查网络。"));
            return;
        }
        if (from is null || to is null)
        {
            PlanResults.Children.Clear();
            PlanResults.Children.Add(Notice("请先选择出发与到达车站。"));
            return;
        }

        int generation = ++_planGeneration;
        BtnPlan.IsEnabled = false;
        PlanResults.Children.Clear();
        PlanResults.Children.Add(Notice("正在规划路径…"));
        try
        {
            QlMtrDirections? web = null;
            if (WebQueryCheck.IsChecked == true)
            {
                try
                {
                    web = await QlMtr.FetchDirectionsAsync(from.ApiName, to.ApiName);
                }
                catch (Exception ex)
                {
                    // 网页查询是可选数据：本地缓存网络仍能给出方案。
                    Globals.Log("MTR API 路径查询失败（继续本地规划）: " + ex.Message);
                }
            }
            if (generation != _planGeneration) return;

            var local = await Task.Run(() => FindLocalPaths(snapshot, from, to));
            if (generation != _planGeneration) return;

            var cards = new List<UIElement>();
            int index = 1;
            if (web is not null)
            {
                var webPath = QlMtrRouter.DirectionsToPath(snapshot, web,
                    QlMtr.PrimaryName(from.ApiName), QlMtr.PrimaryName(to.ApiName));
                if (webPath is not null) cards.Add(PlanCard(index++, webPath, true));
            }
            foreach (var path in local)
                cards.Add(PlanCard(index++, path, false));

            PlanResults.Children.Clear();
            if (cards.Count == 0)
            {
                PlanResults.Children.Add(Notice("未找到可行路径。"));
                return;
            }
            foreach (var card in cards) PlanResults.Children.Add(card);
        }
        catch (Exception ex)
        {
            PlanResults.Children.Clear();
            PlanResults.Children.Add(Notice("路径规划失败：" + ex.Message));
            Globals.Log("MTR 路径规划失败: " + ex);
        }
        finally
        {
            if (generation == _planGeneration) BtnPlan.IsEnabled = true;
        }
    }

    /// <summary>对两座车站的站台组合分别跑 Dijkstra，按总耗时取前 5 条不同路径。</summary>
    private static List<QlMtrPath> FindLocalPaths(QlMtrSnapshot snapshot, StationChoice from,
        StationChoice to)
    {
        var paths = new List<QlMtrPath>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        foreach (string fromId in from.Ids)
        {
            foreach (string toId in to.Ids)
            {
                foreach (var path in QlMtrRouter.FindPaths(snapshot, fromId, toId, MaxLocalPaths))
                {
                    if (signatures.Add(path.Signature)) paths.Add(path);
                }
            }
        }
        paths.Sort((a, b) => a.Total.CompareTo(b.Total));
        if (paths.Count > MaxLocalPaths)
            paths.RemoveRange(MaxLocalPaths, paths.Count - MaxLocalPaths);
        return paths;
    }

    private Border PlanCard(int index, QlMtrPath path, bool web)
    {
        var inner = new StackPanel();
        var header = new WrapPanel();
        header.Children.Add(Pill("方案 #" + index, web ? "OkGreen" : null));
        header.Children.Add(Pill(web ? "网页查询" : "本地规划", web ? "Accent" : null));
        inner.Children.Add(header);
        inner.Children.Add(Text("总计 " + Math.Round(path.Total / 60000.0) + " 分钟 · 换乘 "
            + path.Transfers + " 次", 15, FontWeights.Bold, top: 8));

        foreach (var leg in path.Legs)
        {
            var row = new Grid { Margin = new Thickness(0, 6, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(4) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            var stripe = new Border
            {
                Width = 4,
                CornerRadius = new CornerRadius(2),
                Background = leg.Walk ? Brushes.Transparent : SwatchBrush(leg.RouteColor),
                Margin = new Thickness(0, 1, 10, 1),
            };
            row.Children.Add(stripe);

            var body = new StackPanel();
            if (leg.Walk)
            {
                body.Children.Add(Text("换乘步行", 13, FontWeights.SemiBold));
            }
            else
            {
                string title = (leg.RouteName ?? "") +
                               (string.IsNullOrEmpty(leg.RouteNumber) ? "" : "  " + leg.RouteNumber);
                body.Children.Add(Text(title, 13, FontWeights.SemiBold));
            }
            body.Children.Add(Text(leg.FromStationName + " → " + leg.ToStationName, 12,
                FontWeights.Normal, "Microsoft YaHei UI", "TextSecondary", 1));
            body.Children.Add(Text(leg.Walk
                ? "步行约 " + Math.Max(1, Math.Round(leg.Time / 60000.0)) + " 分钟"
                : Math.Round(leg.Time / 60000.0) + " 分钟 · " + leg.Stops + " 站", 11.5,
                FontWeights.Normal, "Microsoft YaHei UI", "TextSecondary", 1));
            Grid.SetColumn(body, 1);
            row.Children.Add(body);
            inner.Children.Add(row);
        }

        return Card(inner, padding: 14);
    }

    // ─────────────────────────── 站点分组 ───────────────────────────

    /// <summary>
    /// 把 MTR 的站台级车站合并成用户理解的“一座车站”：先沿 connections 求并查集，
    /// 再按中文主名兜底合并（对齐 QL-HMCL 的 <c>stationGroupIds</c>）。
    /// </summary>
    private static List<StationChoice> BuildStationChoices(QlMtrSnapshot snapshot)
    {
        var index = new Dictionary<string, int>(StringComparer.Ordinal);
        var list = new List<QlMtrStation>();
        foreach (var station in snapshot.Stations)
        {
            if (index.ContainsKey(station.Id)) continue;
            index[station.Id] = list.Count;
            list.Add(station);
        }
        if (list.Count == 0) return new List<StationChoice>();

        var parent = new int[list.Count];
        for (int i = 0; i < parent.Length; i++) parent[i] = i;

        int Find(int value)
        {
            while (parent[value] != value)
            {
                parent[value] = parent[parent[value]];
                value = parent[value];
            }
            return value;
        }

        void Union(int a, int b)
        {
            int rootA = Find(a);
            int rootB = Find(b);
            if (rootA != rootB) parent[rootB] = rootA;
        }

        foreach (var station in list)
        {
            if (!index.TryGetValue(station.Id, out int self)) continue;
            foreach (string connection in station.Connections)
                if (index.TryGetValue(connection, out int other)) Union(self, other);
        }

        var byName = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var station in list)
        {
            string name = QlMtr.PrimaryName(station.Name);
            int self = index[station.Id];
            if (byName.TryGetValue(name, out int other)) Union(self, other);
            else byName[name] = self;
        }

        var groups = new Dictionary<int, List<QlMtrStation>>();
        foreach (var station in list)
        {
            int root = Find(index[station.Id]);
            if (!groups.TryGetValue(root, out var members)) groups[root] = members = new();
            members.Add(station);
        }

        var choices = new List<StationChoice>();
        foreach (var members in groups.Values)
        {
            var first = members[0];
            string primary = QlMtr.PrimaryName(first.Name);
            string secondary = QlMtr.SecondaryName(first.Name) ?? "";
            var names = new List<string>();
            foreach (var member in members)
            {
                string piece = QlMtr.PrimaryName(member.Name);
                string english = QlMtr.SecondaryName(member.Name) ?? "";
                names.Add(english.Length > 0 ? piece + " " + english : piece);
            }
            string subtitle = members.Count > 1
                ? (secondary.Length > 0 ? secondary + " · " : "") + members.Count + " 个站台"
                : secondary;
            var ids = members.Select(member => member.Id).Distinct(StringComparer.Ordinal).ToArray();
            choices.Add(new StationChoice
            {
                Primary = primary,
                Secondary = secondary,
                Subtitle = subtitle,
                ApiName = first.Name,
                Ids = ids,
                IdSet = new HashSet<string>(ids, StringComparer.Ordinal),
                Search = string.Join(" ", names.Distinct(StringComparer.Ordinal)),
            });
        }
        choices.Sort((a, b) => string.Compare(a.Primary, b.Primary, StringComparison.CurrentCulture));
        return choices;
    }

    /// <summary>一座“用户车站”：中文主名 + 英文名 + 站台 id 集合。</summary>
    private sealed class StationChoice
    {
        public string Primary { get; init; } = "";
        public string Secondary { get; init; } = "";
        public string Subtitle { get; init; } = "";
        public string ApiName { get; init; } = "";
        public IReadOnlyList<string> Ids { get; init; } = Array.Empty<string>();
        public IReadOnlySet<string> IdSet { get; init; } = new HashSet<string>();
        public string Search { get; init; } = "";
        public string Display => Secondary.Length == 0 ? Primary : Primary + " · " + Secondary;
    }

    // ─────────────────────────── 视觉工具 ───────────────────────────

    private static string MinutesText(long epochMs, long now)
    {
        long minutes = (long)Math.Round((epochMs - now) / 60000.0);
        return Math.Max(0, minutes) + " 分钟";
    }

    private Border Card(UIElement child, double padding = 12, double marginBottom = 8)
    {
        var card = new Border
        {
            Style = (Style)FindResource("Card"),
            Padding = new Thickness(padding),
            Margin = new Thickness(0, 0, 0, marginBottom),
        };
        card.Child = child;
        return card;
    }

    /// <summary>列表/结果里的浅色块（比 Card 更轻，用于卡片内部条目）。</summary>
    private Border Block(UIElement child) => new()
    {
        Background = (Brush)FindResource("NavHover"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10, 8, 10, 8),
        Margin = new Thickness(0, 0, 0, 6),
        Child = child,
    };

    private TextBlock Text(string text, double size, FontWeight weight,
        string family = "Microsoft YaHei UI", string brushKey = "TextPrimary", double top = 0,
        HorizontalAlignment align = HorizontalAlignment.Left, bool ellipsis = false,
        bool wrap = false)
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
            HorizontalAlignment = align,
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

    private Border Pill(string text, string? colorKey)
    {
        Brush background = colorKey switch
        {
            "Accent" => (Brush)FindResource("NavHover"),
            "OkGreen" => Frozen(0xE8, 0xF7, 0xEF),
            "Danger" => Frozen(0xFD, 0xEC, 0xEA),
            _ => Frozen(0xF0, 0xF4, 0xFB),
        };
        Brush foreground = colorKey switch
        {
            "OkGreen" => (Brush)FindResource("OkGreen"),
            "Danger" => (Brush)FindResource("Danger"),
            "Accent" => (Brush)FindResource("AccentDeep"),
            _ => (Brush)FindResource("TextSecondary"),
        };
        var pill = new Border
        {
            Background = background,
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(0, 0, 6, 4),
        };
        pill.Child = new TextBlock
        {
            Text = text,
            Style = (Style)FindResource("BaseText"),
            FontSize = 11.5,
            Foreground = foreground,
        };
        return pill;
    }

    private Border Swatch(int color) => new()
    {
        Width = 10,
        Height = 10,
        CornerRadius = new CornerRadius(3),
        Background = SwatchBrush(color),
        Margin = new Thickness(0, 0, 6, 0),
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>近黑色线路色向白色提升，避免在浅色卡片上看不见（对齐 QL-HMCL）。</summary>
    private static Brush SwatchBrush(int color)
    {
        int red = (color >> 16) & 0xFF;
        int green = (color >> 8) & 0xFF;
        int blue = color & 0xFF;
        double luminance = (0.2126 * red + 0.7152 * green + 0.0722 * blue) / 255.0;
        if (luminance < 0.2)
        {
            double mix = Math.Min(0.8, (0.2 - luminance) / 0.2);
            red = (int)Math.Round(red + mix * (255 - red));
            green = (int)Math.Round(green + mix * (255 - green));
            blue = (int)Math.Round(blue + mix * (255 - blue));
        }
        return Frozen((byte)red, (byte)green, (byte)blue);
    }

    private static Brush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }
}
