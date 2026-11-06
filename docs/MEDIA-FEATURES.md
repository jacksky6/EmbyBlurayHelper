# 原盘音视频规格识别

该功能在 Emby 扫描新加入的 BDMV、Blu-ray ISO 电影时，补充经过真实码流验证的 Dolby Atmos 和 Dolby Vision 信息。结果写入 Emby，后续详情页读取已保存的信息。它与原盘语言补全、Infuse 播放兼容分别管理。

## 使用

1. 使用包含本功能的 Helper，以及 `EmbyBlurayHelper.Plugin.dll` 0.1.4 或更新版本。安装或更新 DLL 后重启 Emby；批量核验需要此版本提供的增强探测凭据检查。
2. 在「连接与账号」保留 **Emby 原版 ffprobe** 的路径。
3. 打开左侧一级菜单「音视频规格」，填写 **独立增强 ffprobe** 路径。它必须是另一份支持 Atmos / Dolby Vision 元数据的现代 ffprobe，不能填写原版路径或 Helper 自身。
4. 点击「检查增强 ffprobe」，开启规格识别。页面会读取实际插件开关、探测器接入状态和文件可访问性。
5. 如需 Emby 音轨名称直接显示 Atmos，勾选「Emby 显示原始轨道标题」并保存。这是 Emby 原生的全局选项，也会让其他影片使用其原有的音轨、视频和字幕标题。

以后新影片由 Emby 入库扫描处理，不需要播放触发，也没有定时轮询整个媒体库。这里的“自动”以 Emby 实际发现并刷新新项目为前提；是否实时发现文件由 Emby 的媒体库监控、计划扫描配置决定。

开启功能不会立即重扫已有影片。已有原盘可以在「单片检查」输入影片 ID，点击「识别并保存规格」；显式完整刷新元数据也会进入扫描扩展。关闭功能保留已保存的规格，不清理媒体数据库，也不会自动关闭 Emby 的原始标题显示选项。

## 按媒体库批量处理已有原盘

在「媒体库处理」中勾选「音视频规格」：


1. 点击「检查媒体库」。这里只读取 Emby 已保存的信息，不扫描视频码流。
2. 勾选所需媒体库，或全选。可以查看每库的原盘数、批量已核验数、待核验数；同一电影跨库只提交一次。
3. 点击「补全所选媒体库」。任务在后台逐个媒体源处理，显示当前影片、进度、成功、失败和跳过数量。多版本影片只处理其中的 BDMV / ISO，普通 MKV、MP4、独立 M2TS 不进入批量计划。

“待核验”表示没有有效的本批量模块核验记录，并不代表影片一定缺少 DV 或 Atmos。正常检测但样本未确认额外标记，也会记录为已核验。第一次批量处理可能再次核验曾经手动或自动处理过的电影，这与自动入库的计数分别统计。

默认只处理尚未核验的媒体源；需要重做时勾选「重新核验已完成的原盘」。已完成结果保存在 `data/media-feature-task.json`，重新检查会核对引擎文件、媒体源和已保存的规格，仍有效的记录会保留。更新增强引擎、媒体源改变或规格被其他操作覆盖后，可重新检查生成计划。任务记录不保存 Emby API key。

切换页面、关闭浏览器不会停止后台任务；仅任务运行且本页面可见时，每 2 秒读取一次进度。可手动停止后继续未完成的媒体源；重启期间的任务会标记中断，可以继续。已交给 Emby 的探测可能仍会完成，重试时仍会核对保存结果。日志模块为 `MediaFeatureBatch`，包含逐影片、逐媒体源的实际规格及读回核验结果。任务记录保存失败会明确提示，不假装支持恢复。

每次规格探测都会由 wrapper 在 Emby 账号的临时目录写入核验凭据。插件 0.1.4 在保存前检查凭据时间、源文件标识及所有增强子探测是否成功；超时或失败不会被算作“没有新增规格的成功检测”。任务还会读回媒体源，核对返回的规格和原有轨道、语言、时长等信息后才计入成功，不需要循环等待 15 秒。

状态在进入页面、修改设置或手动刷新时检查。自动处理记录在 Emby 日志的 `MediaFeatures`；手动核验和配置记录也写入 Helper 的运行日志。源文件只读。

## 两份探测器的边界

| 场景 | 使用的探测器与行为 |
| --- | --- |
| 基础编码、声道、帧率、语言、时长等 | 仍调用用户安装的 **Emby 原版 ffprobe**；已有 BDMV/ISO 读取器负责选择正片并补充导航信息。 |
| 本功能开启时的蓝光 JSON 探测 | 原版成功后，对所选正片额外调用 **独立增强 ffprobe**；仅合并确认的 Atmos / DV 信息。 |
| 普通 MKV/MP4、非 JSON、版本查询 | 原版行为透传，不调用增强探测器。 |
| 播放、转码 | 继续使用 Emby 原有流程，不用增强 ffprobe 代替播放或转码程序。 |
| 增强探测失败、超时或没有证据 | 保留成功的原版结果；失败记录原因，不编造 Atmos / DV。 |

Emby 的自定义探测器入口仍指向 Helper，这是既有语言补全的接入方式。Helper 内部首先运行原版 ffprobe，**并未把基础探测切换到新版 ffprobe**，也不覆盖 Emby 的 ffprobe / ffmpeg 文件。仅启用规格识别不会顺带开启语言补全；两者共用入口时，恢复原探测器前需要先关闭规格识别。

增强引擎采用独立路径，不随当前这份开发版本自动下载。Windows 填 `.exe` 的完整路径；Linux、DSM、Docker、macOS 填对应系统和架构的可执行文件路径，并保证运行 Helper / 探测器的账号可执行。共享动态库版本还需要它自己的依赖；不能直接在 DSM 上填写 Windows 路径。目前这轮真实集成验证仅在 Windows 本地 Emby 4.10.2.0 完成，未更新 109 上的 SPK。

## 识别与保护规则

- TrueHD、E-AC-3 必须由增强探测器读到 Atmos profile。不会根据首条音轨、7.1 声道或文件名判断 Atmos，同 PID 的 AC-3 core 也不会继承 TrueHD 的 Atmos 标记。
- 普通 DV 使用真实 DOVI 配置。UHD 双层 Profile 7 需要标准基础层与增强层组合，并从增强层实际解码帧中读取 RPU 元数据；结合残差标记区分 FEL / MEL。仅存在两路 HEVC 不足以判定 DV，不伪造未知 DV level。
- 按 PID 与编码唯一匹配原版探测流。写回已有影片时还核对 Emby 流类型、索引及音频声道；保留原始编码、尺寸、声道、语言、时长、轨道顺序、外部字幕和用户已有标题。
- 使用 Emby 原生 `ExtendedVideoType` / `ExtendedVideoSubType` 保存 DV，并补充 Atmos profile；缺少标题时才生成规格备注。已有自定义标题会保留，因此开启原始标题显示也不保证每条用户自定义标题都包含 Atmos。
- 新 ISO 如果 Emby 尚未生成媒体流，则用原版探测结果建立基础信息，再附加扩展规格和缺失时长。

额外读取按需进行，不解压完整 ISO、不生成 remux 文件、不编码音视频。增强探测总超时 20 秒，包含基础流检查以及必要的短段增强层帧分析。结果缓存包含源文件标识、原始流信息和增强引擎版本文件标识；有证据与无新增证据的成功结果都缓存。缓存默认在系统临时目录的 `emby-bluray-helper-features/<账号>`，系统清理缓存后可重新检测。

插件保存新项目的文件标识，正常重复扫描未变化的项目会跳过。显式完整刷新可以重新核验。短段探测不能保证涵盖每张盘所有可能的特殊封装；未识别到额外标记应理解为“样本未确认”，不能据此证明整片不存在该规格。识别结果也不等于客户端具有该格式的播放能力。

## 模块

| 文件或目录 | 职责 |
| --- | --- |
| `ProbeWrapper/MediaFeatures/MediaFeatureEnricher.cs` | 独立引擎启动、有限读取、超时、缓存、失败回退。 |
| `ProbeWrapper/MediaFeatures/MediaFeatureMerger.cs` | 从真实流/帧提取证据；按 PID 合并附加字段。 |
| `EmbyPlugin/MediaFeatures/AutomaticMediaFeatureProvider.cs` | Emby 入库后处理扩展，检测新项目、跳过未变化项目。 |
| `EmbyPlugin/MediaFeatures/FeatureStreamMerge.cs` | 写库前保护既有流、语言、技术字段和自定义备注。 |
| `App/MediaFeatureService.cs` | 管理端设置、实际状态、独立引擎检查和单影片核验。 |
| `App/MediaFeatureCatalog.cs` | 媒体库分页、BDMV/ISO 范围、媒体源签名及保存结果的读回校验。 |
| `App/MediaFeatureBatch.cs`、`MediaFeatureBatchModels.cs` | 独立批量任务、分库选择、去重、取消/继续、持久化进度。 |
| `ProbeWrapper/MediaFeatures/MediaFeatureAttempt.cs`、`MediaFeatureReceipt.cs` | 汇总本次所有增强子探测的结果，为 Emby 插件提供新鲜的核验凭据。 |
| `App/LibraryPipeline.cs`、`App/Web/library.js` | 「媒体库处理」：合并三项检查结果，按顺序在后台执行所选补全。 |
| `Web/index.html`、`Web/app.js` | 独立一级页面与开关，不执行媒体解析。 |

上述路径均相对于 `src/EmbyBlurayHelper.` 对应项目目录。规格证据解析、Emby 写库、管理页面与 Infuse 代理保持各自边界。

## 本机验证：2026-11-01

环境：Windows x64、.NET SDK 8.0.427、Emby 4.10.2.0。原版 ffprobe 为 `5.1-emby_2023_06_25_p6`，增强版本为 `N-124401-g17734f6967-20260506`。

| 样本 | 核验结果 |
| --- | --- |
| 《奇异博士2》，本机 ID 5 | 视频样本为 HDR10，未确认 DV；PID 0x1100 为 TrueHD + Atmos。26 条流保留；开启原始标题后 API 的 DisplayTitle 含 Atmos。 |
| 《布达佩斯大饭店》，本机 ID 13 | 实际读取到增强层 RPU，确认 DV Profile 7 FEL；首条音轨保持 DTS-HD MA。11 条流保留。 |
| 新增《禁闭岛》ISO 测试库 | 入库自动生成 10 条流及约 2:18:04 时长，无需点击单片核验。 |
| 新增有限长度的布达佩斯 BDMV 样本 | 入库自动识别 DV 7 FEL；样本只保留 64 MiB 媒体数据，其数秒时长不是完整影片时长。 |
| 重启、开关切换后的重复增量扫描 | 完成扫描，新增自动规格检测次数为 0。 |

真实浏览器检查覆盖登录、开关关闭/重新开启、原始标题设置、无效增强引擎拒绝、桌面/手机布局和 JavaScript 错误。测试库和临时盘符在验证后移除，原 NAS 媒体未修改。

批量功能追加验证：本机 31 部电影中只有 **13 部 BDMV、2 部 ISO** 进入计划，15 部全部通过保存和读回核验，失败 0、跳过 0；耗时约 24 秒（包含已有缓存）。部分原盘读到 DV 7 FEL / Atmos，另一些只保留原规格，均有独立的核验状态。普通文件未探测。

批量模块已通过 37 项范围、分页、分库选择、跨库去重、多版本、失败/无凭据、停止继续、持久化和引擎变化检查；新增页面通过 19 项交互检查。管理程序总计 120 项检查，探测核心的规格与凭据检查总计 22 项，插件流保留和格式限制检查总计 21 项；原有 65 项语言任务与 37 项页面回归继续通过。

构建与针对性检查示例（仓库根目录）：

```powershell
dotnet build src/EmbyBlurayHelper.EmbyPlugin -c Release -o artifacts/plugin
dotnet publish src/EmbyBlurayHelper.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/checks/iso-app-batch-updated
dotnet run --project tests/EmbyBlurayHelper.Probe.Tests -c Release
dotnet run --project tests/EmbyBlurayHelper.EmbyPlugin.Tests -c Release
dotnet run --project tests/EmbyBlurayHelper.Features.Tests -c Release
dotnet run --project tests/EmbyBlurayHelper.App.Tests -c Release -- artifacts/checks/iso-app-batch-updated/EmbyBlurayHelper.exe
node tests/web/library-language.test.cjs
node tests/web/media-feature-batch.test.cjs
```

页面测试需要 `jsdom`，本地临时依赖目录为 `artifacts/ui-check/node_modules`。
