# DEVLOG — 轻蓝启动器

> 给接手者（人类或 AI）：本文件是项目唯一权威状态文档。开工先读最后一条日志，收工更新后提交。
> 行为规范的权威参照：`REPO_ROOT\ql-launcher\QL-HMCL 行为备忘`（§7 引擎行为备忘 + §6 E2E 场景）。
> 本仓库与其同吃一套 manifest v1 / ql-manifest-state.json / dblpack.json 格式与同一资源服务器。

## 1. 约定

- 目标 .NET 10 (`net10.0-windows`) + WPF；**零 NuGet 依赖**（内置 System.Text.Json/HttpClient 即可）。
- 界面语言只做简中；品牌名「轻蓝启动器 / Qinglan Launcher」，不使用 PCL 字样。
- 铁律：测试产物放 `%TEMP%\ql-tmp\`，C 盘 Temp 只放小文件。
  （沿用 ql-launcher 仓库教训：C 盘空间紧张。）
- 引擎代码照 QL-HMCL 行为备忘 §7 行为备忘重写，**不逐行翻译 GPL Java 源**；数据格式是事实不受版权约束。
- commit 规约：`feat(ql):/fix(ql):/docs(ql):` + 简述。git 身份沿用 `<username>` / `<email>`。

## 2. 关键事实速查（同步引擎，来自 ql-launcher 定稿行为）

- 通道：`std`(官方包)/`pfm`(优化包) 各自 mods 文件夹（活跃=`mods`，停用=`mods-std`/`mods-pfm`，标记文件 `.qinglan-channel`）；`common` 直接落在游戏目录（相对路径含目录前缀）。
- 状态文件 `ql-manifest-state.json`：`packVersion/manifestUrl/activeChannel/enabledOptional/tracked{channel::{path:sha1}}`；损坏自动挪为 `.corrupt` 并空态起步。
- 规划**全部** MODS_CHANNELS 条目 + common；切频道不删对方文件；禁用可选 mod = 落成 `*.jar.disabled`。
- reconcile：OK / RENAME（`.jar`↔`.jar.disabled` 兄弟互换免下载，**不计入 unchanged**）/ DOWNLOAD。
- purge 只删 `tracked` 记录的文件（键 `channel::logicalPath`），用户文件永不触碰。
- 下载键必须用文件值对象而不是“文件名”当 key（同名文件任务曾静默丢失）。
- manifest 校验：schema=1，channels 必含 std/pfm/common，路径禁 `/`开头、`..`、`:`，sha1/urls/size(>=0) 必填，渠道内 path 去重。
- `versionId` 导入时去 `-full` 后缀；发布器排除 PCL/、servers.dat、modrinth.index.json、fabric-loader-*。
- E2E 五场景（见 §6 表）：A 新装 406 下载 / B 幂等 / C 关 dh RENAME / D 切 pfm / E 切回；本仓库 tests 全量复刻。

## 3. 计划（v1 四件套）

- [x] M0 脚手架：WPF 工程、无边框壳、侧栏导航、主题、DEVLOG
- [~] M1 同步：Core(Sync) + tests(119 检查全绿) ✅；同步页 UI + mrpack 种子导入接线 ⏳
- [ ] M2 启动：Java 探测/下载、1.20.1/fabric 启动链、离线 + 微软登录、日志面板
- [ ] M3 状态页：SLP varint + HTTP 探测 + 30s 轮询
- [ ] M4 发布：单文件自包含 exe + 文档

## 4. 工作日志（append-only）

- **2026-11-02 M0**：仓库创建。WPF 脚手架 + 主题（浅蓝、圆角卡片、无边框 + 自定义标题栏）、
  侧栏导航四入口（首页/轻蓝同步/服务器状态/设置）。壁纸素材计划从 `HMCL-3.16.4-ql2.jar`
  的 `assets/img/wallpapers/ql/` 直接取用（原图来自用户自己的服务器截图）。
  下一步：Core(Sync) 三个模型 + 目录布局 + 状态文件（照 §2/§7 复刻），tests 五场景。
- **2026-11-02 M1 引擎**：`Core/Sync/`（Manifest 严格校验 / SyncState `.corrupt` 兜底 / DirectoryLayout
  recover 四分支 / SyncManager.Analyze+Sync / SeedImporter mrpack+seedize / DblPack 清单）+ `Net/QlHttp`
  （.part + SHA-1 校验 + 候选 URL 重试）。测试 `tests/Qinglan.Tests` 四个套件 **119 检查全绿**：
  A 新装 8 下载 / B 幂等 / C 关 dh（std+pfm 各一次 RENAME）/ D 切 pfm 零下载 / E 切回、purge（双频道各删一）、
  部分失败不记账、取消不记账、Analyze 干跑、FULL/LITE 种子、dblpack 元数据、坏 mrpack 拒收。
  坑位记录：① 下载必须**先建父目录**再写 `.part`（mods-pfm/resourcepacks 这类不存在的目录曾全军覆没）；
  ② `MiniHttpServer.AddFile` 只进内存词典，禁止顺手落盘（曾把 `Path.Combine` 根路径写穿到 `D:\mods`）；
  ③ `Fx.Manifest` 的 pfm 缺省镜像 std 三个条目，planned 总数 = 3+3+2 = 8，C/E 的 rename 在两个频道各发生一次。
  下一步：SyncPage UI（查更新/频道切换/可选勾选/应用/导入区，versionId `-full` 剥离）→ 提交 M1 UI。
