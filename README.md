# 轻蓝启动器 (Qinglan Launcher)

为「轻蓝 MTR 2.0」整合包社区定制的轻量启动器。全新 C#/WPF 实现，不基于 PCL/HMCL 源码，
manifest v1 格式与 `ql-launcher`（HMCL fork）完全兼容，共用同一套资源服务器与发布工具。

## v1 功能（核心四件套）

1. **轻蓝同步**：manifest 驱动增量同步（std/pfm 双频道 + 可选 mod 组），种子包（.mrpack）导入
2. **启动**：fabric 实例启动（Java 自动下载、离线 + 微软正版登录）
3. **服务器状态**：SLP + HTTP 探测、自动轮询
4. （v1.1 预告）MTR 轨交页、论坛页、mod/光影下载页

## 结构

```
src/Qinglan.Launcher     WPF 主程序
src/Qinglan.Launcher/Core    无 UI 的引擎（同步 / 启动 / 状态 / 配置）
tests/Qinglan.Tests          纯引擎测试（复刻 HMCL-QL 的 E2E 场景 A–E）
```

## 构建

```powershell
dotnet build src/Qinglan.Launcher                 # 主程序
dotnet run --project tests/Qinglan.Tests          # 引擎测试（无外部依赖的自写 runner）
dotnet publish src/Qinglan.Launcher -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

许可：本仓库为独立实现（自有代码，不含 PCL/HMCL 源码）；数据格式与服务端沿用轻蓝社区既有设施。
