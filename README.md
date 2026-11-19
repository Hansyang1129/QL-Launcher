# 轻蓝启动器 (Qinglan Launcher)

为「轻蓝 MTR 4.x」整合包社区定制的轻量启动器。全新 C#/WPF 实现，不基于 PCL/HMCL 源码，
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

## 配置

仓库只包含空壳模板 [`config.example.json`](config.example.json)，**不包含任何真实部署端点、OAuth client id 或日志上报地址**。复制到 `<启动器目录>/data/config/config.json` 后填写 `deployment` 段：

- 留空的 `server` / `mtr` / `reporter` / `forum` / `sync.publisherBaseUrl`：对应模块显示统一的“配置未就绪”占位，不发起请求；
- 留空的 `auth.*`：自动使用微软官方登录端点，离线启动不受影响；
- `microsoftClientId`：仅本机配置，用于启用微软正版登录。

`data/` 已在 `.gitignore` 中，真实配置不会进入版本库。

官方/本服构建可在 `src/Qinglan.Launcher/config.embedded.json` 放置同一结构的真实 `deployment` 段（该文件已 gitignore）；csproj 会把它作为 EmbeddedResource 打进 exe。运行时外部 `data/config/config.json` 若含 `deployment` 段则整段覆盖内嵌值，没有则直接使用内嵌值；开源构建没有该文件，保持全空占位。内嵌文件也可带 `microsoftClientId`，外部未填时作为默认值。仓库上传的始终只有带注释的空模板 `config.example.json`。

游戏内 RPU 客户端为闭源组件：本仓库只保留更新按钮、进度条和日志展示三个接口挂点，并保证同步不触碰 `qlrpu/**` 与 `config/qlrpu-client.json`。

许可：本仓库为独立实现（自有代码，不含 PCL/HMCL 源码）；数据格式与服务端沿用轻蓝社区既有设施。
