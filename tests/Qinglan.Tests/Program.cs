using System.Net;
using System.Net.Http;
using Qinglan.Launcher.Core;
using Qinglan.Tests;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("仅支持 Windows 测试（WPF 工程引用约束）");
    return 2;
}

// 测试的全部 HTTP 目标都是本机回环（MiniHttpServer）。用户若开着系统代理（旁路列表不含
// 127.0.0.1），默认 HttpClient 会把回环请求也送进代理，导致下载全灭。测试进程整体禁用代理。
HttpClient.DefaultProxy = new WebProxy();

Console.WriteLine("轻蓝启动器 引擎测试");
try
{
    ManifestStateSuite.Run();
    LayoutSuite.Run();
    SyncScenariosSuite.Run();
    PackDblpackSuite.Run();
    LaunchSuite.Run();
    AuthSuite.Run();
    RuntimeSuite.Run();
    RpuSuite.Run();
    StatusSuite.Run();
    MtrSuite.Run();
    ForumSuite.Run();
}
finally
{
    Fx.CleanupAll();
}
return T.Finish();
