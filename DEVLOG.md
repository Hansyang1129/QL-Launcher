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
- **2026-11-17 对接 QL-HMCL 新版同步（包驱动引擎）**：同步栈按 QL-HMCL fork 新版引擎整体重写
  （地面真相全读：QLPackSync/QLSyncState/QLConstants/QLManifest(Tool)/QLDirectoryLayout/
  QLDblPack/QLPackGuard/QLInstances/ui.QLSyncPage）。范式变更：**没有 manifest 服务器**——
  fork 里 `DEFAULT_MANIFEST_URL=""`（files.example.com 占位从未上线，假 URL 输入框删除）；
  更新源 = 整合包自己的 `.mrpack`（同步页现场挑文件，否则回落实例 `.ql-pack/` 缓存最新包，
  构建里不烘任何远端地址）。新组件：`Mrpack.cs`（索引模型/ReadHead/版本归一剥 `-full`）、
  `MrpackManifest.cs`（buildFromMrpack 移植：`mods/**`→std 单频道；`config|shaderpacks|
  resourcepacks/**`+`options.txt`→common；`PCL/`、`servers.dat`、`modrinth.index.json` 跳过；
  `fabric-loader-*`/`fabric-api*` 排除；可选组按文件名 voxy/dh/axiom 且只有 mod 进组；
  index 覆盖 overrides；sha1 门 = 40 位长度，Java 精确不查字符集）、`PackSync.cs`（overrides
  载荷优先直写零网络 → URL 候选序（CDN 在前、发布器兜底 URL 垫底，与 Java 字节对齐）；
  `.qlimport` 临时 + 原子替换；SHA-1 复验错删；plain↔`.disabled` 姊妹收养；purge 只清计划外；
  RPU 遗留 = handedOver 交接不删；**部分失败状态照记**（Java 同款，失败件下轮重规划）；
  dblpack 兜底重生成）、`PackGuard.cs`（mods 里 qinglanrpuclient 门禁，停用副本不算）、
  `Instances.cs`（只管 `Qinglan-2nd-RPU`：版本隔离目录优先，平铺实例兼容）。状态文件新增
  `packUrl`（null=重新挑包；空白归一 null；传 null 保留已记录值）与 `optionalRecorded`
  （全新实例随包全启用，记录后缺席组=用户主动停用）；`manifestUrl`/`activeChannel` 只读兼容。
  删除 `SeedImporter`/`ManifestFetcher`/`SyncManager` 与 `SwitchChannel`（pfm 双频道时代终结，
  pfm 标记失效、遗迹不抬升原地保留）；种子导入并入包应用（overrides 优先写即全新安装）。
  归属口径（用户拍板）：**resourcepacks/shaderpacks/config/mods 全部 mrpack 管，
  `qlrpu/`+`config/qlrpu-client.json` 归 RPU**。同步页 UI 重写：游戏根目录选择 + 实例卡
  （版本隔离/平铺标签 + RPU mod 门禁提示，无 mod 锁应用）+ 包浏览/`.ql-pack` 回落 +
  查更新（读版本差 + 可选组勾选即落盘）+ 应用（进度条/分账摘要/失败续传提示）；
  RPU 半空占位卡（预留：更新状态·发布清单·手动刷新）；设置页措辞改「游戏根目录」。
  Java quirk 保真两枚：① 停用全部可选组时记录回落随包状态（`enabledGroups` 空 → allGroups
  fallback，QLPackSync 逐字对齐，下一轮按启用计划收养还原）；② 进度 total = planned+tracked
  双计、清理循环遇 plannedPaths 直接 continue 不推进 finished——幂等轮 finished 停在计划数。
  测试 **390/390 全绿**（+11）：SyncScenariosSuite 重写（FULL 全新应用/幂等/停用-还原/packUrl
  三态/进度/双组停一部分/清除/RPU 交接/LITE 部分失败续跑）、ManifestStateSuite（单 std 必需、
  pfm-only 拒、RPU 归属、新字段往返）、LayoutSuite（pfm 标记失效）、新 PackDblpackSuite
  （路由表/overrides 优先零网络/index 下载/dblpack 元数据/ReadHead/坏包容忍）替换
  SeedDblpackSuite。测试坑两枚：夹具可选组文件名必须真含 distant+horizon（按名识别）；
  overrides 按条目名排序遍历（Java 同序），可选组顺序是 ASCII 排序序而非声明序。
  截图 `ql-tmp/sync-page.png`：实例卡「Qinglan-2nd-RPU（平铺）」+ RPU mod 门禁在位 +
  包卡 + RPU 占位，假 URL 框/频道切换/取消键全部消失。构建 0 错 0 警。
  下一步：真包在 <gameDir> 实例现场跑一轮同步实测 → M2b-2 UI 接线 → M3 状态页 → M4 发布。
- **2026-11-18 真包实测 + RPU 闭源口径 + 摘要保留**：① 真包 `Qinglan-2nd-RPU 26.9.1.mrpack`
  （68.3MB）解析零漂移：versionId=26.9.1、MC 1.20.1、loader 0.19.5、std=70、common=176
  （config 172 / options.txt 1 / shaderpacks 3）、可选组 axiom/dh/voxy。实例
  `<gameDir>\.minecraft\versions\Qinglan-2nd-RPU` 的 `ql-manifest-state.json` 由 QL-HMCL 写入，
  C# 读出一致（packVersion=26.9.1、packUrl=null、optionalRecorded=true、enabled=[axiom]、
  tracked std=70/common=176）——**共享状态文件互操作已实证**，格式不动。现场同步：
  下载 3 / 未变 243 / 重命名 0 / 删除 0 / 交接 0 / 失败 0；3 个下载是渲染器运行时改写的
  `config/{indium-renderer,iris}.properties`、`config/fabric/indigo-renderer.properties`
  （包内容≠运行时状态，每轮同步重写回包版本，属预期，非遗漏）。快照备份在
  `ql-tmp/realrun/backup/`（368 文件），harness `ql-tmp/realrun` 可复跑 inspect/--sync。
  ② RPU 口径（用户拍板）：**RPU 为闭源组件，读写与更新全部由游戏内客户端自行完成**；启动器
  只留两个接口挂点——`RpuUpdateRequested` 事件（更新触发）+ `SetRpuUpdateState(status, percent?)`
  （进度投影），UI 卡片改为说明文案 + 禁用「检查 RPU 更新」按钮 + 默认隐藏进度条，不实现任何
  RPU 更新/读写逻辑。③ 修复：应用完成后摘要被 `RefreshInstanceAsync` 重置抹掉——`RefreshInstanceAsync`
  / `ResetFlow` 增加 `keepResult` 参数，`OnApply` 收尾走 `keepResult: true`，摘要保留。
  UIA E2E（`ql-tmp/e2e-sync.ps1`，截图 `ql-tmp/e2e/01-page|02-checked|03-applied.png`）：
  标题「版本隔离」正确、可选组仅 axiom 勾选、查更新「已是最新：26.9.1（无需操作）」、
  应用后「同步完成：下载 3 · 未变 243」且进度条停在 50%（Java 双计数同款）。测试 **390/390 全绿**，
  构建 0 错 0 警。下一步：M2b-2 启动 UI 接线 → M3 状态页 → M4 发布。
- **2026-11-18 M2b-2 启动 UI 接线**：首页重做成启动台（左右两栏）——左列实例卡（包名·版本来自
  dblpack/sync state、布局与实例路径）+ 账号卡（离线名 / 微软设备码登录浮层：代码复制、跳转登录页、
  关窗取消；页面加载静默 refresh_token 续期，恢复失败明确提示；「退出登录」回离线）+ 运行环境
  （内存下拉 + Java 探测行）+ 启动卡（启动/取消准备/结束游戏、进度条、状态行）；右列启动日志
  （TextBox 自动换行 + 200k 字符截断 + 自动滚底 + 清空/打开目录；占位文案首次输出时清掉）。
  设置页加 Java 目录选择/恢复自动探测（落盘前校验 bin\java.exe）与微软 client id 保存入口。
  引擎侧小口子：`QlVersionJson.JavaComponent` 解析+合并；`QlLaunchPrep.Finalize` 增加可选
  `nativesDir`（版本隔离实例的 natives 落 `versions/Qinglan-2nd-RPU/natives-windows`，避免污染
  根目录）；`QlJavaLocator` 递归扫 `data/runtimes` 的 bin/java.exe（Mojang 清单的组件前缀子目录
  不再漏）；新 `QlMemory`（0 = 物理内存 1/4 夹 [2048, 8192]，本机自动 7628 MB）；`QlConfig` 加
  `microsoftClientId` / `microsoftAccountName` / `playerName`。离线名默认取 `Environment.UserName`
  清洗后前 16 位（本机 <player>），首次启动落盘。
  **抓到一个致命老 bug**：`QlArgsBuilder` 从未把主类插进 argv，整套游戏参数被 java 当成 JVM
  选项 → `Unrecognized option: --username` + `Could not create the Java Virtual Machine`。
  正确顺序固定为：`-Xmx` + JVM 模板参数 → `-Dlog4j.configurationFile`（JVM 选项）→ 主类 →
  游戏参数，空值可选对（--clientId/--xuid）在最后整对剔除；补了主类唯一性/位置断言。
  控制台日志是 Mojang `LegacyXMLLayout` 跨行 XML，日志面板观感极差：新 `QlLogXml` 把
  `<log4j:Event>` 事件还原成 latest.log 同款 `[thread/LEVEL]: message`（Throwable 换行保留，
  非 XML 行透传，收尾不全时原文兜底），只做展示层整形，不动游戏 log4j 配置、不落盘。
  真实例实测（`<gameDir>\.minecraft`，基础设施本来就全：libraries 324 文件、assets 9361 文件、
  64 个适用库与 3598 个资源对象全在位）：缺件只剩 `assets/log_configs/client-1.12.xml` 一个，
  自动补齐后装配 96 库/59 段类路径；Java 自动选中系统 `C:\Program Files\Java\jdk-21`（1.20.1
  只需 17+），Mojang 运行时下载路径仍留作无 Java 时兜底（引擎已测，本轮未触发）。
  UIA E2E（`ql-tmp/e2e-launch.ps1`）：`ql-tmp/e2e-launch/01-home|02-preparing|03-running|
  04-exited|05-settings.png`——日志已是可读纯文本（199 mods、Sodium/NVIDIA+AMD 适配器、
  MemoryLeakFix 等），结束游戏后 UI 回空闲。完整进游戏验证（`ql-tmp/e2e-game-boot.ps1`）：
  游戏窗口 `Minecraft* 1.20.1` 起来并捕获 `ql-tmp/e2e-boot/06-game-window.png`（窗口内是 RPU
  自己的「在线授权与获取发布」启动闸门，属闭源组件流程，启动器不介入）。微软登录本轮
  client id 为空未实机走设备码（引擎在 M2b-1 已覆盖）；实际用离线账号 <player> 打通全链。
  测试 **409/409 全绿**（390 + 19：Java 组件传递/内存策略/natives 覆盖/配置往返/日志整形/
  参数顺序），构建 0 错 0 警。下一步：M3 服务器状态页 → M4 发布打包。
- **2026-11-18 M3 服务器状态页**：新 `Core/Status/QlServerStatus.cs`——Minecraft SLP（1.7+ 协议 47
  varint 分帧握手 + 状态请求，递归摊平 description/MOTD、players.online/max、sample 玩家名，
  分帧上限 1MB 防恶意长度）与 HTTP GET（读头 + 最多 4KB 正文）双探测；**仅 HTTP 200 算在线**
  （维护页/错误页不能伪装健康），顺序探测、单点 5s 超时、任何单点异常收敛成离线卡片不拖垮整表，
  取消向上抛出。状态页重写：刷新卡（时间戳/汇总/30s 自动刷新提示）、三张端点卡（标签/地址/延迟/
  在线人数/MOTD/失败原因 + 在线离线胶囊），`Loaded`/`Unloaded` 启停 30s 计时器并在每轮结束重置
  （手动与自动共用 busy 闸门，离页即暂停）；MOTD 的 §x 传统颜色码只在展示层剥除，引擎结果保持原文。
  **两处真实环境坑**：① QL-HMCL 的默认表探三个服务的根路径，实测 `:14614`/`:19451` 根路径
  404/404、`:19451` 其余路径 401（服务在线，只是根路径不是健康页）——改探真实 200 路径：
  MTR 地图 API `/mtr/api/map/stations-and-routes?dimension=minecraft:overworld`、Reporter `/login`。
  ② 系统代理（Watt Toolkit `127.0.0.1:26561`）对非标端口直接回 404，`HttpClient` 默认走代理时
  启动器 1/3 在线而同机 curl 全 200；状态探测改用 `UseProxy=false` 的专用 `ProbeClient` 强制直连，
  与 SLP 的直连 TCP 同口径。UIA E2E（`ql-tmp/e2e-status.ps1`）：首轮 18:24:40 → **3/3 在线**——
  轻蓝 SLP 延迟 88 ms、在线 0/16、MOTD「轻蓝 二周目-26.8.x / 直接运维: <player>…」可读；
  MTR Web HTTP 200 115 ms；Reporter HTTP 200 131 ms；30s 后 18:25:10 时间戳更新、延迟重测
  （64/116/98 ms），自动刷新实证。截图 `ql-tmp/e2e-status/01-status.png`、`02-auto-refresh.png`。
  测试 **459/459 全绿**（+50：SLP 握手字节/分帧异常/varint 过长/读取超时/连接拒绝、HTTP 200/500/404/
  超时/非法 URL/非 HTTP 协议、批量隔离与默认表、MOTD 摊平），构建 0 错 0 警。
  下一步：M4 发布打包。
- **2026-11-18 M3 补丁：在线玩家卡片 + 加速器双态复测**：状态页新增「在线玩家」卡——SLP 人数
  `在线 X / Y`、玩家名胶囊（最多渲染 64 个防超大 sample）、空状态（无人在线 / 服务器未公开名单）、
  样本少于在线数时注明「仅显示服务器公开的 N 位玩家」；端点卡移除重复的「在服务器：…」行。
  假 SLP（127.0.0.1:32650，3 人 Alice/Bob/Carol）验证胶囊布局：`ql-tmp/e2e-status/03-players-fake.png`；
  真实端点复测 0/16 空状态：`03-players.png`。加速器双态：开启（ProxyEnable=1，e2e3）与关闭
  （=0，e2e4）各跑一轮，均 **3/3 在线**——`UseProxy=false` 的 ProbeClient 不受系统代理开关影响。
  测试仍 **459/459 全绿**，构建 0 错 0 警。下一步：M4 发布打包。
- **2026-11-18 M4 三页：MTR 地图 / MTR 轨交 / 论坛**：① **MTR 地图**用 WebView2 内嵌服务器
  `http://mc.example.com:14614/`，用户目录放 `data/webview2`，站内弹窗交给系统浏览器；运行时
  缺失、初始化失败、导航失败都切同一块「webview 不可用」提示并保留浏览器入口（WebView2 是独立
  HWND，提示层显示前先隐藏 WebView，避免盖住 WPF 覆盖层）。SDK 三件 DLL 直接放 `lib/webview2/`
  随仓库分发，csproj 用 `<Reference>` + `<Content>` 落地，仍然零 NuGet。② **MTR 轨交**：站台级
  车站按 `connections` 并查集 + 中文主名兜底合并成用户理解的一座站（对齐 QL-HMCL
  `stationGroupIds`）；车站视图「按站台」请求全站实时到站并按站台分组、「按线路」列出途经线路与
  最近一班；线路视图按站序逐站展示下一班与停靠站台；路径规划先取服务器 `directions`，再对起讫站
  全部站台组合跑本地 Dijkstra，按总耗时补最多 5 条备选，卡片明确标注「网页查询 / 本地规划」。
  ③ **论坛**：Halo 内容 API 列表优先，接口 500 时自动切 RSS 备份并在页内黄条说明；正文先走内容
  API，失败或 RSS 来源时抓渲染页 `article#content`，HTML 转纯文本按段落/列表/引用整形，每篇保留
  浏览器入口。MTR/论坛请求统一走 `QlDirectHttp`（`UseProxy=false`），与状态探测同口径绕开本机
  加速器代理。真实环境：MTR 快照 **239 站 / 155 线**、地图 WebView2 实渲染成功；论坛列表 API
  当前 500，RSS 备份 20 帖、正文可读。UIA E2E（`ql-tmp/e2e-m4.ps1`）截图 `ql-tmp/e2e-m4/`：
  `01-map` 内嵌真实线网、`02` 站台实时到站（分钟/实时/站台胶囊）、`03` 按线路视图、`04` 21 站
  线路时刻表、`05` 查询提示、`06` 网页方案 + 本地备选（站数已按线路站序计算）、`07/08` 论坛
  RSS 提示与两篇正文。测试 **574/574 全绿**（+115：MTR 解析/时刻表展开/站台分组/Dijkstra/步行
  换乘/服务器路径整形，论坛列表/错误信封/RSS/渲染页/HTML 转文本），构建 0 错 0 警。
  下一步：M4 发布打包与真机验收。
- **2026-11-18 M4 补丁：站台卡片化 + 上下行合并**：① 车站「按站台」不再把每班车画成独立卡片，
  改为**每个站台一张卡片**（标题「站台 X · 最近 3 班」，对齐 QL-HMCL 的 `TRAINS_PER_PLATFORM=3`），
  卡内按到站时间排最近 3 班，行间用细分隔线，右侧为到站分钟/实时/终点站胶囊；服务器忽略上限时
  客户端再截断。切站/刷新时滚动回顶部，首张卡片标题不再被裁。② 线路数据新增分组键
  `QlMtr.RouteGroupKey` = 中文主名 + 服务编号，把 MTR 拆开的上下行/同服务方向合并成一条展示项：
  车站「按线路」每个线路组一张卡片、卡内逐行列方向（终点/站数/方向后缀/本站站台/下一班）；
  线路列表一项一条线路（如「[WIP]荃湾快线 快速 · 2 个方向 · 21 站」），右侧用方向页签（N/S 等）
  切换站序，不再把方向一股脑铺成多行。真实数据 155 个方向合并为 71 条线路/服务。截图
  `ql-tmp/e2e-m4/14-platform-cards.png`、`15-station-route-group.png`、`16-route-group.png`。
  测试 **577/577 全绿**（+3：分组键上下行一致/不同服务不同键/英文名忽略方向段），构建 0 错 0 警。
- **2026-11-18 M4 补丁：WebView2 地图 Unknown 导航失败排查**：现象是地图页 100–200ms 内报
  `NavigationCompleted(Unknown)` 并切失败面板；排查结果：MTR 地图服务直连 HTTP 200、Edge 同版本
  Chromium 能正常加载、清空 `data/webview2` 缓存仍失败，排除服务端与缓存。根因是 WebView2 生命周期里
  早期/被中止的导航完成事件被当成地图结果：旧代码一收到失败就 `Web.Visibility=Collapsed`，反而把
  真正的地图导航一起中止，形成“越失败越隐藏、越隐藏越失败”的循环。改为只认发往地图 URL 的本次
  导航（忽略 `about:blank` 等非地图事件）、加 15s 超时兜底，并记录 `开始导航/导航完成 source=`；
  日志实证 `开始导航: http://mc.example.com:14614/` → `成功 source=http://mc.example.com:14614/`，
  地图恢复内嵌渲染（`ql-tmp/e2e-m4/01-map.png`）。构建 0 错 0 警。
