using System.Text;
using Qinglan.Launcher.Core.Mtr;

namespace Qinglan.Tests;

/// <summary>M4 MTR 轨交引擎：快照/时刻表/到站/路径解析、站台分组、本地 Dijkstra 与展示整形。</summary>
internal static class MtrSuite
{
    public static void Run()
    {
        T.Group("M4 MTR 数据解析");
        Parsing().GetAwaiter().GetResult();
        T.Group("M4 MTR 时刻表展开");
        Timetable();
        T.Group("M4 MTR 站台分组与名称");
        GroupsAndNames();
        T.Group("M4 MTR 本地路径规划");
        Router();
        T.Group("M4 MTR 服务器路径整形");
        DirectionsShaping();
    }

    private const string SnapshotJson = """
        {"currentTime":1000000,"data":{
          "stations":[
            {"id":"000000000000000A","name":"甲站|Jia","color":255,"connections":["000000000000000B"]},
            {"id":"000000000000000B","name":"甲站|Jia","color":255,"connections":["000000000000000A"]},
            {"id":"000000000000000C","name":"丙站|Bing","color":0,"connections":[]},
            {"id":"000000000000000D","name":"丁站|Ding","color":0,"connections":[]}
          ],
          "routes":[
            {"id":"00000000000000A1","name":"一号线|Line 1||N","color":16711680,"number":"1","hidden":false,"circularState":"NONE",
             "stations":[{"id":"000000000000000A","name":"甲站","dwellTime":1000},
                         {"id":"000000000000000C","name":"丙站","dwellTime":1000},
                         {"id":"000000000000000D","name":"丁站","dwellTime":1000}],
             "durations":[120000,180000]},
            {"id":"00000000000000A2","name":"隐藏线|Hidden||S","color":0,"number":"","hidden":true,"circularState":"CLOCKWISE",
             "stations":[],"durations":[]}
          ]}}
        """;

    private const string DeparturesJson = """
        {"currentTime":1000000,"data":{"departures":[
          {"id":"00000000000000A1","departures":[
            {"deviation":0,"departures":[0,60000,120000]},
            {"deviation":30000,"departures":[30000]}]},
          {"id":"00000000000000A2","departures":[]}
        ]}}
        """;

    private static async Task Parsing()
    {
        await using var srv = await MiniHttpServer.StartAsync(Fx.TempDir());
        srv.AddFile("/mtr/api/map/stations-and-routes", Encoding.UTF8.GetBytes(SnapshotJson));
        srv.AddFile("/mtr/api/map/departures", Encoding.UTF8.GetBytes(DeparturesJson));
        srv.AddResponder("/mtr/api/map/arrivals", (_, _) => (200, Encoding.UTF8.GetBytes(
            """
            {"currentTime":1000000,"data":{"arrivals":[
              {"destination":"丙站|Bing","arrival":1060000,"departure":1061000,"deviation":0,"realtime":true,
               "departureIndex":0,"isTerminating":true,"routeId":161,"routeName":"一号线|Line 1||N",
               "routeNumber":"1","routeColor":16711680,"platformId":11,"platformName":"2",
               "cars":[{"vehicleId":"v1"}]}]}}
            """)));
        srv.AddResponder("/mtr/api/map/directions", (_, _) => (200, Encoding.UTF8.GetBytes(
            """
            {"currentTime":1000000,"data":{"connections":[
              {"routeId":"","startStationId":"","endStationId":"000000000000000A","startPlatformName":"","endPlatformName":"",
               "startTime":1000000,"endTime":1060000,"walkingDistance":10},
              {"routeId":"00000000000000A1","startStationId":"000000000000000A","endStationId":"000000000000000C",
               "startPlatformName":"1","endPlatformName":"2","startTime":1000000,"endTime":1120000,"walkingDistance":0}]}}
            """)));

        var snapshot = await QlMtr.FetchSnapshotAsync(srv.BaseUrl);
        T.CheckEq("快照服务器时间", 1000000L, snapshot.Time);
        T.CheckEq("车站数", 4, snapshot.Stations.Count);
        T.CheckEq("线路数（含隐藏）", 2, snapshot.Routes.Count);
        var line = snapshot.Routes[0];
        T.CheckEq("线路颜色", 16711680, line.Color);
        T.CheckEq("线路编号", "1", line.Number);
        T.CheckEq("线路三站", 3, line.Stops.Count);
        T.CheckEq("区间耗时两项", 2, line.Durations.Count);
        T.Check("隐藏线路标记", snapshot.Routes[1].Hidden);
        T.CheckEq("环线状态透传", "CLOCKWISE", snapshot.Routes[1].CircularState);
        T.CheckEq("站台连接", "000000000000000B", snapshot.Stations[0].Connections[0]);

        var departures = await QlMtr.FetchDeparturesAsync(srv.BaseUrl);
        T.CheckEq("时刻表包只保留有发车组的线路", 1, departures.ByRoute.Count);
        var groups = departures.ByRoute["00000000000000A1"];
        T.CheckEq("一条线路两个发车组", 2, groups.Count);
        T.CheckEq("首组偏差", 0L, groups[0].Deviation);
        T.CheckEq("首组三班", 3, groups[0].Offsets.Count);

        var arrivals = await QlMtr.FetchArrivalsAsync(srv.BaseUrl,
            new[] { "000000000000000A", "000000000000000B" }, 6);
        T.CheckEq("到站记录数", 1, arrivals.Count);
        T.CheckEq("负/正长整型转 16 位十六进制（线路）", "00000000000000A1", arrivals[0].RouteId);
        T.CheckEq("站台 id 转十六进制", "000000000000000B", arrivals[0].PlatformId);
        T.Check("实时标记", arrivals[0].Realtime);
        T.Check("终点站标记", arrivals[0].Terminating);
        T.CheckEq("车辆 id", "v1", arrivals[0].Cars[0]);
        string posted = srv.Bodies("/mtr/api/map/arrivals")[0];
        T.Check("到站请求带 stationIdsHex", posted.Contains("stationIdsHex", StringComparison.Ordinal));
        T.Check("到站请求带每站台上限", posted.Contains("\"maxCountPerPlatform\":6", StringComparison.Ordinal));

        var directions = await QlMtr.FetchDirectionsAsync(srv.BaseUrl, "甲站|Jia", "丙站|Bing");
        T.CheckEq("路径两段", 2, directions.Legs.Count);
        T.Check("首段是步行", !directions.Legs[0].Ride && directions.Legs[0].WalkingDistance == 10);
        T.Check("次段是乘车", directions.Legs[1].Ride && directions.Legs[1].RouteId == "00000000000000A1");
        T.CheckEq("路径耗时区间", 120000L, directions.Legs[1].Arrival - directions.Legs[1].Departure);
        string bodyText = srv.Bodies("/mtr/api/map/directions")[0];
        using (var request = System.Text.Json.JsonDocument.Parse(bodyText))
        {
            T.CheckEq("路径请求带出发站名", "甲站|Jia",
                request.RootElement.GetProperty("startStationName").GetString());
            T.CheckEq("路径请求带到达站名", "丙站|Bing",
                request.RootElement.GetProperty("endStationName").GetString());
        }

        // 服务器错误信封（HTTP 200 + 无 data）必须转成可读的 IO 异常。
        srv.AddFile("/mtr/api/map/stations-and-routes", Encoding.UTF8.GetBytes(
            """{"code":500,"message":"boom"}"""));
        bool failed = false;
        try
        {
            await QlMtr.FetchSnapshotAsync(srv.BaseUrl);
        }
        catch (IOException ex)
        {
            failed = ex.Message.Contains("data", StringComparison.Ordinal) &&
                     ex.Message.Contains("500", StringComparison.Ordinal);
        }
        T.Check("错误信封 → IOException 且带 code", failed);
    }

    private static void Timetable()
    {
        var snapshot = Snapshot();
        var route = snapshot.Routes[0];
        var departures = new QlMtrDepartures(1000000, new Dictionary<string, IReadOnlyList<QlMtrDepartureGroup>>
        {
            [route.Id] = new[]
            {
                new QlMtrDepartureGroup(0, new long[] { 0, 60000, 120000 }),
                new QlMtrDepartureGroup(30000, new long[] { 30000 }),
            },
        });

        var next = QlMtr.NextAt(departures, route, 1, 1000000, 2);
        T.CheckEq("展开后按发车时间排序取前二", 2, next.Count);
        T.CheckEq("首班发车", 1000000L, next[0].DepartureAt);
        T.CheckEq("首班到站含区间耗时", 1120000L, next[0].ArrivalAt);
        T.CheckEq("第二班为偏差组", 1030000L, next[1].DepartureAt);
        T.CheckEq("第二班到站", 1150000L, next[1].ArrivalAt);
        T.CheckEq("第二班偏差", 30000L, next[1].Deviation);

        var late = QlMtr.NextAt(departures, route, 1, 1400000, 5);
        T.Check("超过一分钟的旧班次被过滤", late.Count == 0);

        var unknown = QlMtr.NextAt(departures, new QlMtrRoute("00000000000000FF", "无|None", 0, "",
            false, "NONE", Array.Empty<QlMtrRouteStop>(), Array.Empty<long>()), 0, 1000000, 5);
        T.Check("未知线路返回空表", unknown.Count == 0);
    }

    private static void GroupsAndNames()
    {
        var snapshot = Snapshot();
        T.CheckEq("名称命中直接分组", 2, QlMtr.StationGroupIds(snapshot, "000000000000000A").Count);
        T.Check("连接闭包含自身与对端",
            QlMtr.StationGroupIds(snapshot, "000000000000000A").SetEquals(
                new[] { "000000000000000A", "000000000000000B" }));
        T.CheckEq("无连接车站独立分组", 1, QlMtr.StationGroupIds(snapshot, "000000000000000C").Count);

        T.CheckEq("中文主名", "甲站", QlMtr.PrimaryName("甲站|Jia"));
        T.CheckEq("无分隔符主名原样", "单名", QlMtr.PrimaryName("单名"));
        T.CheckEq("英文副名", "Jia", QlMtr.SecondaryName("甲站|Jia")!);
        T.Check("无分隔符副名为空", QlMtr.SecondaryName("单名") is null);
        T.CheckEq("线路方向后缀", "N", QlMtr.RouteDirection("一号线|Line 1||N"));
        T.CheckEq("两段名方向取第二段", "B", QlMtr.RouteDirection("A|B"));
        T.CheckEq("无方向后缀", "", QlMtr.RouteDirection("A"));

        var up = new QlMtrRoute(Id(0x51), "南际铁路|Nansai Rail||S2 -U", 0, "S2", false,
            "NONE", Array.Empty<QlMtrRouteStop>(), Array.Empty<long>());
        var down = new QlMtrRoute(Id(0x52), "南际铁路|Nansai Rail||S2 -D", 0, "S2", false,
            "NONE", Array.Empty<QlMtrRouteStop>(), Array.Empty<long>());
        var other = new QlMtrRoute(Id(0x53), "南际铁路|Nansai Rail||S3 -U", 0, "S3", false,
            "NONE", Array.Empty<QlMtrRouteStop>(), Array.Empty<long>());
        T.CheckEq("同线同服务上下行分组键一致", QlMtr.RouteGroupKey(up), QlMtr.RouteGroupKey(down));
        T.Check("不同服务分组键不同", QlMtr.RouteGroupKey(up) != QlMtr.RouteGroupKey(other));
        T.CheckEq("线路英文名忽略方向段", "Nansai Rail", QlMtr.RouteEnglishName(up.Name));
    }

    private static void Router()
    {
        // A -R1- C -R2- D，中间换线：总耗时 60s + 60s + 换乘惩罚 180s。
        string a = Id(0xA0), c = Id(0xC0), d = Id(0xD0);
        var snapshot = new QlMtrSnapshot(0,
            new[]
            {
                Station(a, "A|A"), Station(c, "C|C"), Station(d, "D|D"),
            },
            new[]
            {
                Route(Id(0x10), "一号线|Line 1||N", a, c),
                Route(Id(0x20), "二号线|Line 2||N", c, d),
            });

        var paths = QlMtrRouter.FindPaths(snapshot, a, d, 5);
        T.Check("找到一条换乘路径", paths.Count >= 1);
        if (paths.Count > 0)
        {
            var path = paths[0];
            T.CheckEq("两段行程", 2, path.Legs.Count);
            T.CheckEq("换乘一次", 1, path.Transfers);
            T.CheckEq("总耗时含换乘惩罚", 300000L, path.Total);
            T.Check("首段一号线", path.Legs[0].RouteName == "一号线" && path.Legs[0].Stops == 1);
            T.Check("次段二号线", path.Legs[1].RouteName == "二号线");
        }

        // 加入 A -R3- D 直达：应排在换乘方案之前，且搜索结果包含两种方案。
        var withDirect = new QlMtrSnapshot(0,
            snapshot.Stations,
            snapshot.Routes.Concat(new[] { Route(Id(0x30), "三号线|Line 3||N", a, d) }).ToArray());
        var ranked = QlMtrRouter.FindPaths(withDirect, a, d, 5);
        T.Check("直达与换乘方案并存", ranked.Count >= 2);
        if (ranked.Count >= 1)
        {
            T.CheckEq("直达方案单段", 1, ranked[0].Legs.Count);
            T.CheckEq("直达方案无换乘", 0, ranked[0].Transfers);
        }
        if (ranked.Count >= 2)
            T.Check("第二条为换乘方案", ranked[1].Legs.Count == 2 && ranked[1].Transfers == 1);

        // 站台步行换乘：C 与 C2 有 connections，R1 到 C、R2 从 C2 出发。
        string c2 = Id(0xC2);
        var walkSnapshot = new QlMtrSnapshot(0,
            new[]
            {
                Station(a, "A|A"), Station(c, "C|C", c2), Station(c2, "C|C", c),
                Station(d, "D|D"),
            },
            new[]
            {
                Route(Id(0x10), "一号线|Line 1||N", a, c),
                Route(Id(0x20), "二号线|Line 2||N", c2, d),
            });
        var walkPaths = QlMtrRouter.FindPaths(walkSnapshot, a, d, 5);
        T.Check("步行换乘路径存在", walkPaths.Count >= 1);
        if (walkPaths.Count > 0)
        {
            var legs = walkPaths[0].Legs;
            T.Check("路径含步行腿与两段乘车", legs.Count == 3 && legs[1].Walk);
            T.CheckEq("步行时间 2 分钟", 120000L, legs[1].Time);
            T.CheckEq("步行换乘总耗时", 420000L, walkPaths[0].Total);
        }
    }

    private static void DirectionsShaping()
    {
        string a = Id(0xA0), b = Id(0xB0), c = Id(0xC0);
        string routeId = Id(0x10);
        var snapshot = new QlMtrSnapshot(0,
            new[] { Station(a, "A|A"), Station(b, "B|B"), Station(c, "C|C") },
            new[] { Route(routeId, "一号线|Line 1||N", a, b, c) });

        // 同线两段 + 一个同站步行：应合并成一段乘车，步行被丢弃。
        var directions = new QlMtrDirections(0, new[]
        {
            new QlMtrDirectionsLeg(true, routeId, a, b, "1", "1", 1000, 61000, 0),
            new QlMtrDirectionsLeg(false, "", b, b, "", "", 61000, 181000, 0),
            new QlMtrDirectionsLeg(true, routeId, b, c, "1", "2", 181000, 241000, 0),
        });
        var path = QlMtrRouter.DirectionsToPath(snapshot, directions, "A", "C");
        T.Check("整形结果非空", path is not null);
        if (path is not null)
        {
            T.CheckEq("同线两段合并为一段", 1, path.Legs.Count);
            T.CheckEq("合并后终点", "C", path.Legs[0].ToStationName);
            T.CheckEq("合并后耗时", 120000L, path.Legs[0].Time);
            T.CheckEq("合并后站数", 2, path.Legs[0].Stops);
            T.CheckEq("网页路径无本地换乘惩罚", 0, path.Transfers);
            T.CheckEq("总耗时取首末时刻差", 240000L, path.Total);
            T.CheckEq("线路显示名", "一号线", path.Legs[0].RouteName!);
        }

        T.Check("空路径返回 null", QlMtrRouter.DirectionsToPath(snapshot,
            new QlMtrDirections(0, Array.Empty<QlMtrDirectionsLeg>()), "A", "C") is null);
    }

    // ─────────────────────────── 构造工具 ───────────────────────────

    private static QlMtrSnapshot Snapshot()
    {
        string a = Id(0x0A), b = Id(0x0B), c = Id(0x0C), d = Id(0x0D);
        return new QlMtrSnapshot(1000000,
            new[]
            {
                Station(a, "甲站|Jia", b),
                Station(b, "甲站|Jia", a),
                Station(c, "丙站|Bing"),
                Station(d, "丁站|Ding"),
            },
            new[]
            {
                new QlMtrRoute(Id(0xA1), "一号线|Line 1||N", 0xFF0000, "1", false, "NONE",
                    new[]
                    {
                        new QlMtrRouteStop(a, "1", 1000),
                        new QlMtrRouteStop(c, "1", 1000),
                        new QlMtrRouteStop(d, "2", 1000),
                    },
                    new long[] { 120000, 180000 }),
                new QlMtrRoute(Id(0xA2), "隐藏线|Hidden||S", 0, "", true, "CLOCKWISE",
                    Array.Empty<QlMtrRouteStop>(), Array.Empty<long>()),
            });
    }

    private static QlMtrStation Station(string id, string name, params string[] connections)
        => new(id, name, 0, connections);

    private static QlMtrRoute Route(string id, string name, params string[] stops)
        => new(id, name, 0x2F7FF0, id[^1].ToString(), false, "NONE",
            stops.Select(stop => new QlMtrRouteStop(stop, "1", 1000)).ToArray(),
            Enumerable.Repeat(60000L, Math.Max(0, stops.Length - 1)).ToArray());

    private static string Id(int value) => value.ToString("X16");
}



