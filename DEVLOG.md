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
- [x] M1 同步：Core(Sync) + tests(119 检查全绿)；同步页 UI + mrpack 种子导入接线真窗 4 页截图通过
- [~] M2 启动：引擎 a 段落地（版本合并/规划下载/natives/参数/Java 探测/进程壳，172 检查全绿）✅；
      UI 接线 + 微软 OAuth + JRE 缺位自动下载 ⏳
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
- **2026-11-02 M1 UI**：`SyncPage` 落地（对齐 HMCL-QL QLSyncPage 四能力 + 种子导入块）：实例卡
  （目录/包版本/频道即时展示）、manifest 地址框（URL 一动即作废已抓 manifest 并锁应用，M14 同效）、
  频道单选、可选组勾选（fetch 后按 manifest.OptionalGroups 重建，State 预填）、查更新 = fetch→Analyze
  →摘要（切频道/需下载/将重命名/将清理）、应用 = Sync+进度条（部分失败提示续传语义）、取消按钮；
  种子导入 = ReadHead 预览（名字/版本/MC/Loader）→ InstallMrpackAsync → Seedize → 摘要引导再同步。
  `SettingsPage` 真身：游戏目录选择 + 打开目录/日志。新增 `Core/Config/QlConfig`（data/config.json，
  损坏容忍）与 `Ui/Pickers`（OpenFolderDialog/OpenFileDialog）。引擎补：`Sync(…, manifestUrl)` 成功后
  记入状态文件；`QlSeedImporter.ReadHead/NormalizeVersionId`（照 QLManifestTool.normalizePackVersion：
  大小写不敏感剥 `-full`）。
  复刻查证（读 Java 源）：optional 后缀对 MODS_CHANNELS **每个**频道条目生效（QLSyncManager
  collectDesiredFiles 274-283 行）；QL-HMCL 行为备忘 §7-119 的「optional 只对 std 生效」是**发布器约定**
  （pfm 条目从不带 optional），引擎不受此限——本仓库与 Java 引擎同轨。
  冒烟：构建 0 错 0 警；测试 119/119 绿；GUI 存活 >9s。提交 `M1 UI`。
  下一步：M2 启动链（Java 探测/下载 → 1.20.1 fabric 启动 → 离线/微软登录 → 日志面板）。
- **2026-11-02 M1 收口**：揪出并修掉两枚被前几轮冒烟掩盖的真 bug + 截图自动化定型。
  ① **窗口根本没弹出来过**：`App.xaml` 一直缺 `StartupUri`，App.xaml.cs 也没 new MainWindow——
  应用无窗空转，M0 冒烟「进程存活 >9s」全是**假阳性**。已补 `StartupUri="MainWindow.xaml"`。
  流程铁律新增：GUI 冒烟必须等到主窗句柄/页面真渲染，进程存活≠窗口存在。
  ② **导航切页失灵**：RadioButton 挂的是 `Click`，而程序化切选（UIA Select/设 IsChecked）只触发
  `Checked` 路由事件——表现为高亮走了、内容区纹丝不动（前两轮截图铁证）。改为 `Checked="OnNav"`
  并加 `_suppressNav` 防重入 guard；真人鼠标路径同样由 Checked 覆盖，行为无损。
  ③ **截图自动化终于定型**（`%TEMP%\ql-tmp\shot.ps1`）：
  纯鼠标点击两条死路——后台进程 SetForegroundWindow 被前台锁拦（截图混入 QQ），且第一下点击
  被「激活窗口」吞掉不落到控件；定型方案 = Alt 键解锁 + SetForegroundWindow + HWND_TOPMOST
  置顶还原，切页一律走 UIA SelectionItemPattern（不依赖鼠标/前台），PSScript 全体 try/finally
  强制收尾杀进程（此前孤儿进程锁 exe 导致 MSB3027 编译连败 10 次重试）。
  成果：01-home / 02-sync / 03-status / 04-settings 四页真窗截图落档 `ql-tmp/shots/`；首页
  MTR 2.0 主视觉 + 启动游戏、同步页 manifest/频道/可选组/种子导入、状态页 M3 占位、设置页
  游戏目录 + 关于，底部状态条随页切换。构建 0 错 0 警，引擎测试态不变（119/119 未触碰）。
  ④ **产品事实（用户口述）**：真实游戏目录 `<gameDir>\.minecraft\versions\Qinglan-2nd-RPU\`；
  其 `mods/qlrpu-client-qlserver-1.0.0.jar`（4.8MB）是**资源包同步引擎 jar**（客户端+服务端），
  归整合包/资源服务器侧管；**模组不走独立 jar**，计划统一由包更新（manifest 通道）落地——
  本仓库同步引擎语义不变，QLRPU jar 在 sync 视角按 manifest 条目正常同步/不误删即可。
  下一步：M2 启动链（Java 探测/下载 → 1.20.1 fabric 启动 → 离线/微软登录 → 日志面板）。
- **2026-11-02 M2a 启动链引擎**：`Core/Launch/` 七件套 —— QlVersionJson（解析/合并/规则求值）、
  QlLaunchPrep（meta 解析 → 合并 → 计划 → 8 路并行下载 → natives 解包 → Finalize 套餐）、
  QlSession（离线身份）、QlArgsBuilder（模板变量注入）、QlJavaLocator（常用位扫描 + -version 判真）、
  QlGameProcess（args 启动 + stdout/stderr 逐行 + 退出码事件）；QlConfig 增 javaDir。
  **真实基准已抓档**（`ql-tmp/m2/vanilla-1.20.1.json` + `fabric-profile.json`），拿真数据修出的规则：
  ① 1.20.1：assetIndex id=**"5"**（totalSize≈652MB）、客户端 23MB/88 库；natives **扁平化**——
  `:natives-windows(-arm64/-x86)` 变体是普通库条目（rules 按 os 放行），x64 机则裁 arm64/x86 变体；
  ② fabric profile：mainClass=KnotClient、**arguments.game 为空数组**（必须吃 vanilla 的）、
  jvm=`-DFabricMcEmu= net.minecraft.client.main.Main `（**单 token 内嵌空格**）、库列表**无 downloads**
  → maven name 拼路径 + url 基地址（maven.fabricmc.net）；
  ③ feature 参数（demo/宽高/quickPlay）v1 不启用；--clientId/--xuid 离线空值成对剔除；
  ④ tokens：离线 UUID = MD5("OfflinePlayer:"+name) 大端 hex + v3/variant 位（与 Java 同构），
  accessToken "0"、userType legacy；log4j 配置在手则**显式追加** `-Dlog4j.configurationFile=`。
  版本隔离（对齐真实例观察）：versions/ libraries/ assets/ natives 落游戏目录内。
  下载口径同同步引擎：缺（大小不符）才下、SHA-1 写后强校验、资源索引损坏当场重下一次。
  测试 **172/172 全绿**（旧 119 + 新 53）：合并去重/规则裁剪/幂等规划/落盘校验/natives 解包/
  类路径序/参数注入/空对剔除/UUID 语义/版本行解析（17.x + 1.8 老记法）/进程壳（cmd exit 42 + 行流）。
  坑位：CheckEq 三参签名（name, expected, actual）——按老套件口径；feature 规则可能缺 action 宽容 allow。
  交付：`dist/m1-sync` + `dist/m2a-launch-engine`（exe 可跑；引擎 UI 接线在 M2b）。
  下一站：M2b —— UI 接线（首页 启动游戏 → 实例/用户名/内存 → 下载进度 → 日志面板）+
  微软 OAuth（device code）+ JRE 缺位自动下载。
- **2026-11-06 M2b-1 登录 + JRE 引擎**：`Core/Auth/QlMicrosoftAuth.cs`（设备码全链：devicecode →
  轮询 pending/slow_down/declined/expired → XBL(RPS 工单 d=<ms>) → XSTS(RETAIL/xsts → rp
  minecraftservices，401 体 XErr 语义表 233/235/236/237/238) → login_with_xbox(XBL3.0 x=<uhs>;<t>) →
  profile(Bearer → id/name → 连字符 UUID)；RefreshAsync refresh_token 静默续期（没回发就沿用旧值））
  + `Core/Launch/QlMojangRuntime.cs`（固定 sha 的 all.json → downloads[windows-x64][component] 首个
  大版本达标候选 → manifest files 表装配：目录创建/link 跳过/仅 LZMA 快速报错/就位文件 SHA-1 复验
  才免下/8 路下载 → 扫 bin/java.exe 定 Home；键带组件/平台前缀也能落家）。产物进 dataDir/runtimes/，
  QlJavaLocator 现成扫到（闭环）。`QlConfig` 加 microsoftRefreshToken / maxMemoryMb。
  MiniHttpServer 扩展：POST 唇言与头块记录 + 序列应答器（第 n 次给第 n 个应答）+ 命中计数。
  测试 **212/212 全绿**（+40）：登录 26 项（请求体逐形状断言）+ 运行时 14 项（路由/缺才下/校验重下/
  LZMA 拒装）。夹具教训：① gamma 组只会有 17.x——把 21 塞 gamma 组造出的"路由跳新-major"场景不是
  真实世界形态，Mojang 组内候选大头版本一致，首个达标即正解；② 预期抛断言忘包 try/catch 会把
  runner 整个炸掉（退出码 0xE0434352），异常场景必须 try/catch + 断言类型/消息。
  设计决策：**微软 client id 走配置**——fork 的 client id 是 gradle/CI 注入（源码/构建产物里都没有），
  v1 默认空 = 禁用微软登录入口（离线登录照常），用户注册 Azure 应用后填入 config.json 即启用。
  JRE 只吃 raw 下载（lzma-only 明确报错，v1 不手写 LZMA 解码）。QlHttp 超时 60s → 10min
  （ConnectTimeout 15s 单独兜底；44MB JRE / 慢索引单请求需要长尾）。
  下一站：M2b-2 UI 接线（首页 启动游戏 → 账号/登录弹层/内存 → 预备进度 → 日志面板 + 版行流）。
- **2026-11-17 RPU 引擎对接（缓存预热）**：启动器同步引擎对接 RPU 系统（`qlrpu-client-qlserver-1.0.0.jar`
  ↔ `rpu.example.com`），替代已死的旧 manifest 源。范围锁定**缓存预热**：只填充
  `<gameDir>/qlrpu/cache/objects`，不解密、不装 /keys（明文完整认证是游戏内 Mod 挂载期的事，
  引擎无 VERIFYING 阶段，共 6 阶段：初始化/在线授权与获取发布/扫描本地文件/探测下载源/下载缺失
  资源/校验完成），永不碰 objects 外的任何文件。产物：`Core/Sync/Rpu/` 六件——RpuEd25519（RFC8032
  零依赖验签，BigInteger）、RpuProtocol（模型/验签/校验/QRPUOBJ1 分段对象）、RpuClient（sessions/
  latest/manifest/sources/check/tickets/keys + 429/503 退避 + 401 重签）、RpuCache（密文名
  `<cipherSha256>.obj/.part`、update.lock 区域锁与 Java FileLock 互查、current.json 原子提交、
  7..90 天保留期清理）、RpuDeploymentConfig（exe 旁 qlrpu-client.json 覆盖 apiBase/公钥，SPKI 去
  头 32B）、RpuSyncEngine（check 128/批、无效对象上报空 cipherSha256、required 必须批内子集、
  工单 3 次重试 1s/2s、commit 快照后再清理）。
  线上地面真相（探针实测，`ql-tmp/rpu-live/`）：releaseId `a5f2…1655`、30 包/6964 文件/密文
  1,555,033,651B；**实测翻出四处协议细节**：① 清单 `packFormat` 是数字（15），Gson 对 String 字段
  数字/字符串二象兼容——STJ 直接炸，补 `RpuNumberOrStringConverter`；② `resourcePath` 真实规则是
  「非空/无首尾斜杠/无反斜杠/无盘符/段非空且非 . ../无 <0x20 与 0x7F」——**无小写限制无长度上限**
  （小写正则 `[a-z0-9/._-]+` 属挂载期 PackResourceIndex，与清单校验无关；线上真实路径
  `TW_spectial.png` 大写直接打脸第一版）；③ `validate` 的条目失败共用一句「资源路径、大小或哈希
  无效。」，包元数据要求 name/description 非空且包 id 全局唯一（「资源包元数据无效。」），objectId
  冲突比四字段，空 packs 合法；④ ClientApi 的 UA 是硬编码字面量 `QingLanRPU/1.0 protocol/1`
  （与 modVersion 1.0.0 无关）且带 `Accept-Encoding: identity`。
  本轮修出的真 bug：AesGcm.Decrypt 参数序（aad 占了 plaintext 槽，空对象即炸）；RpuClient
  EnsureSession↔Exchange 无限互递归（/sessions 的 POST 必须跳过会话前置）；ProbeSourcesAsync
  从未上报 Sources 阶段事件。测试侧两坑：**用户开着 Watt Toolkit 系统代理且旁路不含 127.0.0.1**，
  默认 HttpClient 把回环请求全送进代理导致老套件 38 项连坐——tests/Program.cs 统一
  `HttpClient.DefaultProxy = new WebProxy()`；MiniHttpServer 只对 responder 路径记录请求头。
  测试 **379/379 全绿**（212 + RpuSuite 11 组 167 项）：Ed25519 RFC/JDK 向量、线上信封回归（真实
  3MB 清单验签+解析+校验）、分段对象往返/篡改、清单校验、缓存锁/快照/对象/清理、部署配置、
  客户端形状/重试/错误文案、引擎端到端（假服务器四轮：全量下载→零下载+本地快照→陈旧清理→
  篡改自愈）。构建 0 错 0 警。
  下一步：M2b-2 UI 接线 → M3 状态页 → M4 发布；RPU 的 UI 挂点（同步页内嵌 RPU 进度）待 M2b-2 一并接。
