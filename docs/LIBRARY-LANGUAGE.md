# 原盘媒体信息批量检查与补全

在 Web 控制台左侧的「Emby 探测语言补全」一级页面中使用。

1. 先保存 Emby 地址和管理员 API key，点击「检查媒体库」。检查分页读取 Emby 已保存的媒体信息，不逐部读取原盘视频。
2. 查看总计和各媒体库的电影数、原盘数、信息完整、语言缺失和信息不足的数量。影片明细显示各原盘版本的时长，信息不足卡片另列缺时长的影片数。勾选需要处理的媒体库，或勾选「全选媒体库」。下方影片明细同步限定到所选库，可分别查看音轨、字幕缺失数量，按待补全或异常筛选。
3. 开启上方「Emby 探测语言补全」，确认显示「已接入 Emby」。ISO 另需通过页面中的前置检查：插件已加载且具备时长能力、原版 ffprobe 提供 HTTP/TCP 输入协议，页面显示「内置 ISO 读取」。未安装 DLL、文件不可读或安装后未重启会提示未加载；插件过旧会提示更新并重启。点击「补全所选媒体库」处理所选库中语言缺失、时长缺失和信息不足的原盘，完整影片自动跳过。ISO 条件不满足时，按钮改为「仅补全 BDMV」并显示不提交的 ISO 数量；纯 ISO 计划不能启动。取消全部选择时不能启动任务。
4. 页面显示计划数、已处理数、当前影片，以及成功、仍有缺失、失败或未核验、跳过的数量。任务运行且该页面可见时每 2 秒读取进度；进入页面和手动刷新也会读取。空闲时不会周期检查媒体库或探测器。

任务在工具后台运行，关闭浏览器或切换页面不会中止。可以停止任务，之后继续补全；继续前会重读媒体信息，避免重复处理已经完成的影片。BDMV 最近 15 秒内已成功提交的刷新会先继续等待核验，不立即重复提交；ISO 探测同步返回，完成后立即核验。停止不能撤销 Emby 已收到的请求。

## 统计口径

- 仅统计 Emby 电影库中的 BDMV 文件夹或 Blu-ray ISO，支持 UHD 和 1080p；MKV、普通视频、DVD 和剧集不在本次批量任务范围内。
- 从 `/Library/VirtualFolders` 读取媒体库，使用各库 `ItemId` 作为 `ParentId` 分页检查电影。根据媒体源的 `Container=bluray`、`Container=iso`、`Container=blurayiso`、`VideoType=Bluray` 或 `IsoType=BluRay` 识别，多个版本只检查其中的原盘源；按电影计数。没有电影或原盘的库也会列出。
- 各库独立统计，总计、所选计划按 Emby 电影 ID 去重。共享电影在多个库出现时只刷新一次，因此各库数字不一定能直接相加。任务启动前重新读取所选库的当前电影 ID；移出范围或媒体库已删除时会跳过或停止，不按旧计划误提交。
- 只检查内部 Audio / Subtitle 的 `Language`。空值、空白、`und`、`unk`、`unknown`、`undefined` 视为缺失；外部字幕和外部音轨不计入。
- 没有内部音轨、缺少媒体源路径或缺少有效时长的原盘归为「信息不足」。时长逐个媒体源检查，主影片的时长不能替代另一个版本的时长；只有语言和各版本时长均有效时才计入「信息完整」。
- ISO 原盘与文件夹原盘进入同一个补全计划。所有平台统一内置 UDF/M2TS 读取：小型播放列表、CLPI 元数据临时提取后删除；ISO 内媒体通过临时 localhost HTTP 按需提供给原版 ffprobe，不挂载、不整盘解压、remux 或转码。选择最长完整无循环主播放列表（支持显式 `-playlist`）；语言按同一播放列表的 PID 匹配。保留 DV 增强层、HD/core 音轨，时长来自完整播放列表。无法确定语言会保留缺失；预期轨道未读到则报错，不切换原生蓝光路径。
- 历史数据不能可靠证明一部电影“从未探测”。这些数量反映上次检查或核验时的当前语言字段。只有本工具已核验的任务才带有本工具的核验时间，相同媒体源重新检查时保留此记录。

## Emby 如何重新探测

逐部调用：

```text
POST /emby/Items/{Id}/Refresh
  ?Recursive=false
  &MetadataRefreshMode=FullRefresh
  &ImageRefreshMode=ValidationOnly
  &ReplaceAllMetadata=false
  &ReplaceAllImages=false
body: {"replaceThumbnailImages":false}
```

未安装新版 DLL 时，上述接口继续用于 BDMV 文件夹。插件 0.1.2 暴露 `bdmv-direct-probe` 后，批量补全优先通过插件直接探测 BDMV，绕过 Emby 自带 BDInfo 对异常播放列表的全盘扫描，不刷新标题和图片。单独构造 BDMV/index.bdmv 探测目标，播放媒体源仍保存原来的文件夹路径。正常 BDMV 和 ISO 复用同一套按需 M2TS 探测、轨道校验和 MPLS 时长处理。只有导航不可读且实际仅有一个 M2TS 时，才直接读取该文件真实的语言及时长，不根据文件名猜语言，也不修改原盘导航文件。

Emby 4.10.2.0 的普通 Default 刷新会根据文件是否变化筛选提供器，不能保证重新调用 ffprobe。FullRefresh 可用于文件夹原盘，但实测 ISO 即使 FullRefresh 也可能完全不调用 ffprobe，不能用 204 响应判断 ISO 已探测。

这是 Emby 的元数据刷新接口，没有公开的“只刷新媒体语言”接口。工具明确关闭替换现有元数据、图片和缩略图的选项；Emby 仍可能运行启用的其他元数据提供器、填充空字段，或刷新同一电影的其他媒体版本。不会直接写 Emby 数据库或修改原盘文件。

BDMV 刷新返回 204 只代表已排队，工具每 1 秒重读媒体信息，最多等待 15 秒。ISO 改为调用随附的小型 Emby 插件：

```text
GET  /emby/BlurayHelper/Status
POST /emby/BlurayHelper/Probe
body: {"ItemId":"1885","MediaSourceId":"mediasource_1885"}
```

ISO 时长补全需要插件 0.1.1 或更新版本，管理页会核对 `missing-runtime` 能力。旧插件可以继续提供原有功能，但不能被算作已支持时长补全。应用升级后先重新检查电影库，以更新旧计划中的字段。

异常 BDMV 直接探测、旧 ISO 播放列表修复需要随包附带的插件 0.1.2。导航选择按同一 M2TS 的时间区间检查重复，过滤反复播放短片的菜单，允许同一 M2TS 的不重叠片段及正片前后很短的重复标志。原盘写 `und` 的语言保留为未识别；直接探测同步结束后立即反馈缺失，不再等待完整核验窗口。

升级至 0.2.0-16 后重新检查电影库，旧任务记录中曾核验成功、读取器修订号尚未更新的 ISO 会标为「旧版 ISO 播放列表需重新核验」。只有用户启动补全时才重读，并显式发送 `RepairPlaylist=true`。这可修正旧菜单造成的超长时长和不对应的内部轨道，保留外部字幕；若新旧时长一致，仍合并保存的语言和自定义轨道标题。该修复不会自动在启动时运行。成功后的读取器修订号会持久保存，后续检查不重复修复。

插件安装：将 `emby-plugin/EmbyBlurayHelper.Plugin.dll` 复制到 Emby 数据目录下的 `plugins`，重启 Emby。实际数据目录以 Emby 控制台显示的路径为准；本机测试目录是 `D:\GIT\Emby插件\programdata\plugins`。只复制插件 DLL，不复制 Emby SDK 或其他依赖 DLL。安装或更新后点击 ISO 区域的「重新检查」。API 只能确认插件是否实际加载，不能仅凭端点不存在区分未安装、权限错误和待重启。未安装插件的服务器仍可批量补全 BDMV。

前置检查不读取电影；进入补全页、保存设置、检查电影库完成或手动刷新时检查，不按任务进度定时执行。任务开始前独立检查一次，不信任浏览器里缓存的就绪状态。条件不满足时统一跳过计划中的 ISO、记录警告及逐部未提交原因，BDMV 继续；即使直接调用补全 API，也不会将这些 ISO 发送给 Emby 探测。

插件接口要求管理员权限，通过 Emby 自身的 `IMediaProbeManager` 调用已接入的探测器，再用 Emby 的 `IItemRepository.SaveMediaStreams` 保存轨道，用 `ILibraryManager.UpdateItems` 填补源所属版本缺失的时长、大小；禁用 metadata writers，不直接操作数据库文件、不刷新电影标题或图片。已有有效时长和大小保持不变，ISO 大小使用整个文件的长度。多个原盘版本逐个按媒体源 ID 处理，写入对应版本的记录；保留已知语言、自定义标题及外部字幕。缺少原有内部轨道或探测结果不完整时拒绝覆盖。ISO 请求最多等待 5 分钟，正常返回后立即读取 Emby 数据核验，仍有缺失则标记部分完成。

只有原有蓝光媒体源和内部轨道仍可匹配、所有内部音轨和字幕均有语言、且每个原盘版本均有有效时长时才计入成功。轨道消失、媒体源变化不会被当成成功。ISO 不再依赖原版 ffprobe 的 libbluray/蓝光协议；已通过 Windows 和 WSL Linux 配合官方 DSM 原版 ffprobe 的真实 ISO 测试，NAS 上升级安装后的结果仍需真机核验。不要用通用 ffprobe 覆盖 Emby 原文件。

开始及每部提交前会核对探测器仍接入本工具；连接、服务器身份和探测配置变化会停止任务。不会自动接入探测器或修改其开关。

## 记录与日志

最新计划、每部结果和核验时间保存在数据目录的 `library-language-task.json`，不保存 API key，只保存连接指纹。重启时将未完成任务标记为中断，保留可继续的计划，不会自行重新提交。升级使用原数据目录即可保留。

运行日志中的 `LibraryLanguage` 模块记录前置检查警告、检查结果、每个媒体源的音轨/字幕编号与当前语言、逐部提交前后的语言对应关系、时长、核验、停止及结束统计。Emby 请求失败会记录 HTTP 方法、接口路径、状态码和脱敏后的错误摘要，区分 BDMV 刷新与插件 ISO 探测。探测器 stderr 还会输出从 BDMV/ISO 读取的 PID 语言表及 ffprobe 流匹配结果。日志仍沿用原来的保留及轮换规则。清除运行日志不会清除这个任务记录文件。

任务记录和日志包括所选媒体库的范围；页面同时显示总体进度和各媒体库的本次处理进度。启动后范围固定，停止或完成后可以更换媒体库继续补全。

## 开发验证

```powershell
dotnet run --project tests/EmbyBlurayHelper.Library.Tests -c Release
dotnet run --project tests/EmbyBlurayHelper.EmbyPlugin.Tests -c Release
dotnet build src/EmbyBlurayHelper.EmbyPlugin -c Release -o artifacts/plugin
dotnet publish src/EmbyBlurayHelper.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/batch-checks/win-x64
dotnet run --project tests/EmbyBlurayHelper.App.Tests -c Release -- artifacts/batch-checks/win-x64/EmbyBlurayHelper.exe
npm install --prefix artifacts/ui-check jsdom@26.1.0 --no-save --package-lock=false --ignore-scripts
node tests/web/library-language.test.cjs
```

任务测试使用短等待窗口模拟 Emby 排队和超时，覆盖分页、媒体库选择、跨库去重、范围重新核对、多版本、未知轨道、ISO、外部字幕、失败、取消、继续、重启和连接变更。另有插件轨道保留、时长保护、仅缺时长的多版本计划检查，以及真实 ISO 的 PID/语言测试。BDMV 生产等待窗口为 15 秒，ISO 同步返回后不继续空等。
