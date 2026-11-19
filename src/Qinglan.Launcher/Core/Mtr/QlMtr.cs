using System.Globalization;
using System.Text.Json;
using Qinglan.Launcher.Core.Net;

namespace Qinglan.Launcher.Core.Mtr;

/// <summary>轻蓝 MTR 轨交网络的一名车站（MTR 模组把每个站台建模成独立车站）。</summary>
/// <param name="Id">16 位大写十六进制站台 id。</param>
/// <param name="Name">双语名，形如 <c>中文|English</c>。</param>
/// <param name="Color">sRGB 显示色。</param>
/// <param name="Connections">同一物理车站内的其他站台 id（换乘步行边）。</param>
public sealed record QlMtrStation(string Id, string Name, int Color, IReadOnlyList<string> Connections);

/// <summary>线路上的一个停靠点。</summary>
/// <param name="StationId">站台车站 id。</param>
/// <param name="PlatformName">站台标签，例如 <c>2</c>。</param>
/// <param name="DwellTime">停站时间（毫秒）。</param>
public sealed record QlMtrRouteStop(string StationId, string PlatformName, long DwellTime);

/// <summary>一条线路。</summary>
/// <param name="Id">16 位大写十六进制线路 id。</param>
/// <param name="Name">双语名；<c>||方向</c> 后缀描述方向。</param>
/// <param name="Color">sRGB 显示色。</param>
/// <param name="Number">短编号，可为空。</param>
/// <param name="Hidden">是否从公开线路图隐藏。</param>
/// <param name="CircularState">非 <c>NONE</c> 表示环线。</param>
/// <param name="Stops">按行车顺序排列的停靠点。</param>
/// <param name="Durations">第 i 段到第 i+1 段的运行毫秒数，通常比停靠点少一项。</param>
public sealed record QlMtrRoute(string Id, string Name, int Color, string Number, bool Hidden,
    string CircularState, IReadOnlyList<QlMtrRouteStop> Stops, IReadOnlyList<long> Durations);

/// <summary>一次 <c>stations-and-routes</c> 抓取得到的全网快照。</summary>
/// <param name="Time">服务器当前时间（epoch 毫秒）。</param>
/// <param name="Stations">全部车站。</param>
/// <param name="Routes">全部线路（含隐藏线路）。</param>
public sealed record QlMtrSnapshot(long Time, IReadOnlyList<QlMtrStation> Stations,
    IReadOnlyList<QlMtrRoute> Routes);

/// <summary>一个发车组：同一偏差值下的一串发车偏移（相对抓取时间）。</summary>
/// <param name="Deviation">偏差毫秒数。</param>
/// <param name="Offsets">升序发车偏移毫秒数，可为负。</param>
public sealed record QlMtrDepartureGroup(long Deviation, IReadOnlyList<long> Offsets);

/// <summary>一次 <c>departures</c> 抓取得到的时刻表包。</summary>
/// <param name="Time">抓取时的服务器时间（epoch 毫秒），所有偏移的原点。</param>
/// <param name="ByRoute">线路 id → 发车组；没有时刻表的线路不出现。</param>
public sealed record QlMtrDepartures(long Time,
    IReadOnlyDictionary<string, IReadOnlyList<QlMtrDepartureGroup>> ByRoute);

/// <summary>一条实时到站记录。</summary>
public sealed record QlMtrArrival(string Destination, long Arrival, long Departure, long Deviation,
    bool Realtime, int DepartureIndex, bool Terminating, string RouteId, string RouteName,
    string RouteNumber, int RouteColor, string PlatformId, string PlatformName,
    IReadOnlyList<string> Cars);

/// <summary>服务器路径查询返回的一段行程。</summary>
public sealed record QlMtrDirectionsLeg(bool Ride, string RouteId, string StartStationId,
    string EndStationId, string StartPlatformName, string EndPlatformName,
    long Departure, long Arrival, long WalkingDistance);

/// <summary>服务器路径查询返回的一条完整路径。</summary>
public sealed record QlMtrDirections(long Time, IReadOnlyList<QlMtrDirectionsLeg> Legs);

/// <summary>展开后的一个具体发车时刻。</summary>
/// <param name="DepartureAt">从首站发车的 epoch 毫秒。</param>
/// <param name="ArrivalAt">到达查询站点的 epoch 毫秒。</param>
/// <param name="Deviation">所属发车组的偏差毫秒。</param>
public sealed record QlMtrTimedDeparture(long DepartureAt, long ArrivalAt, long Deviation);

/// <summary>
/// 轻蓝 MTR 地图 API 客户端：静态网络、时刻表、实时到站与服务器路径查询。
/// 语义对齐 QL-HMCL 的 <c>QLMtr</c>（普通 HTTP、无鉴权、失败重试、容错缺字段）。
/// </summary>
public static class QlMtr
{
    // 根地址来自部署配置 deployment.mtr.baseUrl（空值由 QlDeployment 统一判定）。

    private const string ApiPath = "/mtr/api/map/";
    private const string Dimension = "minecraft:overworld";

    /// <summary>抓取全网快照。</summary>
    public static async Task<QlMtrSnapshot> FetchSnapshotAsync(CancellationToken ct = default)
        => await FetchSnapshotAsync(QlDeployment.RequireMtrBaseUrl(), ct).ConfigureAwait(false);

    /// <summary>抓取全网快照（测试可指定 base URL）。</summary>
    public static async Task<QlMtrSnapshot> FetchSnapshotAsync(string baseUrl, CancellationToken ct = default)
    {
        string body = await QlDirectHttp.GetTextAsync(Normalize(baseUrl) + Endpoint("stations-and-routes"), ct)
            .ConfigureAwait(false);
        using var doc = ParseDocument(body, "network snapshot");
        var root = doc.RootElement;
        var data = DataOf(root, "network snapshot");

        var stations = new List<QlMtrStation>();
        foreach (var item in ArrayOrEmpty(data, "stations"))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? id = TrimToHex(Str(item, "id"));
            if (id is null) continue;
            var connections = new List<string>();
            foreach (var connection in ArrayOrEmpty(item, "connections"))
            {
                string? link = connection.ValueKind == JsonValueKind.String
                    ? TrimToHex(connection.GetString())
                    : null;
                if (link is not null) connections.Add(link);
            }
            stations.Add(new QlMtrStation(id, Str(item, "name"), Int(item, "color"),
                connections.ToArray()));
        }

        var routes = new List<QlMtrRoute>();
        foreach (var item in ArrayOrEmpty(data, "routes"))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? id = TrimToHex(Str(item, "id"));
            if (id is null) continue;

            var stops = new List<QlMtrRouteStop>();
            foreach (var stopElement in ArrayOrEmpty(item, "stations"))
            {
                if (stopElement.ValueKind != JsonValueKind.Object) continue;
                string? stopId = TrimToHex(Str(stopElement, "id"));
                if (stopId is null) continue;
                stops.Add(new QlMtrRouteStop(stopId, Str(stopElement, "name"),
                    Long(stopElement, "dwellTime")));
            }

            var durations = new List<long>();
            foreach (var duration in ArrayOrEmpty(item, "durations"))
                if (duration.ValueKind == JsonValueKind.Number && duration.TryGetInt64(out long value))
                    durations.Add(value);

            routes.Add(new QlMtrRoute(id, Str(item, "name"), Int(item, "color"),
                Str(item, "number"), Bool(item, "hidden"), Str(item, "circularState", "NONE"),
                stops.ToArray(), durations.ToArray()));
        }

        return new QlMtrSnapshot(Long(root, "currentTime", DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()),
            stations.ToArray(), routes.ToArray());
    }

    /// <summary>抓取所有线路的时刻表包（约 0.7 MB）。</summary>
    public static async Task<QlMtrDepartures> FetchDeparturesAsync(CancellationToken ct = default)
        => await FetchDeparturesAsync(QlDeployment.RequireMtrBaseUrl(), ct).ConfigureAwait(false);

    /// <summary>抓取时刻表包（测试可指定 base URL）。</summary>
    public static async Task<QlMtrDepartures> FetchDeparturesAsync(string baseUrl,
        CancellationToken ct = default)
    {
        string body = await QlDirectHttp.GetTextAsync(Normalize(baseUrl) + Endpoint("departures"), ct)
            .ConfigureAwait(false);
        using var doc = ParseDocument(body, "departures");
        var root = doc.RootElement;
        var data = DataOf(root, "departures");

        var byRoute = new Dictionary<string, IReadOnlyList<QlMtrDepartureGroup>>(StringComparer.Ordinal);
        foreach (var item in ArrayOrEmpty(data, "departures"))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string? routeId = TrimToHex(item.TryGetProperty("id", out var idElement) &&
                                        idElement.ValueKind == JsonValueKind.String
                ? idElement.GetString()
                : null);
            if (routeId is null) continue;

            var groups = new List<QlMtrDepartureGroup>();
            foreach (var groupElement in ArrayOrEmpty(item, "departures"))
            {
                if (groupElement.ValueKind != JsonValueKind.Object) continue;
                var offsets = new List<long>();
                foreach (var offset in ArrayOrEmpty(groupElement, "departures"))
                    if (offset.ValueKind == JsonValueKind.Number && offset.TryGetInt64(out long value))
                        offsets.Add(value);
                groups.Add(new QlMtrDepartureGroup(Long(groupElement, "deviation"), offsets.ToArray()));
            }
            if (groups.Count > 0) byRoute[routeId] = groups.ToArray();
        }

        return new QlMtrDepartures(Long(root, "currentTime",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), byRoute);
    }

    /// <summary>取一批站台的实时到站；每站台最多 <paramref name="maxPerPlatform"/> 条。</summary>
    public static async Task<IReadOnlyList<QlMtrArrival>> FetchArrivalsAsync(
        IReadOnlyList<string> stationIdsHex, int maxPerPlatform, CancellationToken ct = default)
        => await FetchArrivalsAsync(QlDeployment.RequireMtrBaseUrl(), stationIdsHex, maxPerPlatform, ct)
            .ConfigureAwait(false);

    /// <summary>取实时到站（测试可指定 base URL）。</summary>
    public static async Task<IReadOnlyList<QlMtrArrival>> FetchArrivalsAsync(string baseUrl,
        IReadOnlyList<string> stationIdsHex, int maxPerPlatform, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            stationIdsHex,
            maxCountPerPlatform = maxPerPlatform,
        });
        string body = await QlDirectHttp.PostJsonAsync(Normalize(baseUrl) + Endpoint("arrivals"), payload, ct)
            .ConfigureAwait(false);
        using var doc = ParseDocument(body, "arrivals");
        var data = DataOf(doc.RootElement, "arrivals");

        var rows = new List<QlMtrArrival>();
        foreach (var item in ArrayOrEmpty(data, "arrivals"))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var cars = new List<string>();
            foreach (var car in ArrayOrEmpty(item, "cars"))
            {
                if (car.ValueKind != JsonValueKind.Object) continue;
                if (car.TryGetProperty("vehicleId", out var vehicle) &&
                    vehicle.ValueKind is JsonValueKind.String or JsonValueKind.Number)
                    cars.Add(vehicle.ToString());
            }
            rows.Add(new QlMtrArrival(
                Str(item, "destination"),
                Long(item, "arrival"),
                Long(item, "departure"),
                Long(item, "deviation"),
                Bool(item, "realtime"),
                (int)Math.Clamp(Long(item, "departureIndex"), 0, int.MaxValue),
                Bool(item, "isTerminating"),
                ToHexId(item, "routeId"),
                Str(item, "routeName"),
                Str(item, "routeNumber"),
                Int(item, "routeColor"),
                ToHexId(item, "platformId"),
                Str(item, "platformName", "1"),
                cars.ToArray()));
        }
        return rows;
    }

    /// <summary>向服务器路径查询一条最优路径。</summary>
    public static async Task<QlMtrDirections> FetchDirectionsAsync(string fromName, string toName,
        CancellationToken ct = default)
        => await FetchDirectionsAsync(QlDeployment.RequireMtrBaseUrl(), fromName, toName, ct)
            .ConfigureAwait(false);

    /// <summary>向服务器路径查询一条最优路径（测试可指定 base URL）。</summary>
    public static async Task<QlMtrDirections> FetchDirectionsAsync(string baseUrl, string fromName,
        string toName, CancellationToken ct = default)
    {
        var payload = JsonSerializer.Serialize(new
        {
            startTime = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            startStationName = fromName,
            endStationName = toName,
        });
        string body = await QlDirectHttp.PostJsonAsync(Normalize(baseUrl) + Endpoint("directions"), payload, ct)
            .ConfigureAwait(false);
        using var doc = ParseDocument(body, "directions");
        var root = doc.RootElement;
        var data = DataOf(root, "directions");

        var legs = new List<QlMtrDirectionsLeg>();
        foreach (var item in ArrayOrEmpty(data, "connections"))
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            string routeId = Str(item, "routeId");
            legs.Add(new QlMtrDirectionsLeg(!string.IsNullOrEmpty(routeId), routeId,
                Str(item, "startStationId"), Str(item, "endStationId"),
                Str(item, "startPlatformName"), Str(item, "endPlatformName"),
                Long(item, "startTime"), Long(item, "endTime"), Long(item, "walkingDistance")));
        }
        return new QlMtrDirections(Long(root, "currentTime",
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), legs.ToArray());
    }

    /// <summary>收集同一物理车站的全部站台 id（沿 connections 求闭包；无连接时退化为同名分组）。</summary>
    public static IReadOnlySet<string> StationGroupIds(QlMtrSnapshot snapshot, string stationId)
    {
        var byId = new Dictionary<string, QlMtrStation>(StringComparer.Ordinal);
        foreach (var station in snapshot.Stations)
            byId.TryAdd(station.Id, station);

        var group = new HashSet<string>(StringComparer.Ordinal) { stationId };
        var pending = new Queue<string>();
        pending.Enqueue(stationId);
        while (pending.Count > 0)
        {
            if (!byId.TryGetValue(pending.Dequeue(), out var node)) continue;
            foreach (string connection in node.Connections)
                if (group.Add(connection))
                    pending.Enqueue(connection);
        }
        if (group.Count > 1) return group;

        if (!byId.TryGetValue(stationId, out var self)) return group;
        string name = PrimaryName(self.Name);
        foreach (var station in snapshot.Stations)
            if (PrimaryName(station.Name) == name)
                group.Add(station.Id);
        return group;
    }

    /// <summary>
    /// 把时刻表包展开成某站点最近的一批到站。筛选按<b>到站</b>时间而非首站发车时间：
    /// 已经驶离首站但尚未抵达该站的在途列车同样计入，这样每个车站展示的是本站最近一班，
    /// 而不是某一班车整条线路的行程。
    /// </summary>
    /// <param name="departures">时刻表包。</param>
    /// <param name="route">线路。</param>
    /// <param name="stopIndex">线路内第几站（0 起）。</param>
    /// <param name="now">当前 epoch 毫秒。</param>
    /// <param name="limit">最多返回多少条。</param>
    public static IReadOnlyList<QlMtrTimedDeparture> NextAt(QlMtrDepartures departures,
        QlMtrRoute route, int stopIndex, long now, int limit)
    {
        if (!departures.ByRoute.TryGetValue(route.Id, out var groups)) return Array.Empty<QlMtrTimedDeparture>();

        long offsetToStop = 0;
        for (int i = 0; i < stopIndex && i < route.Durations.Count; i++)
            offsetToStop += route.Durations[i];

        var result = new List<QlMtrTimedDeparture>();
        foreach (var group in groups)
        {
            foreach (long offset in group.Offsets)
            {
                long departureAt = departures.Time + offset;
                long arrivalAt = departureAt + offsetToStop;
                if (arrivalAt < now - 60_000) continue;
                result.Add(new QlMtrTimedDeparture(departureAt, arrivalAt, group.Deviation));
            }
        }
        result.Sort((a, b) =>
        {
            int byArrival = a.ArrivalAt.CompareTo(b.ArrivalAt);
            return byArrival != 0 ? byArrival : a.DepartureAt.CompareTo(b.DepartureAt);
        });
        if (result.Count > limit) result.RemoveRange(limit, result.Count - limit);
        return result;
    }

    /// <summary>
    /// 从某站的实时到站行中选该线路/站台最近的一班。
    /// <para>线路时刻表按站查询时，每站只拿到本站的 arrivals；该函数负责过滤线路与站台，
    /// 这样不同车站可以落到不同班次，而不是一条线路共用同一班车的行程。</para>
    /// </summary>
    public static QlMtrArrival? NearestArrival(IReadOnlyList<QlMtrArrival> arrivals,
        QlMtrRoute route, QlMtrRouteStop stop, long now)
    {
        QlMtrArrival? best = null;
        foreach (var arrival in arrivals)
        {
            if (!string.Equals(arrival.RouteId, route.Id, StringComparison.OrdinalIgnoreCase)) continue;
            if (!string.IsNullOrEmpty(stop.PlatformName) &&
                !string.Equals(arrival.PlatformName, stop.PlatformName, StringComparison.Ordinal))
                continue;
            if (arrival.Arrival < now - 60_000) continue;
            if (best is null || arrival.Arrival < best.Arrival) best = arrival;
        }
        return best;
    }

    /// <summary>双语名的中文部分。</summary>
    public static string PrimaryName(string bilingual)
    {
        int split = bilingual.IndexOf('|');
        return split < 0 ? bilingual : bilingual[..split];
    }

    /// <summary>双语名的第二语言部分；没有竖线时为 null。</summary>
    public static string? SecondaryName(string bilingual)
    {
        int split = bilingual.IndexOf('|');
        return split < 0 ? null : bilingual[(split + 1)..];
    }

    /// <summary>线路名的方向后缀（<c>中文|English||方向</c> 的第三段之后）。</summary>
    public static string RouteDirection(string name)
    {
        string[] parts = name.Split('|');
        if (parts.Length >= 3) return string.Join('|', parts[3..]);
        if (parts.Length == 2) return parts[1];
        return "";
    }

    /// <summary>线路展示名：中文主名（调用方可另附编号）。</summary>
    public static string RouteDisplayName(QlMtrRoute route) => PrimaryName(route.Name);

    /// <summary>线路英文名：双语名的第二段（忽略方向段）。</summary>
    public static string RouteEnglishName(string name)
    {
        string[] parts = name.Split('|');
        return parts.Length >= 2 ? parts[1].Trim() : "";
    }

    /// <summary>
    /// 线路分组的键：中文主名 + 服务编号。MTR 把上下行拆成两条 route，
    /// 这个键把同一线路同一服务的多个方向合并成一条展示项。
    /// </summary>
    public static string RouteGroupKey(QlMtrRoute route)
        => PrimaryName(route.Name) + "\u001F" + route.Number;

    // ─────────────────────────── JSON 工具 ───────────────────────────

    private static string Endpoint(string name)
        => ApiPath + name + "?dimension=" + Uri.EscapeDataString(Dimension);

    /// <summary>容许调用方传入带或不带末尾斜杠的 base URL（测试服务器以斜杠结尾）。</summary>
    private static string Normalize(string baseUrl) => baseUrl.TrimEnd('/');

    private static JsonDocument ParseDocument(string body, string what)
    {
        try
        {
            return JsonDocument.Parse(body);
        }
        catch (JsonException e)
        {
            throw new IOException("MTR " + what + " 返回的不是合法 JSON", e);
        }
    }

    private static JsonElement DataOf(JsonElement root, string what)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object)
            return data;
        string code = root.ValueKind == JsonValueKind.Object &&
                      root.TryGetProperty("code", out var codeElement)
            ? "（code=" + codeElement + "）"
            : "";
        throw new IOException("MTR " + what + " 没有 data 段" + code);
    }

    private static IEnumerable<JsonElement> ArrayOrEmpty(JsonElement obj, string member)
    {
        if (obj.ValueKind != JsonValueKind.Object ||
            !obj.TryGetProperty(member, out var element) ||
            element.ValueKind != JsonValueKind.Array)
            return Array.Empty<JsonElement>();
        return element.EnumerateArray();
    }

    private static string Str(JsonElement obj, string member, string fallback = "")
    {
        if (!obj.TryGetProperty(member, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.String => value.GetString() ?? fallback,
            JsonValueKind.Number => value.ToString(),
            _ => fallback,
        };
    }

    private static int Int(JsonElement obj, string member)
        => obj.TryGetProperty(member, out var value) && value.TryGetInt32(out int parsed) ? parsed : 0;

    private static long Long(JsonElement obj, string member, long fallback = 0)
        => obj.TryGetProperty(member, out var value) && value.TryGetInt64(out long parsed) ? parsed : fallback;

    private static bool Bool(JsonElement obj, string member)
        => obj.TryGetProperty(member, out var value) &&
           value.ValueKind is JsonValueKind.True or JsonValueKind.False && value.GetBoolean();

    private static string ToHexId(JsonElement obj, string member)
        => obj.TryGetProperty(member, out var value) && value.TryGetInt64(out long parsed)
            ? parsed.ToString("X16", CultureInfo.InvariantCulture)
            : "-";

    private static string? TrimToHex(string? id)
    {
        if (id is null) return null;
        id = id.Trim();
        return id.Length == 16 ? id.ToUpperInvariant() : null;
    }
}

