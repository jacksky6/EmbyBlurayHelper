# P8.1 M2TS / Blu-ray ISO compatibility experiment

日期：2026-11-07。分支：`experiment/infuse-dv-p81-iso`；起点：`2efb56a`。

## 当前结论

已生成两份有限长度的 P8.1 M2TS / UDF 2.50 蓝光镜像，并通过实际码流、
播放列表、CLPI 定位索引及 HTTP Range 核验。**用户在 Infuse / Apple TV
实测反馈不可用，已于 2026-11-07 停止此方向，不进入生产播放路径。**

这轮验证的是软件播放器的容器兼容性，不宣称 P8.1 属于标准 UHD 蓝光
配置，也不宣称生成的镜像能在实体蓝光机播放。tsMuxeR 自身会输出
非标准 DV 蓝光轨道警告，实验记录完整保留了该警告。

生产默认保持普通原盘走 ISO、P7→P8.1 走按需 MKV；另提供普通原盘
MKV 复制封装的可选设置，便于后续对比，与此 P8.1 ISO 实验无关。
没有替换 Helper / Emby 二进制、重启服务、改变端口、修改运行配置，
也没有在 DSM 上部署。本轮只有一分钟静态测试文件；**没有实现整片
P8.1 ISO 按需生成**。

## 本机与样本

- Windows 本机 `192.168.1.10`；Emby 4.10.2.0 在 8096，既有代理在 8097，管理页 8099。
- 来源为本机 Emby 条目 12《少数派报告》同一只读 NAS BDMV 的 `00294.m2ts`。
- 最终改取原盘起点约 60 秒，避免 tsMuxeR 对片段中途起始 POC 的重建错误。
- 原始视频为 P7 FEL；使用既有实验脚本经 `dovi_tool -m 2` 生成 P8.1。
  基础层压缩画面保持，舍弃 FEL 残差；不重新编码视频和音频。
- 该新来源首先通过原有 MKV 离线核验，作为本轮 ISO 与 MKV 的共同输入。
- 最终 ISO 来源目录：`artifacts/checks/dv-p81-iso-source-20261107/`。
- 最终镜像与记录：`artifacts/checks/dv-p81-iso-20261107-v5/`。
  之前的 v1–v4 目录是失败或未通过全部核验的诊断记录，没有加入媒体库。

这些生成文件受现有 `artifacts/` Git 排除规则保护；工具、脚本和本说明纳入版本控制。

## 工具

- .NET SDK 8.0.427；实际运行时 8.0.33。
- FFmpeg / ffprobe：`N-124401-g17734f6967-20260506`，沿用独立实验版本。
- dovi_tool 2.3.4、MKVToolNix 89.0，沿用既有短样本工具。
- tsMuxeR 2.7.0，官方便携 Windows x64 构建，解压至 `artifacts/tools/tsMuxer-2.7.0/`。
- ZIP 来源：<https://github.com/justdan96/tsMuxer/releases/download/2.7.0/tsMuxer-2.7.0-win64.zip>
- ZIP SHA256：`E3CF117D2C6F01332188123641C63FE38AB9731AE7AA23645F6F9A261A7A301C`。

仅下载便携工具，没有安装系统组件。

## 封装中发现的差异

tsMuxeR 2.7.0 的 HEVC 实现：
<https://github.com/justdan96/tsMuxer/blob/2.7.0/tsMuxer/hevcStreamReader.cpp>

1. `getTSDescriptor` 在蓝光模式主动省略 `DOVI` 注册描述符和 DoVi 描述符；
   普通 M2TS 模式写出 P8 / compatibility 1 / BL+RPU / 无 EL 配置。
2. 蓝光创作把含 RPU 的单路 HEVC 误分配为 DV 增强轨 PID `0x1015`，放到
   MPLS DV 子路径，主视频轨计数为零。BDInfo 因而只能看到音频，原有 ISO
   补全器也会正确拒绝该播放列表，不能直接将此创作输出作为有效对照。
3. 实验工具 `MplsSingleLayer.Promote` 对此单片段、单视频、单音频 fixture
   提升现有 HEVC BL+RPU 为主视频轨，删除不再引用的 DV 子路径，重定位
   后续章节 / 扩展区，同时修正主副两份 MPLS。PID 和媒体偏移保持不变。
   这不是通用蓝光导航改写器，不用于实际影片。
4. 为有限 HEVC 输入加 EOS / EOB，令 tsMuxeR 正确输出末帧 PES 时间戳。
   最后额外的 EOB 是独立 7 字节 NAL，不含画面和 RPU。验证器用其完整
   内容 SHA256 识别，只将该包排除在画面 PTS 比较之外；全部 1,440 帧
   都必须有 PTS，没有忽略丢失时间戳的真实画面。

两份 ISO 共用修正后的导航和同一 M2TS PES 内容，区别仅在 PMT：

| 样本 | M2TS PMT | 实际压缩视频与 RPU |
| --- | --- | --- |
| 04 P81 Native | 保留蓝光模式输出，不加 DoVi 描述 | BL + P8.1 RPU，无 EL |
| 05 P81 Signaled | 增加 `DOVI` 注册与 P8.1 描述 | 与 04 相同 |

05 的附加描述字节为 `0504444F5649B005010010351F`，来自 tsMuxeR 普通
M2TS 对照，校验为 profile 8、level 6、RPU=1、EL=0、BL=1、compatibility=1。
在原 PMT 填充空间内添加，并重算 MPEG-2 CRC，更新 720 次重复 PMT。
媒体长度、包数、PCR、视频 / 音频 PES、CLPI 偏移不改变。

ISO 使用现有 `VirtualUdfImage` 生成，避免引入另一个未验证的镜像写入器。
独立 DiscUtils UDF 和 BDInfo 重新读取结果，且镜像成员哈希必须与输入 M2TS 一致。

## 已通过的实际核验

数据见最终实验目录的 `iso-inspection.json`、`verification-offline.json`、
`verification-http.json`。两份 ISO 的共同结果：

| 核验 | 结果 |
| --- | --- |
| 镜像大小 | 466,944,000 字节 |
| M2TS 大小 | 432,666,624 字节 |
| MPLS 时长 | 60.1016889 秒 |
| 章节 | 0 / 20 / 40 秒 |
| CLPI 定位点 | 80，全部指向对应视频 PID 的真实 PES 起点 |
| 压缩基础层画面 NAL | 11,520，数量与 SHA256 与 P8.1 输入完全一致 |
| P8.1 RPU | 1,440，数量与内容 SHA256 完全一致 |
| 视频画面包 | 1,440，全部有 PTS |
| 音频包 | 5,625，逐包 SHA256 与输入完全一致 |
| 相同平移后视频 PTS 最大偏差 | 0.500 ms |
| 相同平移后音频 PTS 最大偏差 | 0.333 ms |
| 首包音画相对偏移变化 | 0 ms；音视频共同平移 600 秒 |
| 实际帧 RPU 解码抽查 | 每份 4 帧，均 disable_residual=1、nlq_method=-1 |

05 的 ffprobe 容器信息读出正确 P8.1 配置；04 无 PMT DV 配置，但解码实际
画面仍读出相同 DV RPU。这两层证据必须分别记录，不能用文件标签代替码流验证。

两个 ISO 和 MKV 对照均通过原有 8096 / 8097：HEAD 长度匹配；文件头、
UDF 导航、32 MiB 的 M2TS 起点、中部、尾部共 **36 个 Range 请求**返回
正确 206、Content-Range 和与本地文件相同的字节。另确认 8097 对两份 ISO
仍返回 ISO 媒体源，不触发 `HelperDv` 的 MKV 转换。

本次数据不衡量首帧、CPU、按需 ISO 的峰值内存或随机跳转成本。静态生成
一分钟镜像的结果不能当作整片实时转换性能证据。

## 真机测试入口

原来的 Infuse Emby 连接：`192.168.1.10:8097`。

刷新媒体库后进入 **DV ISO Compatibility Test**，库 ID `2345`：

| ID | 显示名称 | 用途 |
| --- | --- | --- |
| 2351 | DV ISO Test 05 - P81 Signaled | 优先测试；ISO 含明确 P8.1 PMT 描述 |
| 2350 | DV ISO Test 04 - P81 Native | ISO 原生蓝光 PMT 对照 |
| 2352 | DV Test 02 - Converted P81 | 同一片段 MKV 对照，文件名为 `DV ISO Test 06 - P81 MKV Control.mkv` |

注册时只创建 / 刷新该三项独立媒体库，关闭在线元数据、图片获取、实时
监控和章节截图，没有全库扫描。原先的 **DV Compatibility Test** 未改变。

需要用户分别记录：能否播放、电视实际是否切换 Dolby Vision、是否颜色
正常、声音同步、跳到 20 / 40 秒后能否继续。优先 05，再 04；异常时对照
同库 MKV。影片详情页 DV 标识不等于电视实际输出。

## 复现

先按 `INFUSE-DV-P81-EXPERIMENT.md` 的既有流程，从原盘起点取约 60 秒，
生成 P8.1 并通过原有 `verify-dv-p81-test.mjs --offline`。随后在仓库根目录：

```powershell
node scripts/prepare-dv-p81-iso-test.mjs `
  --source artifacts/checks/dv-p81-iso-source-20261107 `
  --output artifacts/checks/<new-iso-experiment> `
  --tsmuxer artifacts/tools/tsMuxer-2.7.0/tsMuxeR.exe

& 'E:\ProgramFiles\dotnet-8\dotnet.exe' build tests/EmbyBlurayHelper.DvIsoExperiment -c Release
& 'E:\ProgramFiles\dotnet-8\dotnet.exe' `
  tests/EmbyBlurayHelper.DvIsoExperiment/bin/Release/net8.0/EmbyBlurayHelper.DolbyVision.Tests.dll `
  artifacts/checks/<new-iso-experiment>

node scripts/verify-dv-p81-iso-test.mjs artifacts/checks/<new-iso-experiment>
scripts/register-dv-p81-iso-test.ps1 -ExperimentPath artifacts/checks/<new-iso-experiment>
node scripts/verify-dv-p81-iso-http.mjs artifacts/checks/<new-iso-experiment>
```

注册脚本从本机设置内部读取 API 密钥，通过请求头发送，不写进实验记录。
默认同名库只能复用相同路径；换新目录复现时需指定新的 `-LibraryName`。
不得用正在播放的生产影片替代这些有限 fixture。

若真机成功，下一阶段仍需独立设计按需 M2TS 复用、固定 Range 寻址、
CLPI 重建 / 映射、取消、缓存和多片段边界。现有 ISO 路径的低开销来自
直接读取原始 M2TS，换成 P8.1 后不能直接继承该性能结论。
