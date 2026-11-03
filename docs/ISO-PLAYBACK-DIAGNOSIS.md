# ISO 原生播放失败：本机诊断（2026-10-31）

## 结论和边界

本次用户要求按原生播放诊断，不依赖转码解决问题。已观察到两条不同的失败路径：Emby iOS 首先直接请求完整 ISO；UHD《禁闭岛》在客户端放弃原生播放后，又自动尝试 HLS 转码并失败。后者的服务器输入错误已经复现，但不能把它当作首次原生播放失败的原因。

目前 Helper 实现的是将 ISO 内部视频、音轨、字幕流探测后写入 Emby，其中包括原盘语言。它没有为 Emby iOS 增加 HTTP ISO 解盘能力，也没有改变这两张 ISO 的播放数据。Infuse 能播放和显示语言，应视为其原有 ISO 读取能力；不能将本次成功归因于语言补全。

调查仅做日志、源码和 API 读取，以及原版 Emby ffmpeg 的短时诊断。没有更改播放配置、替换 ffmpeg、部署播放修复或重启服务。

## 版本和样本

- 服务端：Windows Emby 4.10.2.0，端口 8096。
- 客户端：请求日志为 Emby for iOS 2.2.59，播放数据请求 User-Agent 为 libmpv；对照为 Infuse-Direct/8.5.9。
- 工具：本机 Emby 随附 ffmpeg / ffprobe `5.1-emby_2023_06_25_p6`，含 `bluray` 输入协议。
- 青蛇：ID 1885，1080p AVC，Container=iso，实际选中 00002.mpls。
- 禁闭岛：ID 1886，2160p HEVC，Container=blurayiso，实际选中 00800.mpls。

程序路径为 `D:\GIT\Emby插件\embyserver-win-x64-4.10.2.0`。源 ISO 路径可从各自 Emby MediaSources 读取，此文档不记录密钥或带认证参数的播放 URL。

## 原生播放路径

服务器日志 `D:\GIT\Emby插件\programdata\logs\embyserver.txt` 显示：

| 时间 | 影片 | 请求/结果 |
| --- | --- | --- |
| 12:40:46.445 | 青蛇 | libmpv 请求 `videos/1885/original.iso`，Range 从 0 开始 |
| 12:42:16.809 | 青蛇 | 该读取以客户端断开结束；服务器提供 video/iso、正确总长度和 Content-Range |
| 12:42:16.816 | 禁闭岛 | libmpv 请求 `videos/1886/original.blurayiso`，Range 从 0 开始 |
| 12:42:16.854 | 禁闭岛 | 客户端很快断开；服务器提供 video/iso、正确总长度和 Content-Range |
| 12:42:16.950–12:42:18.600 | 禁闭岛 | DirectPlayError 后反复请求 HLS；master 返回 200，live 返回 500 |
| 12:43:06 起 | 禁闭岛 | Infuse 对完整 ISO 发起多个 Range 请求 |
| 12:43:38 起 | 青蛇 | Infuse 对完整 ISO 发起多个 Range 请求 |

另对两片的原生 `original.*` 入口做了独立抽样：请求 ISO 偏移 32768–98303 的 65536 字节，均返回 206、video/iso。对该片段计算 SHA256，与直接读取源 ISO 的同一片段完全相同。两片总长度分别为 30536433664 和 60674408448 字节。

这证明本次取样没有被替换成主影片 M2TS、转封装结果或错误页面，不能据此认定全片传输、客户端解析或解码已经通过。服务器日志没有客户端 libmpv 的具体解盘/解码错误；首次失败高度指向客户端处理 HTTP ISO 的环节，精确原因仍需客户端日志确定。

## 自动回退路径：已经明确复现的输入错误

六份 12:42:17–18 的 `ffmpeg-transcode-*_1.txt` 都是禁闭岛。Emby 生成的关键输入为：

```text
ffmpeg.exe ... -i "\\192.168.1.109\web\...\Shutter.Island....iso" ...
```

输入没有 `bluray:`，随后报 `Invalid data found when processing input`。硬件编码和软件编码回退均在打开输入时失败，因此本次 HLS 500 不能归因于某个硬件编码器。请求还包含 PGS 字幕烧录和禁止视频流复制，故这次自动回退并不是无损转封装。

用相同 Emby 原版 ffmpeg 直接运行以下最小对照；`$isoPath` 为上述样本的实际路径，`$ffmpegPath` 为 Emby 的 ffmpeg.exe：

```powershell
& $ffmpegPath -hide_banner -v warning -i $isoPath -map 0:v:0 -an -sn -c copy -t 1 -f null -
& $ffmpegPath -hide_banner -v warning -i ('bluray:' + $isoPath) -map 0:v:0 -an -sn -c copy -t 1 -f null -
```

| 样本 | 普通 ISO 输入 | bluray: 输入 |
| --- | --- | --- |
| 青蛇 1080p | 退出 1，Invalid data | 退出 0 |
| 禁闭岛 UHD | 退出 1，Invalid data | 退出 0 |

这两条命令只复制视频压缩包到空输出，没有重编码，没有通过 Emby 发起播放。原版工具本身能够读取这两张 ISO 的主影片，与 Emby 当前的播放输入组织方式是不同问题。

还做了明确选择播放列表、从 60 秒位置读取约 2 秒视频和首条音轨的 `-c copy` MKV 诊断。两次退出 0，ffprobe 分别识别为 H.264 1920×1080 + DTS、HEVC 3840×2160 + DTS。样本保存在 `artifacts/checks/iso-playback-20261031/`。这些是短片段读取/封装验证，不能视为实际客户端、完整 DTS-HD、PGS、Dolby Vision 或全片寻址验证。探测短片段时有 DTS residual 提示；UHD 读取另有 BD-J/JVM 提示，但没有阻止本次主影片读取。

## Emby 4.10 的扩展接口证据

本机 DLL 反编译结果显示：

- `BlurayMounter.CanMount` 只接受 `fileInfo.IsDirectory && container == bluray`。当前自带蓝光挂载器不会接管这两个 ISO 文件。
- `MediaMountManager` 会调用已注册的 `IMediaMounter`，返回 `IMediaMount`。
- `EncodingCommon.AcquireResources` 在服务器处理播放前调用挂载器，成功后用挂载结果替换 MediaPath / DirectMediaPath / Protocol，并管理资源释放。
- `EncodingHelpers.AttachMediaSourceInfo` 另外识别 MediaSourceInfo 的 EncoderPath / EncoderProtocol。这说明服务器处理输入有独立于文件路径的扩展空间，但具体方案仍需实证。
- `FFProbeProvider.IsSupported` 明确排除 `.iso`，解释了普通元数据刷新没有探测 ISO；当前由 Helper 插件显式调用探测后写入媒体流。

这些接口适合进一步研究“让服务器正确读取 ISO 主影片”。它们不会自动修复 Emby iOS 对完整 HTTP ISO 的原生播放。尤其不能直接假设把挂载结果拼成 `bluray:` 就一定能通过 Emby 后续的路径、协议和文件系统处理。

## 诊断时的媒体信息缺项（后续已补齐）

两片已经保存流和语言，但 item/source 的 RunTimeTicks 均为空，MediaSource.Size 为 0。禁闭岛经原版 ffprobe 的 bluray 输入可读到约 8284.65 秒的时长。因此目前是“媒体流及语言已补全”，还不能声称“完整 ISO 媒体信息已补全”。

这些缺项可能影响界面进度、码率估算或播放能力判断，尚未做客户端 A/B 验证。但它们不是上述原版 ffmpeg 以普通文件方式打开 ISO 报 Invalid data 的原因；Infuse 也已在相同 Emby 数据条件下成功播放。

后续按用户要求优先解决显示：插件 0.1.1 已补齐两片的时长和大小，重启后仍保留，详情与 PlaybackInfo 均返回有效时长。详见 [本机验证记录](ISO-LANGUAGE-LOCAL-VALIDATION.md#时长与显示补全1402-起)。这不代表本节的客户端原生播放问题已经修复。

青蛇文件名没有 `.bluray.iso` 标记，当前 Container 为 iso。官方命名说明建议用 `.bluray.iso` 表示蓝光；类型区分值得核对，但禁闭岛已经是 blurayiso 且仍失败，不能把改名当作共同解法。本轮没有重命名文件或修改数据库。

## 官方支持边界与后续方向

[Emby 官方 ISO 格式说明](https://emby.media/support/articles/Movie-Naming.html#iso-format) 声明是 basic support：可入库，并在 Emby for Kodi、Emby Windows、Emby for Xbox 等 HTPC 应用播放，其他应用通常需要外部播放器。因此不能把 Emby 能入库、网页有轨道信息，等同于所有客户端都能原生播放完整 ISO，也不能泛化为 UHD ISO 一律不能播放。

按用户的原生播放要求，下一步应优先获取实际 Emby iOS 播放器日志，核对 HTTP ISO 解析与支持范围；当前服务端证据不足以直接断言是 HEVC、Dolby Vision、音轨或字幕解码失败。

如果后续允许改变交付方式，则可以另行研究按需暴露 ISO 内主影片数据，或在需要时无损转封装；它们可以不重编码，但已不是完整 ISO 原样直传。需要分别验证播放列表、轨道索引、HTTP Range、拖动、字幕和 HDR/DV，不能由本次短片段复制测试宣称已完成。当前不部署这类变更。
