# 模块结构与维护边界

## 程序边界

| 项目 | 职责 |
| --- | --- |
| `EmbyBlurayHelper.ProbeWrapper` | 调用 Emby 原版 ffprobe；为 BDMV/ISO 选择正确探测输入，从实际播放列表、CLPI 中补齐流语言。返回媒体探测结果。 |
| `EmbyBlurayHelper.EmbyPlugin` | 运行在 Emby 内，显式探测 BDMV/ISO，并通过 Emby 接口保存媒体流和技术信息。 |
| `EmbyBlurayHelper.App` | 独立辅助程序：Web 控制台、配置、日志、探测器接入管理、媒体库批量任务。复用探测和代理核心。 |
| `EmbyBlurayHelper.Proxy` | Infuse 播放兼容：按需读取 BDMV 播放列表、修改 MPEG-TS 语言描述。和媒体信息入库分开。 |

ISO 的信息补全写入 Emby 后由详情页、媒体库 API 和 PlaybackInfo 使用；它不依赖 Infuse 代理，也不改变 ISO 文件。Infuse 对完整 ISO 的原生播放是播放器自己的能力。

## ISO 跨平台读取

ISO 在 Windows、Linux、DSM 共用内置 UDF/M2TS 路径，不根据操作系统切换蓝光协议实现，也不再保留原生 `bluray:` 重试。原版 ffprobe 只负责其擅长的 MPEG-TS 编码识别，必须具备 HTTP/TCP 输入协议。

| 文件 | 职责 |
| --- | --- |
| `ProbeProcess.cs` | JSON ISO 探测进入统一读取器；普通媒体、非 JSON、功能关闭时透传。失败返回非零退出码。 |
| `IsoDiscMetadata.cs` | ISO 只提取小型导航文件，BDMV 直接读导航；BDInfo 扫描 CLPI/MPLS。按实际时间区间过滤重复菜单，选择最长完整无循环主播放列表，允许同一 M2TS 的不重叠片段。 |
| `IsoMediaHttpSource.cs` | 仅监听临时 localhost 端口，随机 URL 提供单个 ISO 成员的 GET/HEAD/Range；有连接、超时和读取量限制，结束后关闭。 |
| `IsoMediaProbe.cs` | ISO/BDMV 输入适配器和共用片段探测流程；仅缺 PID 时追加其他片段。保留 DV 增强层及 HD/core，校验预期 PID，合成整片时长、章节、源大小。BDMV 导航不可读且实际只有一个 M2TS 时，读取其真实技术信息。 |
| `BluRayLanguageAugmenter.cs` | 以同一播放列表按 PID 补缺语言，保留已知语言，分别记录音轨与字幕对应关系。 |

导航文件在临时目录中使用后删除，M2TS 不落盘。媒体样本每片段累计最多读取 256 MiB，总探测截止 2 分钟；正常样本远小于上限。读取不完整、加密或不支持的镜像不会伪造流后写库。主片选择是最长完整播放列表的启发式规则，有多个相似时长版本时会记录备选数量；不能承诺破解混淆播放列表或自动识别导演剪辑偏好。

## Emby 插件内部

| 文件/目录 | 职责 |
| --- | --- |
| `ProbeRequests.cs` | 路由和管理员鉴权声明、请求字段。 |
| `ProbeBluRayMediaService.cs` | API 入口、串行探测控制、组织调用、结果日志；不承担字段合并策略。 |
| `Media/BluRaySourceResolver.cs` | 识别蓝光来源，核对媒体源与所属版本，防止把另一个版本的信息写到主影片。 |
| `Media/BluRayMediaProbe.cs` | 通过 IMediaProbeManager 调用探测器，验证视频/音轨列表。 |
| `StreamMerge.cs` | 流匹配、保留已知语言和自定义备注、保留外部字幕、拒绝轨道错配。 |
| `Media/MediaFacts.cs` | 时长和大小的补缺策略；有效已有值优先，不用空值或零覆盖。 |
| `Persistence/BluRayMediaWriter.cs` | 探测后再次核对版本，调用 Emby 保存接口；ISO 大小读取完整 ISO 文件长度。 |
| `Media/SavedBluRayMedia.cs` | 保存结果、API 输出和轨道/时长日志格式。 |

时长保存在源所属 item 的 `RunTimeTicks`，Emby 再由它生成该版本的 `MediaSourceInfo.RunTimeTicks`。写库使用 Emby 自身接口，不操作 SQLite 文件。技术字段更新禁用 metadata writers，不触发 NFO、标题或图片写出。媒体流和 item 字段属于两次保存；中途失败返回失败，批量任务重新读回实际结果，不能只依赖插件响应计成功。

插件 0.1.2 的 `bdmv-direct-probe` 能力决定是否绕过 Emby 旧 BDInfo 刷新。BDMV 仅为本次探测新建 index.bdmv 输入目标，保存和播放仍使用原文件夹。`playlist-repair` 允许显式修复旧 ISO 选错菜单的轨道及时长；批量计划保存读取器修订号，旧成功记录在重新检查后进入一次性修复计划，用户启动补全才执行。普通补缺继续保护已有有效字段和用户轨道标题。

## 批量检查与页面

| 文件 | 职责 |
| --- | --- |
| `LibraryMediaModels.cs` | 可持久化任务、媒体库、影片、媒体源和轨道模型。 |
| `LibraryMediaAnalyzer.cs` | 纯数据分析：读取已保存的 Emby 信息，统计语言和各版本时长缺失；不访问磁盘、不启动探测。 |
| `LibraryLanguageTask.cs` | 媒体库范围、分页、计划、取消/继续、逐源提交、重新读取和核验、进度与任务日志。 |
| `EmbyJsonContent.cs` | Emby 控制请求的 JSON 格式兼容；保留 PascalCase，使用缓冲内容提供 Content-Length。 |
| `IsoPrerequisites.cs` | 检查已加载的插件端点、能力及原版 ffprobe 输入协议；独立于影片探测，不安装插件或重启服务。 |
| `EmbyRequestFailure.cs` | 失败接口与有限长度响应摘要，脱敏后用于任务结果及日志。 |
| `Web/index.html`、`Web/app.js`、`Web/app.css` | 页面内容、交互、样式；显示每个版本时长和任务状态，页面不执行原盘解析。 |

功能入口是左侧一级菜单：Emby 探测语言补全、音视频规格、原盘章节、Infuse 播放。时长放在原盘媒体信息批量功能内，共用范围选择和任务进度。

## 音视频规格

`ProbeWrapper/MediaFeatures` 只补充从码流确认的 Atmos 和 Dolby Vision。基础探测仍调用 Emby 原版 ffprobe，独立现代 ffprobe 负责追加流/帧证据；二者不替换文件或共享技术字段覆盖策略。增强探测有单独超时、证据缓存和失败透传边界。

`EmbyPlugin/MediaFeatures/AutomaticMediaFeatureProvider.cs` 实现入库后的元数据扩展。`FeatureStreamMerge.cs` 负责既有流的保护，自动检查状态保存在插件配置。管理端 `MediaFeatureService.cs` 负责独立开关、引擎检查、原生标题显示配置和单影片核验，播放代理不参与。使用方式、限制和真实样本验证见 [原盘音视频规格识别](MEDIA-FEATURES.md)。

`MediaFeatureBatch.cs` 与 `MediaFeatureBatchModels.cs` 独立维护规格批量计划，不复用语言是否缺失的判断。`MediaFeatureCatalog.cs` 只收集 BDMV / ISO 媒体源，负责分页、源签名和写入后的读回校验；`Web/media-features.js` 负责按库选择和进度交互。默认记录成功的正、负识别结果，避免把普通 HDR10 / TrueHD 反复判作缺失。规格任务和语言任务分别持久化。

wrapper 的 `MediaFeatureAttempt` 汇总每个片段的增强探测结果，`MediaFeatureReceipt` 在 Emby 账号的临时目录写入源标识、结果及时间。插件 0.1.4 保存前必须取得本次成功凭据，防止增强失败的原版透传结果被标记为“已核验”；基础探测仍保留失败透传行为。

新增媒体字段时，在探测/补缺规则中定义来源与保护策略，再加入分析、任务核验和页面显示；新增播放兼容功能应放在代理侧，避免混入显示信息保存流程。探测器集成、账号和日志管理继续使用各自现有模块。

## 原盘章节

`ProbeWrapper/DiscChapterReader.cs` 只读取 BDInfo / UDF 导航信息。`IsoDiscMetadata.SelectChapterPlaylist` 复用合法非循环列表筛选，按 Emby 保存的片段总大小和时长匹配版本；同条件的候选章节不同则拒绝猜测。统一可执行文件提供 `--helper-chapters <path> [runtimeTicks] [sourceSize]` JSON 子进程协议，不访问 Web 私有配置或 API key，不调用 ffprobe。

插件 `Chapters/ChapterProcess.cs` 负责 Emby 账号下的限时调用及协议验证；`NativeChapterMerge.cs` 保留用户章节和非章节标记；`ChapterWriter.cs` 使用 Emby 的 GetChapters / SaveChapters，校验媒体源所有者、时长边界、并发编辑和写后读回。`AutomaticChapterProvider.cs` 在入库元数据后处理阶段运行，不在播放时重复解析。

自动提供器同时把保存后的列表放回 `MetadataResult<Movie>.Chapters`：Emby 在所有提供器结束后还会提交这个字段，仅提前调用 SaveChapters 会在刷新收尾被覆盖。读取失败时也携带已有章节/标记，避免最终提交清空；验证必须在 `RefreshItem Complete` 后重新读取，不能只看提供器的成功日志。

App `Chapters/NativeChapterService.cs` 负责独立配置和插件确认；`NativeChapterBatch.cs` 使用只读目录查询收集 BDMV / ISO，独立持久化任务状态、范围和签名。Web `chapters.js` 处理一级页面及仅在活跃任务可见时的进度刷新。功能不触及音视频流、原版探测器、NFO 和缩略图设置。详见 [原盘章节](NATIVE-CHAPTERS.md)。

## 原盘章节截图（跟随 Emby）

`EmbyPlugin/Thumbnails` 与章节时间点、规格补全分离。`DiscThumbnailPolicy` 读取库的既有截图设置；`AutomaticDiscThumbnailProvider` 仅在扫描模式提取；`DiscThumbnailEntryPoint` 监听原生缩略图任务成功完成并排队无默认触发器的 `DiscThumbnailTask`，展示补充进度和取消。

`DiscMediaSession` 调用只读 `--helper-thumbnail-session` 协议。BDMV 用真实 M2TS，ISO 用临时 loopback Range 流，按 MPLS/CLPI 映射时间。`EmbyImageExtractor` 绑定 Emby 原生单帧提取实现，显式选择主视频，核验 HDR 色调映射；`DiscThumbnailColorPolicy` 拒绝不安全的 HDR/DV 输出。`DiscThumbnailService` 合并图片并保留章节、媒体流和自定义图片；固定间隔采用 Emby BIF。详见 [原盘章节截图](DISC-THUMBNAILS.md)。

## 关键验证

- `EmbyBlurayHelper.EmbyPlugin.Tests`：轨道保护，未知/无效技术字段和已有时长保护。
- `EmbyBlurayHelper.Thumbnails.Tests`：原生任务完成监听、失败/取消/关闭门控及无独立默认定时器；截图设置和 HDR/DV 保护在插件纯逻辑测试中验证。
- `EmbyBlurayHelper.Library.Tests`：媒体库范围、多版本、只缺时长、旧插件能力、请求长度兼容、保存结果核验。
- `tests/web/library-language.test.cjs`：一级导航、媒体库选择、时长格式、多版本显示、任务刷新行为。
- `EmbyBlurayHelper.App.Tests`：管理接口、会话、设置和代理基础回归。
- `EmbyBlurayHelper.Features.Tests`：规格批量范围、多版本、负结果、凭据确认、读回检查、取消/继续、计划恢复。
- `tests/web/media-feature-batch.test.cjs`：规格页面的分库选择、去重、重做选项、任务刷新和状态保护。
- `EmbyBlurayHelper.Chapters.Tests`：章节独立配置、分库/跨库范围、既有章节保护、签名变化、保存核验、取消/继续和重启恢复。
- `tests/web/native-chapters.test.cjs`：原盘章节一级页面、选择范围、快速检查结果、进度刷新和插件前提提示。

真实 Emby/ISO 验证记录在 `ISO-LANGUAGE-LOCAL-VALIDATION.md`，109 警告逐片核验和修复见 `109-WARNING-VALIDATION.md`；播放失败研究单独放在 `ISO-PLAYBACK-DIAGNOSIS.md`，不能用探测成功代替实际播放验证。
