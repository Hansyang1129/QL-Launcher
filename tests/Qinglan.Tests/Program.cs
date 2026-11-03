using Qinglan.Launcher.Core;
using Qinglan.Tests;

if (!OperatingSystem.IsWindows())
{
    Console.WriteLine("仅支持 Windows 测试（WPF 工程引用约束）");
    return 2;
}

Console.WriteLine("轻蓝启动器 引擎测试");
try
{
    ManifestStateSuite.Run();
    LayoutSuite.Run();
    SyncScenariosSuite.Run();
    SeedDblpackSuite.Run();
    LaunchSuite.Run();
}
finally
{
    Fx.CleanupAll();
}
return T.Finish();
