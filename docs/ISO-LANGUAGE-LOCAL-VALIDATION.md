# ISO 语言补全：本机验证记录（2026-10-31）

## 当前运行状态

本机仅保留 Emby `8096`、主 Helper 管理页 `8099`、主 Helper 代理 `8097`。两个研究代理 `8107/8117`、multiclip-test、batch-release9、ui-tabs-preview、ui-top-level-preview 已停止，文件和数据保留。未发现这些实例的开机启动项。三个保留入口均返回 HTTP 200。

**新版已部署到本机主实例，服务端语言结果已核验。** 此前重启并替换两个服务的组合命令被工具审核拒绝后，用户于 12:32 运行 `scripts/update-local-test.ps1 -Apply`，完成程序、插件更新和备份。实际集成发现请求字段大小写问题，修复后于 12:36 使用 `-Apply -HelperOnly` 更新 Helper 成功；Emby 和已加载插件继续运行，没有再次重启。DSM 未修改。

## 已验证的路径

此前已安装的最小插件通过 `IMediaProbeManager.GetMediaInfo` 调用已配置的探测器，使用 `IItemRepository.SaveMediaStreams` 保存流。禁闭岛 10 条、青蛇 7 条流已写入本机 Emby，正常重启后依然存在；`PlaybackInfo` 分别返回相同数量的轨道，无 ErrorCode。普通 ISO FullRefresh 没有触发 ffprobe，因此不再依赖它完成 ISO 补全。

此前青蛇音轨 #1 为空。整盘 CLPI 汇总无法确定该 PID 的语言，但 ffprobe 实际选择的 `00002.mpls` 明确提供 `0x1100=zho`。新版在 ISO 探测时读取原版 ffprobe 的 `selected NNNNN.mpls` 日志，按该播放列表提取语言，避免依赖整盘 PID 汇总。BDMV 输入不增加此处理。

## 新版单次真实探测

没有启动额外服务或监听端口。直接运行 staging 中的 EXE，使用原主实例的数据目录和原版 Emby ffprobe，对两张真实 ISO 进行了探测。两次均退出 0；耗时是本次测量值，受缓存和网络影响。

| 影片 / Emby ID | 实际播放列表 | 本次耗时 | 音轨编号与语言 | 字幕编号与语言 |
| --- | --- | --- | --- | --- |
| 青蛇 / 1885 | 00002.mpls | 348 ms | #1 zho、#2 zho | #3 kor、#4 eng、#5 zho、#6 zho |
| 禁闭岛 / 1886 | 00800.mpls | 691 ms | #2 eng、#3 zho、#4 zho | #5 eng、#6–9 zho |

日志分别显示每条轨道的 index、PID、原盘语言和补全状态。禁闭岛 TrueHD / AC3 的两个流共享 PID `0x1101`，均匹配 `zho`；这不是重复索引。单次探测后又通过实际插件接口完成青蛇写库，详情见下节。

## 更新后服务端集成验证

首次通过 8099 批量功能检查 33 部电影，识别到 15 部原盘，只剩青蛇 #1 音轨缺失。提交该片时实际 Emby 返回 HTTP 400，日志明确为 `ItemId is required.`。此前先修正了请求字段的 PascalCase，但后续 14:04 的真实批量验证发现仍有分块 JSON 兼容问题；完整原因和最终修复见下节，不能将此前仅有的大小写修正视为实际端到端已通过。

使用 PascalCase 字段直接调用实际插件返回 HTTP 200，青蛇保存 7 条流，音轨 #1/#2 均为 zho，4 条字幕为 kor/eng/zho/zho。随后修正批量请求序列化，测试桩也改为拒绝 camelCase 请求；40 项任务测试和 101 项应用测试通过。修正版 Helper 已更新到主路径，并核对 SHA256 与构建产物一致。

实际核验范围：

- Emby 普通媒体信息 API 与 PlaybackInfo 均返回青蛇和禁闭岛完整的音轨、字幕语言，无 ErrorCode。
- 以 `Infuse-Direct/8.5.9` 标识通过主代理 8097 请求 PlaybackInfo，两个 ISO 同样返回完整语言，无 ErrorCode；尚未验证真实 Infuse 界面或视频播放。
- 对 SQLite 的 DB/WAL/SHM 制作只读来源的本地快照，复制前后与副本的全部哈希相同。在副本查询 `mediastreams2`，确认青蛇 `(ItemId=1885, StreamIndex=1, Language=zho)` 及两片其他音轨、字幕均已保存。没有打开或修改运行中的原数据库。
- 修正版主 Helper 重新检查全部电影的已保存媒体信息：15 部原盘全部 complete，0 条音轨、0 条字幕语言缺失。此次仅为 API 信息检查，没有触发 Emby 全库扫描或额外探测。
- 当前 Emby PID 966236、Helper PID 1011484，仅监听本项目的 8096/8097/8099。匿名插件状态请求返回 401，管理员 API key 可正常使用。

边界：真实插件写库使用了直接 API 请求；修复后的批量提交格式由严格匹配 Emby 实际字段名的测试覆盖。因为真实数据已经完整，没有为重复制造缺失而清空数据库，因此主控制台本次最终执行的是重新检查，不是另一次补全任务。

## 新版兼容与数据保护

- BDMV 保持原先 FullRefresh 流程，不要求安装新插件；ISO 未安装插件时显示具体安装提示，同一任务仍可处理其他 BDMV。
- 含 ISO 的多版本电影逐个提交缺失信息的蓝光媒体源；插件核对源所属的真实 item，不把其他版本的流写到主版本。
- 插件接口增加管理员鉴权。已知语言、自定义标题、外部字幕保留；缺失原有内部轨道、codec 不匹配或返回重复 index 时拒绝覆盖。
- ISO 探测同步返回后立即读回 Emby 核验，不为盘内没有的语言继续空等 5 分钟；请求本身最长 5 分钟。普通管理 API 保持原超时设置。
- 可移植包和 SPK 的构建脚本已纳入可选插件 DLL；尚未重新生成或发布 DSM 包，也没有自动修改 Emby 的插件目录。
- Windows 的原版 ffprobe 已验证支持 libbluray；DSM 自带版本可能缺少这项能力，DSM ISO 路径仍需单独验证，不能套用本机结论。

## 验证与产物

- 应用：101 项检查通过。
- MPEG-TS / 播放列表：169 项检查通过。
- 批量任务：40 项检查通过，包含 BDMV 无插件兼容及多 ISO 版本。
- 插件流合并：7 项数据保护检查通过。
- 青蛇实际播放列表的 ISO 提取与补写：17 项检查通过；另有上述两张 ISO 的完整真实 ffprobe 输出验证。
- 插件使用 `mediabrowser.server.core 4.9.1.90` SDK 构建，0 错误、0 警告；已在 Emby 4.10.2.0 加载并验证实际探测、源所属校验和流保存。
- WSL Python 3.14.4 和 shell 对打包脚本的语法检查通过，没有安装额外系统工具。

已部署 EXE 的构建产物：`artifacts/checks/iso-app-batch-updated/EmbyBlurayHelper.exe`。

已部署插件的构建产物：`artifacts/plugin/EmbyBlurayHelper.Plugin.dll`。

更新前备份：`artifacts/checks/local-update-20261031-123201-a64a8f`；Helper 字段大小写修复前备份：`artifacts/checks/local-update-20261031-123655-ff997d`；写库后核验快照：`artifacts/checks/iso-after-fill-20261031`。

用户随后验证：Infuse 能播放并显示语言；Emby 客户端播放失败，UHD 提示没有兼容的流。Infuse 对 ISO 的原有能力不能归因于本次语言补全。已另行诊断原生 ISO 读取和失败后的自动回退，详见 [ISO-PLAYBACK-DIAGNOSIS.md](ISO-PLAYBACK-DIAGNOSIS.md)。两片时长和媒体源大小仍有缺项，当前不宣称完整媒体信息或 Emby ISO 播放能力已经解决。

仍待完成：按原生播放要求获取客户端侧失败证据；之后进行 DSM ISO 能力测试。所有后续常驻测试统一使用主实例，不恢复旧预览端口。

## 时长与显示补全（14:02 起）

用户将当前范围明确为信息显示，以 Infuse 原生播放为主。本次插件升级为 0.1.1：保存流后填补缺失的 RunTimeTicks 和 Size；已有有效时长不覆盖，ISO 大小使用整个 ISO 文件长度。由源所属版本的 item 生成影片详情和播放媒体源信息，不修改播放路径。

本机通过统一更新脚本完成应用和插件更新，备份 `artifacts/checks/local-update-20261031-140235-8cd9dd`。批量请求修复后只更新 Helper，备份 `artifacts/checks/local-update-20261031-140637-b6fe91`。

真实检查 33 部电影，15 部蓝光原盘中 13 部信息完整，2 部仅缺时长（禁闭岛、青蛇），0 条语言缺失。首次实际批量提交两片均返回 400，没有计入成功。用相同 .NET 8 HttpClient 对插件做最小对照，确认：

| 请求内容 | Content-Length | 结果 |
| --- | --- | --- |
| `JsonContent.Create`，PascalCase | 无，使用分块传输 | 400，ItemId is required |
| 缓冲后的相同 JSON / StringContent | 52 | 200，返回正确 itemId 和时长 |

最终将这一兼容处理放入 `EmbyJsonContent.cs`，批量测试桩也收紧为拒绝无 Content-Length 的插件请求。不是只改字段名。直接真实请求成功写入两片信息：

| 影片 | RunTimeTicks | 时长 | ISO 大小 |
| --- | --- | --- | --- |
| 青蛇 1885 | 59344583330 | 01:38:54.4583330 | 30536433664 字节 |
| 禁闭岛 1886 | 82846513670 | 02:18:04.6513670 | 60674408448 字节 |

用户详情 API 中 item 与 source 时长一致，青蛇 7 条流、禁闭岛 10 条流和各语言保持完整。上述两片实际写库来自诊断对照中的直接插件请求；不将它描述为修复后批量任务的实际提交成功。任务会重新读回最新数据并跳过已完整影片，避免为制造测试缺项而清空时长。

页面增加每个原盘版本的时长列和缺时长影片数量，“信息完整”需要轨道语言与各媒体源时长都有效。纯分类与持久化模型从任务调度文件拆出；插件的路由、版本校验、探测、字段策略和保存也分别成文件。维护边界见 [ARCHITECTURE.md](ARCHITECTURE.md)。

自动验证：45 项批量任务、13 项插件媒体字段保护、29 项 Web 交互、101 项应用检查通过；插件 SDK 构建 0 警告/0 错误。当前浏览器自动化没有可用浏览器，因此未做本次页面截图检查；显示数据和交互由真实 API 与 DOM 测试核验。

最终持久化核验：Emby 通过自身 Restart 接口正常重启（PID 1014412 → 1008832），插件仍为 0.1.1；两片 item/source 时长、大小及 7/10 条媒体流均保留。带 Infuse 标识通过主代理 8097 请求 PlaybackInfo，同样返回正确时长，0 条音轨/字幕语言缺失。修正版 Helper 重试此前计划时，重新读回数据后正确跳过两部已完整影片，0 失败；再次检查 33 部电影，15 部原盘全部信息完整、0 部缺时长，canFill=false。没有为了测试重新制造缺失数据。

实际管理页面 HTTP 资源包含时长列和多版本时长渲染代码，运行二进制与发布产物 SHA256 一致。仅保留 8096/8097/8099，DSM 尚未部署本次更新。

## 统一内置 ISO 读取（0.2.0-13，18:56 起）

本节记录当前实现，取代上文依赖原生 `bluray:` 输入和解析 ffprobe 播放列表日志的 ISO 探测路径。用户明确要求不预留原生回退，因此所有平台统一通过内置 UDF/M2TS 读取；内置读取失败直接报错，不重试原生 Blu-ray 协议，也不保存不完整的探测结果。BDMV 继续使用已有流程。

DSM 根因已通过官方 Emby 4.10.2.0 DSM 7.2 x86_64 安装包中的原版 ffprobe 复现：没有 `bluray` 输入协议，但有 `http` 和 `tcp`。新版只要求后两项，管理页能力检查也按此判断，不再要求原生 Blu-ray 支持。

ISO 内的小型导航元数据由 DiscUtils.Udf 读取，再用 BDInfo 选择完整主播放列表。对应 M2TS 通过临时 localhost 随机 URL 按需提供给 Emby 原版 ffprobe，编码、声道和 HDR 信息仍来自实际媒体流；MPLS 提供时长、章节和语言映射。无需挂载、整盘解压、remux 或转码。主片段缺少预期 PID 时才读取能够补齐它的其他片段，仍缺失则明确失败。Dolby Vision 增强视频及同 PID 的 TrueHD/AC3 流均保留。

### 真实媒体与跨平台验证

同一套代码分别在 Windows 原版 ffprobe 和 WSL Linux＋官方 DSM 原版 ffprobe 上探测两张真实 ISO。两组均通过 50 项检查，调用时显式加入 `-protocol_blacklist bluray`，确认不调用原生 Blu-ray 路径。

| 影片 | 流数量与组成 | MPLS 时长（秒） | ISO 大小（字节） |
| --- | --- | --- | --- |
| 青蛇 | 7：1 视频、2 音轨、4 字幕 | 5934.458333 | 30536433664 |
| 禁闭岛 UHD | 10：2 视频、3 音轨、5 字幕 | 8284.776489 | 60674408448 |

青蛇音轨为 zho/zho，字幕为 kor/eng/zho/zho；禁闭岛音轨为 eng/zho/zho，字幕为 eng/zho/zho/zho/zho。禁闭岛的两条视频（含 DV）和 DTS、TrueHD、AC3 均保留。临时 SMB 挂载已卸载并删除空挂载目录，未修改共享内容。

### 主实例与持久化读回

通过 `scripts/update-local-test.ps1 -Apply -HelperOnly` 更新本机主 Helper，备份位于 `artifacts/checks/local-update-20261031-185602-0f72b3`。插件仍为已加载的 0.1.1，无需更换 DLL 或重启 Emby，也未增加常驻测试端口。

通过实际插件接口重新探测并保存青蛇 1885、禁闭岛 1886，均返回 HTTP 200。随后从管理员用户的影片详情接口与 `PlaybackInfo` 分别读回，确认 7/10 条流、上述语言及完整 ISO 大小均正确，`PlaybackInfo` 无 ErrorCode。管理 API 的 ISO 前置检查返回 `ready`、`canProbe=true`、插件 0.1.1 和统一内置读取说明。

已有有效时长按保护策略保留：青蛇为 `59344583330` ticks，禁闭岛为 `82846513670` ticks。禁闭岛与此次 MPLS 探测值相差约 0.125 秒属于保留已有数据的预期行为，没有覆盖原有有效时长。

另对真实青蛇 ISO 指定不存在的 `-playlist 99999`，主 Helper 探测进程返回退出码 1、stdout 长度 0，stderr 明确指出 `99999.MPLS` 不存在或不是完整播放列表；未调用原生路径，也未提交任何入库请求。

### 验证与交付范围

- 批量任务 59 项、Web DOM 37 项、应用 104 项、插件数据保护 13 项、代理/传输/播放列表 169 项检查通过。
- Windows 真实 ISO 50 项、Linux＋DSM 原版 ffprobe 真实 ISO 50 项检查通过。
- `scripts/build-synology.ps1 -Version 0.2.0-13` 成功生成 DSM 7.2 x86_64 SPK；插件构建 0 警告、0 错误。
- `tests/synology/check-spk.py <SPK> --lifecycle --cross-user` 通过 83 项，包含不同 Helper/Emby 服务用户的权限与生命周期验证。
- SPK：`artifacts/downloads/EmbyBlurayHelper-0.2.0-13-dsm7.2-x86_64.spk`。
- SHA256：`4df2a908b87806a51d17ba3a1fdc9d995fe3d1055e8f95f857b960e0e62e5442`。

尚未安装到 109 DSM 真机；WSL 中使用官方 DSM 二进制的测试不等同于 DSM 真机验证。用户升级 SPK 后需在“原盘语言补全”页重新检查并尝试缺失影片补全。本次验证覆盖媒体信息显示与保存，不宣称解决 Emby 客户端 ISO 原生播放兼容性。
