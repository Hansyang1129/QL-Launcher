namespace Qinglan.Launcher.Core.Mtr;

/// <summary>一条行程腿：乘车段或换乘步行段。</summary>
/// <param name="Walk">true = 步行换乘。</param>
/// <param name="RouteId">线路 id；步行段为 null。</param>
/// <param name="RouteName">线路中文主名；步行段为 null。</param>
/// <param name="RouteNumber">线路短编号；步行段为 null。</param>
/// <param name="RouteColor">线路显示色。</param>
/// <param name="FromStationId">起点站台 id。</param>
/// <param name="FromStationName">起点中文名。</param>
/// <param name="ToStationId">终点站台 id。</param>
/// <param name="ToStationName">终点中文名。</param>
/// <param name="Time">该段耗时毫秒。</param>
/// <param name="Stops">乘车段的站数；步行段为 0。</param>
public sealed record QlMtrLeg(bool Walk, string? RouteId, string? RouteName, string? RouteNumber,
    int RouteColor, string FromStationId, string FromStationName, string ToStationId,
    string ToStationName, long Time, int Stops);

/// <summary>一条候选路径。</summary>
/// <param name="Legs">按顺序排列的行程腿。</param>
/// <param name="Total">总耗时毫秒（含换乘惩罚与步行）。</param>
/// <param name="Transfers">换乘次数。</param>
/// <param name="Signature">去重签名（线路与区段序列）。</param>
/// <param name="RideEdges">用到的单跳乘车边，供备选路径搜索禁用。</param>
public sealed record QlMtrPath(IReadOnlyList<QlMtrLeg> Legs, long Total, int Transfers,
    string Signature, IReadOnlyList<string> RideEdges);

/// <summary>
/// 本地路径规划：把 MTR 网络快照建成带权图，用 Dijkstra 搜索最多 N 条不同路径。
/// 语义对齐 QL-HMCL 的 <c>QLMtrRouter</c>：换乘步行 2 分钟、换线惩罚 3 分钟、
/// 并行线路按显示身份去重、服务器路径结果整形为同一套卡片模型。
/// </summary>
public static class QlMtrRouter
{
    private const long WalkTime = 120_000;
    private const long TransferPenalty = 180_000;

    /// <summary>在线路图上搜索最多 <paramref name="maxPaths"/> 条不同路径。</summary>
    public static IReadOnlyList<QlMtrPath> FindPaths(QlMtrSnapshot snapshot, string fromStationId,
        string toStationId, int maxPaths)
    {
        var model = new Model(snapshot);
        var results = new List<QlMtrPath>();
        var signatures = new HashSet<string>(StringComparer.Ordinal);
        var banned = new HashSet<string>(StringComparer.Ordinal);

        for (int attempt = 0; attempt < maxPaths * 3 && results.Count < maxPaths; attempt++)
        {
            var candidate = Dijkstra(model, fromStationId, toStationId, banned);
            if (candidate is null) break;
            if (signatures.Add(candidate.Signature)) results.Add(candidate);
            if (candidate.Legs.Count == 0) break;
            if (candidate.RideEdges.Count == 0) break;
            foreach (string edge in candidate.RideEdges) banned.Add(edge);
        }
        return results;
    }

    /// <summary>把服务器 <c>directions</c> 的单条路径整形为本地卡片模型。</summary>
    public static QlMtrPath? DirectionsToPath(QlMtrSnapshot snapshot, QlMtrDirections directions,
        string fromName, string toName)
    {
        if (directions.Legs.Count == 0) return null;

        var stations = new Dictionary<string, QlMtrStation>(StringComparer.Ordinal);
        foreach (var station in snapshot.Stations) stations.TryAdd(station.Id, station);
        var routes = new Dictionary<string, QlMtrRoute>(StringComparer.Ordinal);
        foreach (var route in snapshot.Routes) routes.TryAdd(route.Id, route);

        var legs = new List<QlMtrLeg>();
        foreach (var leg in directions.Legs)
        {
            long time = Math.Max(0, leg.Arrival - leg.Departure);
            if (!leg.Ride)
            {
                legs.Add(new QlMtrLeg(true, null, null, null, 0,
                    leg.StartStationId, StationName(stations, leg.StartStationId, fromName),
                    leg.EndStationId, StationName(stations, leg.EndStationId, toName),
                    time, 0));
                continue;
            }

            routes.TryGetValue(leg.RouteId, out var route);
            string routeName = route is null ? leg.RouteId : QlMtr.PrimaryName(route.Name);
            string routeNumber = route?.Number ?? "";
            int routeColor = route?.Color ?? 0;
            legs.Add(new QlMtrLeg(false, leg.RouteId, routeName, routeNumber, routeColor,
                leg.StartStationId, StationName(stations, leg.StartStationId, fromName),
                leg.EndStationId, StationName(stations, leg.EndStationId, toName),
                time, StopCount(route, leg.StartStationId, leg.EndStationId)));
        }
        if (legs.Count == 0) return null;

        int transfers = 0;
        string? lastRideRoute = null;
        var rendered = new List<QlMtrLeg>();
        foreach (var leg in legs)
        {
            if (leg.Walk && leg.FromStationName == leg.ToStationName) continue;
            if (!leg.Walk && rendered.Count > 0)
            {
                var previous = rendered[^1];
                if (!previous.Walk && previous.RouteId == leg.RouteId)
                {
                    // 同一线路被拆成多段（或中间夹了同站步行）：合并回一段。
                    rendered[^1] = previous with
                    {
                        ToStationId = leg.ToStationId,
                        ToStationName = leg.ToStationName,
                        Time = previous.Time + leg.Time,
                        Stops = previous.Stops + leg.Stops,
                    };
                    continue;
                }
            }
            if (!leg.Walk && lastRideRoute is not null && lastRideRoute != leg.RouteId)
                transfers++;
            if (!leg.Walk) lastRideRoute = leg.RouteId;
            rendered.Add(leg);
        }
        if (rendered.Count == 0) return null;

        long total = Math.Max(0, directions.Legs[^1].Arrival - directions.Legs[0].Departure);
        return new QlMtrPath(rendered, total, transfers, "", Array.Empty<string>());
    }

    private static string StationName(IReadOnlyDictionary<string, QlMtrStation> stations,
        string stationId, string fallback)
    {
        if (string.IsNullOrEmpty(stationId)) return fallback;
        return stations.TryGetValue(stationId, out var station)
            ? QlMtr.PrimaryName(station.Name)
            : fallback;
    }

    /// <summary>乘车段跨越的站数；找不到两端时返回 0（服务器偶尔给出不存在的站台）。</summary>
    private static int StopCount(QlMtrRoute? route, string fromId, string toId)
    {
        if (route is null || route.Stops.Count < 2) return 0;
        int best = 0;
        for (int i = 0; i < route.Stops.Count; i++)
        {
            if (route.Stops[i].StationId != fromId) continue;
            for (int j = 0; j < route.Stops.Count; j++)
            {
                if (i == j || route.Stops[j].StationId != toId) continue;
                int distance = Math.Abs(j - i);
                if (best == 0 || distance < best) best = distance;
            }
        }
        return best;
    }

    private sealed class Edge
    {
        public Edge(string? routeId, string fromStation, string toStation, long cost)
        {
            RouteId = routeId;
            FromStation = fromStation;
            ToStation = toStation;
            Cost = cost;
        }

        public string? RouteId { get; }
        public string FromStation { get; }
        public string ToStation { get; }
        public long Cost { get; }
        public bool Ride => RouteId is not null;
    }

    private sealed class Model
    {
        public readonly Dictionary<string, List<Edge>> Adjacency = new(StringComparer.Ordinal);
        public readonly Dictionary<string, QlMtrStation> Stations = new(StringComparer.Ordinal);
        public readonly Dictionary<string, QlMtrRoute> Routes = new(StringComparer.Ordinal);

        public Model(QlMtrSnapshot snapshot)
        {
            foreach (var station in snapshot.Stations)
            {
                Stations.TryAdd(station.Id, station);
                Adjacency.TryAdd(station.Id, new List<Edge>());
            }
            foreach (var route in snapshot.Routes) Routes.TryAdd(route.Id, route);

            foreach (var route in snapshot.Routes)
            {
                if (route.Hidden || route.Stops.Count < 2) continue;
                for (int i = 0; i + 1 < route.Stops.Count; i++)
                {
                    string a = route.Stops[i].StationId;
                    string b = route.Stops[i + 1].StationId;
                    if (!Stations.ContainsKey(a) || !Stations.ContainsKey(b)) continue;
                    long cost = i < route.Durations.Count ? route.Durations[i] : 60_000;
                    Adjacency[a].Add(new Edge(route.Id, a, b, cost));
                    Adjacency[b].Add(new Edge(route.Id, b, a, cost));
                }
                if (route.Stops.Count > 2 && IsCircular(route))
                {
                    string a = route.Stops[^1].StationId;
                    string b = route.Stops[0].StationId;
                    if (Stations.ContainsKey(a) && Stations.ContainsKey(b) &&
                        Adjacency.TryGetValue(a, out var aEdges) &&
                        Adjacency.TryGetValue(b, out var bEdges))
                    {
                        long average = route.Durations.Count == 0
                            ? 60_000
                            : (long)route.Durations.Average();
                        aEdges.Add(new Edge(route.Id, a, b, average));
                        bEdges.Add(new Edge(route.Id, b, a, average));
                    }
                }
            }

            foreach (var station in snapshot.Stations)
            {
                if (!Adjacency.TryGetValue(station.Id, out var edges)) continue;
                foreach (string connection in station.Connections)
                {
                    if (connection == station.Id || !Stations.ContainsKey(connection)) continue;
                    edges.Add(new Edge(null, station.Id, connection, WalkTime));
                }
            }
        }

        private static bool IsCircular(QlMtrRoute route)
            => !string.IsNullOrEmpty(route.CircularState) && route.CircularState != "NONE";
    }

    private sealed record State(string Station, string? LastRoute, long Cost)
    {
        public string Key => Station + "|" + (LastRoute ?? "");
    }

    private sealed record Trace(State? Previous, Edge? Edge, long Cost);

    private static QlMtrPath? Dijkstra(Model model, string fromId, string toId,
        IReadOnlySet<string> banned)
    {
        if (!model.Adjacency.ContainsKey(fromId) || !model.Adjacency.ContainsKey(toId))
            return null;

        var queue = new PriorityQueue<State, long>();
        var traces = new Dictionary<string, Trace>(StringComparer.Ordinal);
        var start = new State(fromId, null, 0);
        traces[start.Key] = new Trace(null, null, 0);
        queue.Enqueue(start, 0);

        while (queue.TryDequeue(out var state, out long priority))
        {
            if (!traces.TryGetValue(state.Key, out var settled) || state.Cost > settled.Cost)
                continue;
            if (state.Station == toId)
                return TracePath(model, traces, state);

            foreach (var edge in model.Adjacency[state.Station])
            {
                if (edge.Ride && banned.Contains(edge.RouteId + "|" + edge.FromStation + ">" +
                                                 edge.ToStation))
                    continue;

                long cost = state.Cost + edge.Cost;
                if (edge.Ride && state.LastRoute is not null && state.LastRoute != edge.RouteId)
                    cost += TransferPenalty;

                var next = new State(edge.ToStation, edge.Ride ? edge.RouteId : state.LastRoute, cost);
                if (!traces.TryGetValue(next.Key, out var known) || cost < known.Cost)
                {
                    traces[next.Key] = new Trace(state, edge, cost);
                    queue.Enqueue(next, cost);
                }
            }
        }
        return null;
    }

    private static QlMtrPath? TracePath(Model model, IReadOnlyDictionary<string, Trace> traces,
        State arrival)
    {
        var path = new List<Edge>();
        var cursor = arrival;
        while (true)
        {
            if (!traces.TryGetValue(cursor.Key, out var trace)) return null;
            if (trace.Edge is null) break;
            path.Add(trace.Edge);
            if (trace.Previous is null) break;
            cursor = trace.Previous;
        }
        path.Reverse();

        var legs = new List<QlMtrLeg>();
        var rideEdges = new List<string>();
        var signature = new System.Text.StringBuilder();
        long total = 0;
        int transfers = 0;
        string? lastRideRoute = null;

        int index = 0;
        while (index < path.Count)
        {
            var edge = path[index];
            if (!edge.Ride)
            {
                var from = model.Stations[edge.FromStation];
                var to = model.Stations[edge.ToStation];
                legs.Add(new QlMtrLeg(true, null, null, null, 0,
                    from.Id, QlMtr.PrimaryName(from.Name),
                    to.Id, QlMtr.PrimaryName(to.Name), edge.Cost, 0));
                total += edge.Cost;
                index++;
                continue;
            }

            model.Routes.TryGetValue(edge.RouteId!, out var route);
            string routeName = route is null ? edge.RouteId! : QlMtr.PrimaryName(route.Name);
            string routeNumber = route?.Number ?? "";
            int routeColor = route?.Color ?? 0;
            string fromId = edge.FromStation;
            long legTime = edge.Cost;
            int stops = 1;
            rideEdges.Add(EdgeKey(edge));
            index++;
            while (index < path.Count)
            {
                var follow = path[index];
                if (follow.Ride && follow.RouteId == edge.RouteId)
                {
                    legTime += follow.Cost;
                    stops++;
                    rideEdges.Add(EdgeKey(follow));
                    index++;
                }
                else break;
            }
            string toId = path[index - 1].ToStation;
            var fromStation = model.Stations[fromId];
            var toStation = model.Stations[toId];
            legs.Add(new QlMtrLeg(false, edge.RouteId, routeName, routeNumber, routeColor,
                fromStation.Id, QlMtr.PrimaryName(fromStation.Name),
                toStation.Id, QlMtr.PrimaryName(toStation.Name), legTime, stops));
            signature.Append(routeName).Append('#').Append(routeNumber)
                .Append('|').Append(fromId).Append('>').Append(toId).Append('>');
            if (lastRideRoute is not null && lastRideRoute != edge.RouteId)
            {
                transfers++;
                total += TransferPenalty;
            }
            lastRideRoute = edge.RouteId;
            total += legTime;
        }
        if (legs.Count == 0) return null;
        return new QlMtrPath(legs, total, transfers, signature.ToString(), rideEdges);
    }

    private static string EdgeKey(Edge edge)
        => edge.RouteId + "|" + edge.FromStation + ">" + edge.ToStation;
}
