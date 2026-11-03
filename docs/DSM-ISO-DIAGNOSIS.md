# DSM ISO 探测实测（2026-10-31）

对象：用户 109 群晖的 Emby 4.10.2.0，Helper SPK 0.2.0-11。测试通过管理员 API，未修改 ffprobe 或原盘，未重启 NAS 服务，也未停止用户正在执行的批量任务。

## 最小复现

1. `GET /emby/BlurayHelper/Status` 返回 ready、version 0.1.1，以及 media-streams、missing-runtime、missing-size。证明插件已加载，本次 500 不能归因为未安装或忘记重启。
2. `GET /emby/Items?Ids=13454,26896&Fields=MediaSources,MediaStreams,Path,RunTimeTicks&EnableImages=false`：禁闭岛为 blurayiso、青蛇为 iso，源分别为 mediasource_13454、mediasource_26896，均缺时长。
3. 逐片 `POST /emby/BlurayHelper/Probe`，使用 PascalCase、Content-Length 已知的 JSON。禁闭岛请求为 `{"ItemId":"13454","MediaSourceId":"mediasource_13454"}`；青蛇同理。

两片都返回 HTTP 500，响应核心内容相同：

```text
ffprobe failed - streams and format are both null
ffprobe version 5.1-emby_2023_06_25_p6
built with gcc 13.4.0 (crosstool-NG 1.28.0)
bluray:<ISO 路径>: Protocol not found
```

旧版 Helper 从 CLPI 提取到语言，但 ffprobe 未返回视频流，不能只拿语言表创建完整流、大小和时长；插件未执行保存。禁闭岛 audio 0x1100=eng、0x1101=zho，subtitles 0x12A0=eng、0x12A1–0x12A4=zho。青蛇主播放列表实际有 audio 0x1100/0x1101=zho 和四条字幕 0x1200=kor、0x1201=eng、0x1202/0x1203=zho；旧语言表还包含菜单交互图形 PID 0x1400，新读取器已明确将其排除，不能当成缺失字幕。

`GET /emby/System/Logs/Log?Name=embyserver.txt` 中亦可看到相同异常，其他 ISO 包括盗火线、何以为家、乱世佳人也报 Protocol not found。

## 处理边界

最初诊断需要另一条不依赖此版 ffprobe 蓝光协议的 ISO 探测路径，不能用普通开源 ffprobe 覆盖 Emby 原版，也不能把 Windows 已验证等同 DSM 可用。后续实现及验证如下。

SPK 0.2.0-12 新增 ISO 条件提示与后台前置检查：未加载插件或缺少 bluray 输入协议时，不逐片提交 ISO；保留 BDMV 流程。失败请求显示具体接口及脱敏响应，避免单个 HTTP 500 掩盖真实原因。检测依据实际加载端点和 `ffprobe -hide_banner -protocols`，不根据 DLL 文件存在或编译选项猜测。

## 0.2.0-13：统一内置读取

按用户要求，ISO 在所有平台只使用内置 UDF/M2TS 路径，已移除原生蓝光协议重试。DiscUtils 读取镜像，BDInfo 选择主播放列表，临时 localhost Range 服务让原版 ffprobe 按需读取真实媒体。主播放列表提供整片时长、章节和 PID 语言；编码、分辨率、HDR、声道仍由实际媒体探测得到，不凭 CLPI 猜测。

2026-10-31 使用官方 `emby-server-synology72_4.10.2.0_x86_64.spk` 中原样提取的 `bin/ffprobe` 及 `lib` 在 WSL Linux 测试。实际协议列表无 bluray、有 http/tcp。测试程序使用与发布 App 相同的 invariant globalization 设置；没有安装或替换系统库。两个 ISO 从现有 SMB 共享按需读取，没有复制完整镜像到本地。

| ISO | 结果 | MPLS 时长 | 完整文件大小 |
| --- | --- | --- | --- |
| 青蛇 1080p | 1 视频、2 音轨、4 字幕；音轨 zho/zho，字幕 kor/eng/zho/zho | 5934.458333 秒 | 30536433664 字节 |
| 禁闭岛 UHD | 2 视频（含 DV 增强层）、3 音轨（含 TrueHD/AC3 同 PID）、5 字幕；音轨 eng/zho/zho，字幕 eng/zho/zho/zho/zho | 8284.776489 秒 | 60674408448 字节 |

两平台集成检查覆盖 ISO 路径和大小、整片时长、高清编码、轨道语言、流编号/总数、章节和 DV/HD/core 保留。明确禁用原生 bluray 协议后仍通过。异常返回非零退出码；缺少预期 PID 不生成替代完整结果。多片段只在需要补足 PID 时追加读取。

前置检查改为实际加载插件及 HTTP/TCP 输入能力，不再阻止“缺少 bluray”的 DSM。已加载的插件 0.1.1 不需要替换；升级 Helper 后在管理页重新检查并补全。WSL 验证证明 DSM 二进制可用，NAS 上实际套件权限、路径和入库结果仍需安装新版 SPK 核验。
