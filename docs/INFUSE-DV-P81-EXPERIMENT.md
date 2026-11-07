# Infuse P7 → P8.1 本机验证

日期：2026-11-07。实验分支：`experiment/infuse-dv-p81`。
起点：`f9c0250`（当时工作区干净）。

## 当前结论与边界

已完成短片段转换、真实码流验证和本机 HTTP 读取验证；用户已用
Apple TV / Infuse 实测三段均能正常播放，并明确确认 **编号 02 的
Converted P81 在连接电视的 Apple TV 上成功点亮 Dolby Vision**。
**尚未实现播放时的自动转换，也没有修改正在运行的 Helper
二进制、Emby 原版 ffmpeg/ffprobe 或 NAS。**

这轮只回答：真实 P7 FEL 来源经不重新编码的 P8.1 转换后，能否被用户的
Infuse 播放链路正确作为 Dolby Vision 播放。短片段成功不能证明整片
按需转换、随机拖动、续播、多片段 BDMV 和 ISO 已经解决。

FEL 转换舍弃增强层画面残差，保留原基础层的压缩画面与转换后的 RPU。
本实验不声称保留完整 FEL 画质。

## 测试机和样本

- Windows 本机：`192.168.1.10`。
- Emby 4.10.2.0：8096；现有 Helper 代理：8097；管理页：8099。
- 本机原影片：《少数派报告》，Emby ID `12`。
- 输入：该影片 `BDMV/STREAM/00294.m2ts`，源媒体通过 NAS SMB 只读。
- 源基础层：HEVC 3840×2160，24000/1001 fps；独立增强层：HEVC 1920×1080。
- 取约 180 秒位置起的一分钟，复制两路视频和第一条 DTS-HD MA 音轨。
- `dovi_tool` 从实际 RPU 确认源为 **Profile 7 FEL**，不是仅凭文件名推断。
- 本轮没有中文字幕和菜单测试；未覆盖 MEL。

最终实验目录（被 Git 的 `artifacts/` 规则排除）：

```text
artifacts/checks/dv-p81-20261107-v2/
  experiment.json             所有实际命令、耗时和媒体信息
  source-dual.mkv             有限长度的源片段
  base.hevc / enhancement.hevc
  timestamps.txt / audio-timestamps.txt
  p7.hevc / p81.hevc
  p7.rpu / p81.rpu
  media/                      三个设备对照样本
  emby-registration.json      测试库及样本 ID，无 API 密钥
  verification-offline.json
  verification.json
  benchmark.json
  *.log
```

首轮目录 `artifacts/checks/dv-p81-20261107/` 保留失败核验记录和样本。
其临时媒体库登记已移除，源影片和首轮样本文件均未删除。

## 工具和可复现操作

使用本机已有的现代 FFmpeg 和 MKVToolNix，不替换 Emby 随附版本：

- FFmpeg：`N-124401-g17734f6967-20260506`。
- MKVToolNix：89.0，包含 `mkvmerge`、`mkvextract`。
- `dovi_tool`：2.3.4，官方便携 Windows x64 构建。
- 官方下载位置：
  `https://github.com/quietvoid/dovi_tool/releases/download/2.3.4/dovi_tool-2.3.4-x86_64-pc-windows-msvc.zip`
- ZIP SHA256：
  `0d9733d311dfe49f9dca0c350f7e6d978182673d333c6f07f7365daf9e6c81eb`。
- 工具存放：`artifacts/tools/dovi_tool-2.3.4/`，未安装系统组件。

核心命令：

```text
dovi_tool -m 2 mux --bl base.hevc --el enhancement.hevc --discard -o p81.hevc
```

`prepare-dv-p81-test.mjs` 用输入端定位和有限时长复制片段，提取 BL/EL，
分别生成 P7、P8.1 和 HDR10 对照，再封装为 MKV。所有版本使用同一条
原音轨。音视频均显式传入从源片段提取的原始时间戳。

从项目根目录执行；`--input` 换成源 M2TS 的完整路径，输出须是新目录：

```powershell
node scripts/prepare-dv-p81-test.mjs `
  --input '<source.m2ts>' --output artifacts/checks/<new-experiment> `
  --seconds 60 --start 180 `
  --ffmpeg 'D:\MySoft\ffmpeg-master-latest-win64-gpl-shared\bin\ffmpeg.exe' `
  --ffprobe 'D:\MySoft\ffmpeg-master-latest-win64-gpl-shared\bin\ffprobe.exe' `
  --mkvmerge 'D:\MySoft\mkvtoolnix\mkvmerge.exe' `
  --mkvextract 'D:\MySoft\mkvtoolnix\mkvextract.exe' `
  --dovi artifacts/tools/dovi_tool-2.3.4/dovi_tool.exe

node scripts/verify-dv-p81-test.mjs artifacts/checks/<new-experiment> --offline
scripts/register-dv-p81-test.ps1 -ExperimentPath artifacts/checks/<new-experiment>
node scripts/verify-dv-p81-test.mjs artifacts/checks/<new-experiment>
scripts/measure-dv-p81-test.ps1 -ExperimentPath artifacts/checks/<new-experiment>
```

注册脚本只允许本机 8096，创建隔离媒体库，禁用网络电影信息/图片获取、
实时监控和章节截图，只刷新该库。已存在同名但指向不同目录的库时拒绝
改动。认证从本机已有设置文件读取，使用请求头，不输出或写入测试记录。

## 已通过的核验

1. 三个视频的基础层 VCL NAL 数量均为 **11496**，实际压缩画面内容
   SHA256 完全相同：
   `3c173c3d37063e9539b9b0eb17ada49d666cdd7e31162bd4d78d9604c4976378`。
2. P7、P8 输出均有 **1437** 条 RPU，六个场景；P8 不含 EL NAL。
3. MKV 中 P8 的真实配置为 `dv_profile=8`、
   `dv_bl_signal_compatibility_id=1`、`RPU=1 / EL=0 / BL=1`。
4. 实际解码读到的 P8 帧具有 Dolby Vision Metadata；抽查四帧的
   `disable_residual_flag=1`、`nlq_method_idc=-1`。不是只写容器标签。
5. 三份样本的 **1437 个视频包**及 **5702 个音频包**的 PTS 均与源片段
   完全一致；DTS-HD MA 压缩音频哈希也一致。
6. 三份样本分别经 8096 和现有 8097 代理，用测试 Infuse User-Agent
   读取：HEAD 长度正确；文件头、重叠区域、中部、尾部共 **24 次 Range**
   均返回正确 206、Content-Range 和与本地文件相同的字节。
7. Emby 已保存三份样本的时长与音视频流。

首轮核验发现 MKVToolNix 默认重新计算 DTS 时间戳，未通过逐包比较。
修正为 `mkvextract timestamps_v2` 提取视频和音频时间戳，封装时对两条
输入分别设置 `--timestamps`；v2 的所有 PTS 已严格一致，没有放宽误差
标准来绕过问题。

## 本机耗时测量

60 秒样本，已分离的 BL/EL 位于本机磁盘，暖文件缓存，单独重复运行
P8.1 转换命令：

| 指标 | 实测 |
| --- | ---: |
| 墙钟耗时 | 0.727 秒 |
| 累计 CPU 时间 | 0.594 秒 |
| 采样到的进程峰值工作集 | 9.66 MiB |
| 输出 HEVC 字节数 | 530795888 |

这只衡量 Windows 上的核心转换，不含 NAS 读取、解复用、复用、HTTP、
播放端或随机寻址；工作集不等于操作系统文件缓存总量。不能据此承诺
DSM 的整条实时链路占用。首次制样取片段约 4.54 秒，转换约 0.56 秒，
每个 MKV 复用约 1.65–1.73 秒，也都是短文件试验。

## Infuse 配合测试

在原本的本机 Emby 连接 `192.168.1.10:8097` 刷新库，进入
**DV Compatibility Test**（最终库 ID `2337`）：

| ID | 影片名 | 内容 |
| --- | --- | --- |
| 2342 | DV Test 01 - Original P7 FEL | P7 双层合并到同一 HEVC 轨的对照 |
| 2343 | DV Test 02 - Converted P81 | 转换后的单层 P8.1 |
| 2344 | DV Test 03 - Base HDR10 | 只保留 HDR10 基础层 |

每段约 60.94 秒，来自同一段画面。01 是重新封装的 P7 对照，不是保留
完整原盘目录结构。优先测试 02，再比较 01 和 03。

还需补充：设备具体型号、播放中的各版本 HDR/DV
信息、电视信号提示、色彩、音画同步、样本内前后拖动。详情页识别出 DV
不能代替真实播放验证；固定输出 DV 的显示模式也不能单独证明元数据
正确应用。若只用手机，结论应限定到该手机，不能直接推广到 Apple TV。

## 第一轮设备反馈与日志核对

用户原话：

> 使用infuse按照 2  1  3  的顺序进行播放，第2个成功点亮了杜比视界，三段都正常播放

2026-11-07 本机 Emby 日志确认客户端为 `Infuse-Direct/8.5.9`、
`Device="Apple TV"`，实际 PlaybackInfo 和媒体读取顺序：

| 本机时间 | 样本 ID | 片名 | 已观察到的服务端结果 |
| --- | --- | --- | --- |
| 12:56:37 | 2343 | 02 - Converted P81 | PlaybackInfo 200，Static=true 媒体读取 206 |
| 12:57:13 | 2342 | 01 - Original P7 FEL | PlaybackInfo 200，Static=true 媒体读取 206 |
| 12:57:39 | 2344 | 03 - Base HDR10 | PlaybackInfo 200，Static=true 媒体读取 206；退出后连接取消 |

这些请求对应预先生成的 MKV 文件，未走 Emby 视频编码链路。
12:56:24–12:56:31 还有三份文件的短范围读取；这不应被误认为实际
播放顺序，后面的 PlaybackInfo 和媒体读取与用户反馈顺序一致。

用户随后明确答复，点亮 Dolby Vision 的是 **编号 02：Converted P81**
（最先播放的样本），设备为 **Apple TV，连接电视**，消除了“第2个”
可能指播放顺序第二段的歧义。

结论：P8.1 短样本在本次 Apple TV / Infuse / 电视链路的起播和 Dolby
Vision 输出验证通过。显示模式来自用户真机反馈，服务端日志独立确认
设备、样本和读取行为；01、03 的实际输出模式、电视及 Apple TV 的
具体型号、色彩准确性和拖动/续播尚未分别验证，不扩大本次结论。

另记录：三次 `Sessions/Playing` 播放状态上报均出现
`ArgumentNullException (Parameter 'key')`，堆栈位于
`SessionManager.OnPlaybackStart`。三段共有，且用户反馈均正常起播；
尚未确定原因，不归因于 P8 转换，也不将残留 Session 的 `PlayMethod`
字段当成这次视频发生转码的证据。后续续播测试应单独核对这一上报。

现有代码检查确认 `TransportLanguagePatcher` 保持文件长度与绝对偏移；
`VirtualUdfImage`/`InfuseChapterProxy` 也按确定长度提供 Range。P8.1
样本的长度已改变，不能把转换直接当成现有等长 PMT 修改使用。
下一阶段需要独立验证流式封装、索引/定位、背压、取消和缓存边界。

## 设备验证通过后才推进的工作

- 明确按需输出的封装、索引、随机定位及缓存上限，不能整片预先转换。
- 将轻量 RPU 转换与 TS/MKV 重新封装分别测量，验证同时播放与取消。
- 明确 MEL/FEL 策略、客户端和原盘条件、失败回退以及独立开关和日志。
- BDMV 单片段、多片段、ISO 分别验证，保留现有正常媒体和客户端路径。
- 确定依赖的跨平台版本和许可，再考虑 SPK/便携包发布。

本轮没有发布 SPK、替换 DLL、推送 Git 或在 DSM 上部署。
