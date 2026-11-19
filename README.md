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

仓库只包含空壳模板 [`config.example.json`](config.example.json)，**不包含任何真实部署端点、OAuth 客户端 id 或日志上报地址**。复制到 `<启动器目录>/data/config/config.json` 后填写 `deployment` 段：

- 留空的 `server` / `mtr` / `reporter` / `forum` / `sync.publisherBaseUrl`：对应模块显示统一的“配置未就绪”占位，不发起请求；
- 留空的 `auth.*`：自动使用微软官方登录端点，离线启动不受影响；
- `microsoftClientId`：仅本机配置，用于启用微软正版登录。

`data/` 已在 `.gitignore` 中，真实配置不会进入版本库。

## JVM / 内存

设置页的「Java 启动参数」支持填写内存上限（MB，留空 = 自动：物理内存 1/4，夹 `[2048, 8192]`）和额外 JVM 参数；首页内存下拉仍保留为快捷档位，非档位值显示为「自定义」。

拼装顺序固定为：

```text
-Xmx<最终值>M
版本 JSON 自带 JVM 参数
用户额外 JVM 参数
-Dlog4j.configurationFile=<路径>（用户未覆盖时）
主类
游戏参数
```

- 用户 `jvmArgs` 里写了 `-Xmx` 时以用户值为准，并与内存字段去重，最终只保留一条；版本 JSON 自带的 `-Xmx` 也会被删除。
- 用户写了 `-Dlog4j.configurationFile=` 时启动器不再注入第二条。
- 「Mojang 默认」按钮把版本 JSON 自带的 JVM 参数填入额外参数框，并自动取消「同时使用版本 JSON 自带 JVM 参数」，避免重复；取消勾选后完全由额外参数框决定。`useVersionJvmArgs` 默认 `true`。
- 无单位 `-Xmx4096` 按 4096 MB 处理并补 `M`；`-Xmx4G` / `-Xmx 4G` 也可识别。
- 未填任何额外参数时，生成的命令行与 R1 逐字等价。
- 「每个参数一行」勾选框只改变编辑视图：按顶层空格换行，引号内空格保留；底层仍保存原始字符串。
- `jvmArgs` / `jvmArgsOnePerLine` 只进本地用户配置，不写入空壳模板 `config.example.json`。

## 本地错误归因

启动失败或游戏非 0 退出时，启动器用 ExitCode、stderr、`data/launcher.log` 尾部与 `<log4j:Throwable>` 做**纯本地**规则匹配，覆盖缺 Java、Java 版本不匹配、OOM、模组冲突、下载/网络、登录失效、原生崩溃与未知兜底。结果以首页启动卡片展示：一句结论 + 1–2 条建议 + 可展开的原始日志；不自动修复、不自动改内存、不重启。

诊断卡只显示本地日志指纹（SHA-1 + 字节数）用于去重，不生成 URL、不上传日志、不发起任何网络请求。`Log4jConfigUrl` 仍是 Mojang log4j 配置的文件地址，与上报无关。

官方/本服构建可在 `src/Qinglan.Launcher/config.embedded.json` 放置同一结构的真实 `deployment` 段（该文件已 gitignore）；csproj 会把它作为 EmbeddedResource 打进 exe。运行时外部 `data/config/config.json` 若含 `deployment` 段则整段覆盖内嵌值，没有则直接使用内嵌值；开源构建没有该文件，保持全空占位。内嵌文件也可带 `microsoftClientId`，外部未填时作为默认值。仓库上传的始终只有带注释的空模板 `config.example.json`。

游戏内 RPU 客户端为闭源组件：本仓库只保留更新按钮、进度条和日志展示三个接口挂点，并保证同步不触碰 `qlrpu/**` 与 `config/qlrpu-client.json`。

许可：本仓库为独立实现（自有代码，不含 PCL/HMCL 源码）；数据格式与服务端沿用轻蓝社区既有设施。
