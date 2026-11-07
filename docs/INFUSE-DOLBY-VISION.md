# Infuse Dolby Vision 按需兼容

开发日期：2026-11-07。开发分支：`feature/infuse-dolby-vision`。
恢复起点：`ffeebf1`；更早的稳定功能基线：`f9c0250`。

## 范围与画质边界

独立开关 `enableInfuseDolbyVision`，默认关闭。开启后，仅处理经过
Helper 播放代理、由 Infuse 发起的静态播放，且 Emby 已识别为 P7 的
UHD BDMV / 蓝光 ISO。普通 MKV、HDR10、其他播放器和原 Emby 8096
连接保持原路径。新影片须先由原盘规格补全识别出真实 P7。

视频与音频不重新编码：复制 BL 压缩画面和原音频，libdovi mode 2
转换实际 RPU，去除 EL，再按需封装为 MKV。**FEL 增强层的画面残差
会舍弃，不等同于完整 FEL 画质。** 原盘不修改，不生成整片临时视频。

虚拟 MKV 携带音轨、字幕、原盘语言，以及 Emby 已保存的章节。
符合条件的 DV 原盘走这一通路时优先于旧的虚拟 ISO / PMT 语言路径；
不要求用户再开启章节或语言开关。外置字幕仍保留原 Emby 索引。

## 使用

1. 使用新版 Helper，进入「Infuse 播放增强」。
2. 展开「缓存与工具设置」，点「检查工具」。默认使用原版 ffprobe
   同目录的 ffmpeg，仅用于片段复制封装；不替换原版探测器。
3. 打开「Infuse 杜比视界兼容」。缺少工具或库无法加载时保存会失败，
   界面显示实际保存状态并给出原因。
4. Infuse 仍连接 Helper 的 8097，用原 Emby 账号。开关变化后强制退出
   Infuse 并重新同步媒体库，避免沿用原盘容器缓存。

Windows x64 依赖放在 `tools/dovi.dll`。开发机可执行：

```powershell
scripts/stage-dovi-windows.ps1 -Destination artifacts/checks/dv-app-preview
```

此脚本只下载固定的官方 libdovi 3.4.0，核验 ZIP 与 DLL 的 SHA256，
同时复制 MIT 许可证。没有系统级安装。

Linux 默认 `tools/libdovi.so`，macOS 默认 `tools/libdovi.dylib`，也可配置
完整路径。必须与操作系统、CPU 和系统运行库兼容。**本轮只验证了
Windows x64 原生库；DSM / Linux / macOS 的库构建和设备验证尚未完成，
不能把 Windows DLL 放进 SPK 后宣称支持。** 缺失或无法加载时不改写
Infuse 元数据，不接管原播放。此功能不要求升级 Emby 插件 DLL。

## 按需输出与 HTTP

- 从 MPLS 匹配当前正片，读取 CLPI CPI coarse/fine 索引；不按平均码率
  猜测 seek 位置，不依赖 FFmpeg 的 `bluray:` 协议。
- 大约每 4 秒在真实随机访问点建立一个段。流开始时间进一步通过
  索引位置的 BL PES PTS 核验。
- 读取一个有上限的小窗口，包括前一个访问点和后续少量访问点，保证
  音频 / 字幕 PES 与 BL/EL 在边界附近完整。ISO 用 UDF 按需读取，不挂载
  或整盘解压；仅现有读取器允许的少量导航文件进入临时目录。
- FFmpeg `-copyts -c copy` 复制到有限长度 MKV；按实际 PTS 对齐两层。
  不同层缺帧、RPU 不是 P7、GOP 不匹配、轨道配置变化均失败关闭。
- 小型固定文件头包含真实 Tracks、Duration、Chapters、SeekHead、Cues。
  每段使用从原 M2TS 包索引算出的固定地址空间。剩余字节为 EBML Void；
  不占用整片磁盘空间，但顺序读取会传输这些填充字节，所以传输大小
  接近原盘大小，而不是删去 EL 后的最小 MKV 大小。
- HEAD、单个普通 / 开放 / 后缀 Range、If-Range 均有明确响应；不支持
  多段 Range 时返回 416。重叠请求返回相同字节。
- 每次请求（含缓存命中）用调用者凭据向 Emby 检查可见性与播放权限。
  不使用管理 API key 代替用户。仅沿用已有原盘兼容规则，允许绕过
  Emby 已知的原盘静态读取 404 / 500；其他拒绝继续传递。
- 显式的旧 DV URL 在开关关闭后返回 410，源变化后返回 409，其他客户端
  返回 403。不会在已声明为 MKV 的响应里返回 ISO / M2TS 原字节。

## 缓存与取消

默认单会话媒体缓存 **512 MiB**，全局 **1024 MiB**。会话按认证信息、
设备 / 播放会话和客户端信息的哈希隔离，不把明文凭据写入缓存键或日志。

缓存按请求填充，保留近期已转换片段，不等待缓存填满才起播。没有一个
「打开影片就一路转换到结尾」的后台任务。Infuse 请求远处 Range 时直接
转换目标段；停止读取后不再继续后续段。当前无人等待的构建会取消，
仍有其他 Range 等待同一构建时不误杀。关闭功能 / 改变配置会停止旧转换。

缓存会核算构建预留量和正在发送的片段。正在使用的片段保持计费并被
固定，预算不足时等待或淘汰已释放的最旧片段，不因慢客户端留下未计费
数组。空闲约 2 分钟后释放（30 秒清理周期）。

**这不是整个进程的 RAM 上限。** 原生工具、有限片段的转换工作区、
最多 32 份小型导航头、.NET GC 与 OS 文件缓存另计。一个全局构建门禁
限制同时只有一段进行 FFmpeg / libdovi 转换，避免工作区随请求并发增加。
单个输入窗口至多 192 MiB；单个输出片段槽至多 128 MiB。

## 验证与仍需设备验证的内容

前置实验 `docs/INFUSE-DV-P81-EXPERIMENT.md` 已由用户在 Apple TV / Infuse
确认短 MKV 样本能点亮 DV。**新按需通路不能仅凭该短片段结果宣称整片
播放已通过。** 还需验证起播、跨段连续播放、前后拖动、续播和停止。

2026-11-07 本机真实来源：《少数派报告》ID 12，NAS SMB BDMV
`00294.m2ts`，P7 FEL，Emby 4.10.2.0，原版 FFmpeg
`5.1-emby_2023_06_25_p6`，libdovi 3.4.0。

- 全片导航：8710.243 秒，1983 个按需段，虚拟长度 97,715,260,480 字节。
- 首段、约 178 / 183 秒、片尾真实转换和重叠 Range 检查通过。
- 两个相邻段合并后，与一次连续读取的参考输出逐轨逐包对照：压缩内容
  和时间戳一致；无边界音频、字幕或视频包重复 / 遗失。
- 实际基础层 VCL 哈希一致，音频 / 字幕数据与 PTS 未变。
- ffprobe 读出真实 DOVI config：Profile 8、BL compatibility 1、RPU=1、
  EL=0、BL=1；不是只改详情页的标签。
- 缓存并发、取消、预算、重试、Range、鉴权、开关关闭与客户端隔离：
  本机综合核验共 120 项；新页面 13 项；应用集成 138 项通过。
- 原有 transport / playlist 169 项、library 86 项、scope 35 项、
  media features 42 项、chapters 31 项及 UDF / 虚拟 ISO 检查通过。
- 抽测约 4.4 秒段的本机完整读取 / 封装 / RPU 转换约 0.7–1 秒。
  这是本机热缓存结果，不是 DSM CPU / 磁盘或网络性能保证。

可复现（不显示或保存 API key）：

```powershell
& 'E:\ProgramFiles\dotnet-8\dotnet.exe' run --project tests/EmbyBlurayHelper.DolbyVision.Tests
& 'E:\ProgramFiles\dotnet-8\dotnet.exe' run --project tests/EmbyBlurayHelper.DolbyVision.Tests -- --http artifacts/app/win-x64/data/settings.json 12 '<original-ffmpeg.exe>' artifacts/tools/libdovi-3.4.0/dovi.dll artifacts/checks/dv-streaming
node tests/web/dolby-vision.test.cjs
```

当前安全范围是未加密、单角度、单时钟域、完整 play item；支持按完整
片段组织的 BDMV 与 UDF ISO 读取路径。裁剪 / 复杂分支保留原路径。
真实 P7 ISO、多片段 DV、MEL、24/25/30/50/60 fps 的设备覆盖尚待补齐；
不将单片 BDMV 测试扩大为所有原盘均已兼容的结论。
