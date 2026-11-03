# Infuse 原盘章节：虚拟 ISO 实验

## 触发与范围

独立开关 `enableInfuseChapters`，位于「Infuse 播放」，默认关闭。仅拦截 Infuse 的播放信息和静态原盘流；Emby 必须已经保存有效普通章节。源格式为 BDMV，UHD 和 1080p 都可参与。无章节、普通视频、真实 ISO、无法确认正片分支时保持原方式。2026-11-03 手机 Infuse 已通过《少数派报告》实际虚拟盘起播测试；章节选择/跳转、Apple TV 与 DSM 仍需分别实机验证。

## 数据流

1. 读取调用者有权访问的 MediaSource 和现有 Chapters；只采用普通章节，不把片头/片尾标记伪装成章节。
2. 按正片大小、时长和片段序列匹配 MPLS，歧义时拒绝猜测。
3. 仅在内存副本上将 Emby 的章节时间点写入 MPLS，按各 PlayItem 的 in/out 时间换算。原盘文件、Emby 数据库不修改。MPLS 只提供时间点，不承载自定义章节名称或截图。
4. 生成只读 UDF 虚拟目录，包含正片导航和原始 M2TS 文件的字节区间映射。返回 `blurayiso` MediaSource 与 `.iso` 播放 URL。
5. 每个 HTTP Range 请求重新经过 Emby 的条目访问和播放权限检查，按需读取原始文件或少量内存导航。支持 HEAD、首/中/尾区间、后缀区间、If-Range 和 416。URL 的指纹绑定章节及原始文件版本，旧版本显式请求返回 409。

媒体库条目列表、单条详情和 PlaybackInfo 的媒体源及单源条目路径、容器、大小保持一致。日志记录请求开始、阶段、上游 HEAD 状态、发送字节数与完成/客户端取消/异常结果；不记录认证参数。

2026-11-02 对照实测：手机 Infuse 可播放真实 ISO《禁闭岛》，但不能播放虚拟 ISO《少数派报告》。真实 ISO 的 UDF 卷描述符声明 2.50，并含物理分区与元数据分区映射、元数据文件和镜像；原虚拟镜像只有 UDF 2.01 单分区。此前多次失败请求均收到 206，随后由客户端主动取消，具体错误原因尚未从服务端日志得知。

虚拟镜像已改为 UDF 2.50 双映射结构：文件系统元数据由 metadata file 提供，电影文件仍通过物理分区的 long allocation descriptors 映射至原 M2TS。主/镜像元数据文件都驻留于只读虚拟视图；完整性描述符标记为关闭。DiscUtils UDF、Emby 原版 ffprobe、BDInfo 章节回读和 HTTP Range 测试均通过。**该改动仍需 Infuse 手机实测；服务端解析通过不等于客户端播放已修复。**

### 2026-11-02 严格读取器核验与修正

手机仍报「发生错误，加载此内容时发生错误」。11:13 的请求为 `GET /emby/Videos/12/stream`、`Range: bytes=0-`；代理返回 206，发送 2,871,132,160 字节后于 15 秒被客户端取消。没有服务端 404/500；真实 ISO《禁闭岛》可播放。该日志只能定位到客户端读流后失败，不能单独证明具体解析原因。

使用相同生成器输出 1,507,328 字节的小型测试盘进行独立核验：Linux 内核 UDF 可以挂载，Windows 原生 UDF 则报告错误 1005「卷不包含可识别的文件系统」。发现并修正主卷描述符的 Character Set List / Maximum Character Set List：这两个字段原先误写入卷扇区数，应为支持字符集的位图 `1`。另将完整性描述符的 Integrity Type 从 `0`（开放）修正为 `1`（关闭）。修正后，Windows 能识别 `UDF / EMBY_BLURAY`，并逐字节验证测试盘中的媒体载荷；Linux 内核读取同一载荷也通过。

卷布局同时对齐真实 UHD 样本：主描述符序列位于 LBA 32，完整性序列位于 64，物理分区起始于 288，备份描述符序列移到分区尾部，三个 anchor 指向对应序列。`VirtualUdfImage.FormatRevision` 进入内容指纹，布局升级后不会沿用旧 HTTP ETag 或显式虚拟盘 URL。

本机 Helper 已按 Helper-only 流程更新；Emby、插件 DLL、媒体数据及 DSM 未更新。修正后的《少数派报告》24 章仍可通过 BDInfo 回读，原版 ffprobe/libbluray 能读取正片流。**Windows 识别修复已经验证，Infuse 播放修复仍需手机重测，不能混为同一结论。**

### 2026-11-02 播放 URL 对照

12:37 和 13:51 手机重测仍失败，最新一次代理返回 206、发送 23,330,816 字节后在约 7.1 秒被客户端取消。手机仍只请求 `/Videos/12/stream` 的 `bytes=0-`，没有使用 PlaybackInfo 返回的 `/Videos/12/stream.iso`，也没有出现真实 ISO 的随机小 Range。新版 UDF 读取器通过并未解决手机播放。

下一项独立试验：仅对已认证的 Infuse 虚拟盘请求，将无扩展名或非 `.iso` 的静态播放入口以 HTTP 307 重定向到同一代理、同一条目、同一媒体源的 `stream.iso`，并携带虚拟盘指纹。保留原查询参数；GET/HEAD 方法及 Range 由客户端在重定向后继续发送。重定向在条目权限、指纹和上游播放权限核验后产生，真实 ISO、非 Infuse 以及功能关闭时继续原路径。日志以 `phase iso-redirect` 区分；不记录包含认证信息的 Location 或完整查询。

新增本机 HTTP 检查覆盖 GET/HEAD、可选 `/emby` 前缀、显式 ISO 地址、旧指纹拒绝及默认客户端跟随后的精确字节读取。**这是对 URL 格式选择的验证实验，尚不能从重定向测试通过推断 Infuse 播放成功。**

### 2026-11-02 真实 ISO 字节对照

20:07 手机已跟随 307 到 `stream.iso`，用户仍反馈相同错误。因此，明确扩展名本身没有解决问题；不能继续把这次失败当成「没有采用 `.iso` 地址」。

为区分虚拟 UDF 内容与客户端打开方式，新增仅本机使用的 `tests/EmbyBlurayHelper.InfuseIsolation`：复用生产 `EmbyProxy` / `InfuseChapterProxy`，通过可选的章节呈现工厂，**仅将 `mediasource_12` 的虚拟盘字节替换为已能播放的真实 ISO 条目 1886**。仍从条目 12 进入，API 容器、ISO URL、重定向、权限和 HTTP Range 走相同代码；Size 与 ETag 绑定真实 ISO。预期画面变为《禁闭岛》，本次只检查能否起播，不验证条目 12 的章节/时长与画面对应。除字节来源及其长度/指纹外，不调整 Infuse 响应策略。

测试程序不会进入发布包、SPK 或生产程序；真实 ISO 直接按区间读取，无复制、转码或生成镜像。原始文件、Emby 数据库、Helper 配置均不写入。普通 Emby 客户端和其他条目保持原路径。测试镜像的原始文件区间构造留在测试项目中，不向生产 UDF 生成器添加原始文件后门。

`scripts/start-infuse-isolation.ps1 -Apply -UntilRestored` 在**同一个本机 8097**临时启动测试程序，持续到手动恢复或诊断进程退出，方便手机稍后测试；正常 Helper 暂停，因此管理页 8099 暂不可用。监督进程在诊断进程退出或执行 `-Restore` 后自动启动正常 Helper。另保留 `-Apply -Minutes 30` 限时模式，超时也会恢复；未指定 `-UntilRestored` 时默认限时 30 分钟。Emby 8096 不停止，不增加新的对外端口。测试前已核对 5 个 HTTP 区间（开头、识别序列、anchor、中间及末尾）与真实 ISO 完全一致，HEAD 为 60,674,408,448 字节，非 Infuse 的条目 12 仍为 `bluray`。

```powershell
& 'E:\ProgramFiles\dotnet-8\dotnet.exe' publish tests/EmbyBlurayHelper.InfuseIsolation -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/checks/infuse-isolation
.\scripts\start-infuse-isolation.ps1 -Apply -UntilRestored
# 手机测试后恢复。此模式没有自动到期时间，不使用原先的 30 分钟窗口。
.\scripts\start-infuse-isolation.ps1 -Restore
```

诊断日志位于 `artifacts/checks/infuse-isolation-session-*/proxy.log` 和 `error.log`，区分响应头已提交与实际发送完成。可用 `GET http://127.0.0.1:8097/__diagnostic/status` 确认是否仍在诊断模式。**必须有手机结果才能判断分支；若已手动恢复或限时模式已超时，再次测试正常模式不能算入该对照。**

### 2026-11-03 已知正常 ISO 字节仍失败

19:49:58 手机从条目 12 请求播放；诊断已返回条目 1886 的真实 ISO。客户端跟随 307 到 `stream.iso`，收到 `206 / video/iso`，读取 87,818,240 字节后于 19:50:03 取消，用户反馈仍不能播放；未见服务端 404/500。该结果说明失败不能只归因于生成的 UDF；但条目 12 的流规格、时长及可能的客户端缓存仍然不同，不能据此断定虚拟 UDF 已获 Infuse 兼容。

下一对照 `-Mode real-iso-transport` 从真正条目 **1886《禁闭岛》** 播放：所有媒体库、详情和 PlaybackInfo API 均由关闭章节改写的代理透传，保留它本来的路径、容器、时长、轨道和章节；仅对该条目的播放流使用生产 `InfuseChapterProxy` 的认证、307、响应头和 Range 代码，数据仍逐区间读取同一个真实 ISO。独立代理与缓存避免流请求产生的测试呈现混入后续元数据响应。条目 12 恢复普通 BDMV 路径。该实验仅存在于测试项目，不修改生产策略。

```powershell
.\scripts\start-infuse-isolation.ps1 -Apply -UntilRestored -Mode real-iso-transport
# 手机从原 8097 连接播放真正的《禁闭岛》，反馈后恢复。
.\scripts\start-infuse-isolation.ps1 -Restore
```

该对照本机核验：在首次取流前、五次取流后分别比较影片列表、用户详情和 PlaybackInfo，完整响应一致（仅忽略每次独立请求由 Emby 新建的 `PlaySessionId`）。开头、VRS、anchor、中间、最后 2048 字节均经生产 Range 路径返回 206，与源 ISO 同偏移逐字节一致；HEAD 长度为 60,674,408,448，未认证请求为 401，旧指纹为 409。验证通过只表示对照条件成立，仍需手机从 8097 播放真正的条目 1886。

20:02:23 手机已从真正条目 1886 播放，用户确认正常。代理日志验证了相同的 307、`stream.iso`、`206 / video/iso` 代码，并出现文件尾、目录区及影片数据的随机 Range 请求，其中影片位置 `227848192` 读取 278,331,392 字节、持续约 12.95 秒；没有本次媒体请求的 404/500。这建立了已知正常的 HTTP 传输对照。客户端随机读取中主动取消旧连接本身不等于播放失败。

下一对照 `-Mode native-iso-metadata`：仍从条目 12 进入并读取同一个真实 ISO，但客户端收到的媒体源改为条目 1886 的原生字段，仅保留条目/媒体源 ID 为 12；路径、容器、时长、轨道及章节均与实际 ISO 一致。相应已有的顶层媒体字段也同步。仅测试项目缓冲和改写客户端 JSON 响应；生产流认证继续读取原始条目 12，正常条目 1886 不改写，不修改数据库或配置。

```powershell
.\scripts\start-infuse-isolation.ps1 -Apply -UntilRestored -Mode native-iso-metadata
# 手机点《少数派报告》（12），预期播放《禁闭岛》。
.\scripts\start-infuse-isolation.ps1 -Restore
```

本机已核对列表、用户详情、GET/POST PlaybackInfo 及 `/emby` 前缀：条目 12 的完整媒体源与原生 1886 一致，仅源 ID / ItemId 为 12。gzip 请求头下亦正确返回改写 JSON，普通 Emby 客户端保持原始 BDMV 数据。取流前后媒体资料一致；0、anchor、ISO 最后 2048 字节分别经生产路径返回 206，并与同一个真实 ISO 比对相同。尚待手机起播结果，不能将此状态当作已修复虚拟 ISO。

20:10 手机从条目 12 成功播放了真实《禁闭岛》，用户确认能播放。日志出现 ISO 尾部、目录和影片位置随机读取，影片位置 `227848192` 读取 302,841,856 字节并持续约 27 秒。相同条目 ID 和传输代码可成功，支持继续排查媒体源描述；尚不能单独确定是路径、额外提示字段、原媒体规格不匹配或客户端状态中的哪一项。

下一对照 `-Mode virtual-iso-native` 恢复条目 12 的实际《少数派报告》虚拟 ISO，保持已验证的 UDF 布局及指纹不变。客户端媒体源以其自身原始响应为基础，仅调整 ISO 容器、文件大小、虚拟文件路径和直接播放能力；沿用源路径分隔符，不注入 `DirectStreamUrl` 或 `MimeType`。本片 21 条媒体流、时长 `87102432000` ticks 和 24 章均保留，不再使用 1886 的媒体数据。以上修改只在测试项目响应层，生产 Helper 尚未采用。

```powershell
.\scripts\start-infuse-isolation.ps1 -Apply -UntilRestored -Mode virtual-iso-native
# 手机点《少数派报告》（12），本次预期实际播放《少数派报告》，并检查章节。
.\scripts\start-infuse-isolation.ps1 -Restore
```

本机验证：虚拟 ISO 为 97,196,113,920 字节；媒体源与原生条目 12 的音轨、字幕、章节、时长逐字段一致。36 个有界 HTTP Range 完成 UDF、24 章 MPLS 回读和视频首/中/尾载荷比对；HEAD、后缀、416、409、401、If-Range 及非 Infuse 透传通过。该结果仍需手机验证，不能把真实 ISO 对照成功当作虚拟 ISO 成功。

没有 ffmpeg 子进程，不编码、不 remux、不提前生成完整 ISO，也不落盘视频缓存。原始视频双层 DV、HDR、音频和字幕字节保持原样。此路径由 Infuse 自身解析 ISO 内 MPLS / CLPI 的语言；原有 PMT 语言补写在其他播放路径继续工作。

### 2026-11-03 实际虚拟盘播放通过并恢复正常 Helper

用户确认本次播放看起来正常。20:17–20:18 的手机请求发生在 `virtual-iso-native` 模式：条目和内容均为 **12《少数派报告》**，虚拟盘长度 97,196,113,920 字节。日志出现目录、盘尾和影片位置的随机 Range；最后一段从 `13150136320` 读取 250,675,200 字节，持续约 11.56 秒。媒体请求返回预期 307/206，未见 404/500，诊断错误日志为空。随机读取中取消旧请求是客户端行为，不能单独视为播放失败。此结果不再使用《禁闭岛》替换内容。

将本次成功的媒体源呈现方式合入生产 `InfuseChapterProxy`：只修改 ISO 容器、虚拟大小、沿用原分隔符的路径及直接播放能力；不再注入 `DirectStreamUrl`、`MimeType` 或覆盖 `AddApiKeyToDirectStreamUrl`。保留原始音轨、字幕、章节、时长和其他原生提示字段。认证、指纹、307 和 UDF 布局保持原实现。多个提示字段作为一组调整后通过，尚未单独证实是哪一个字段决定 Infuse 读取器行为。

本机已结束诊断并以 Helper-only 流程更新正常程序，恢复 8097 代理和 8099 管理页。Emby 8096、插件 DLL、配置、数据库和原盘文件均未变更，DSM/SPK 未部署。更新前备份：`artifacts/checks/local-update-20261103-202405-3533a1`。

正式程序回归通过：125 项应用检查、169 项 TS/播放列表检查、UDF 基础及 >4 GiB 载荷映射；本机实际 24 章经 UDF/BDInfo 回读，36 个 HTTP 区间及首/中/尾视频载荷一致。新增原生媒体源字段语义比较，确认只变化上述呈现字段，不受 JSON Unicode 转义差异影响。真实 ISO 条目 1886 的完整媒体源保持原样。手机章节列表及跳转效果还需单独确认，不能仅从起播成功推断全部章节交互已通过。

### CPU、内存和 I/O 开销

虚拟 ISO 不会把整部影片读入内存：只保存目录、MPLS/CLPI 等导航与源文件区间映射。本片 `MetadataBytes = 359642`，约 351 KiB；这不是进程总内存。每个活动媒体响应使用 64 KiB 转发缓冲，底层文件流及 HTTP 运行时另有缓冲开销。当前呈现缓存最多保留一分钟，缓存过期后可能重新读取导航和校验文件状态；每个 Range 仍会向 Emby 检查条目及播放权限，因此不是零开销。

2026-11-03 在正常 Windows Helper（12 个逻辑处理器）上测量了进程 `TotalProcessorTime` 增量；所有请求经本机 8097、真实鉴权及 Range 路径读取 NAS 原始 M2TS，客户端丢弃载荷，不写入视频文件：

| 条件 | 读取量 | 墙钟时间 | Helper CPU 时间 | 单核等效平均 | 整机等效平均 |
| --- | --- | --- | --- | --- | --- |
| 8 GiB 位置有界读取，未限速 | 256 MiB | 3.026 秒 | 890.62 ms | 29.43% | 2.45% |
| 同一区间重复读取，可命中系统缓存 | 256 MiB | 0.446 秒 | 375 ms | 84.08% | 7.01% |
| 9 GiB 位置，客户端按 100 Mb/s 节奏读取 | 64 MiB | 7.945 秒（含请求初始化） | 750 ms | 9.44% | 0.79% |

首次未限速传输约 84.59 MiB/s，重复读取约 574 MiB/s，均不能直接当作常规影片播放的持续负载。限速使用 64 KiB 客户端缓冲并按累计字节/12,500,000 B/s 控制读取时间。进程工作集从测量前约 95.9 MiB 到最后约 104.1 MiB，没有随 576 MiB 累计传输等量增长；该值包含整个 Helper，不是只有章节模块，亦非硬性内存上限。未启动 ffmpeg/ffprobe。

这些是短时本机观测值，只统计 Helper 进程 CPU，未包含 Emby、SMB 内核处理或 Infuse 解码，不代表 DSM 上的精确占用。普通播放的主要持续工作是文件读取和网络转发；启动、频繁跳转及多客户端并发还会增加导航、鉴权和请求处理开销。DSM 应在部署后按真实码率和并发数测量。

## 实现边界

- 当前为最小正片视图，不承诺 BD-J 菜单交互，也不暴露原盘的全部花絮。
- 使用纯托管 UDF 2.50 只读视图，无挂载和外部打包依赖。不能将能够被 UDF / libbluray 读取等同于已获得物理蓝光盘规范认证。
- 配置开关影响新请求；关闭后需要退出播放并重新打开影片，避免客户端继续使用旧虚拟 ISO URL。
- 手机实际虚拟盘起播已经通过；尚未单独确认章节选择/跳转、Infuse 媒体库模式、Apple TV 和 DSM 的实机回归。

## 代码与测试

`Proxy/Chapters` 分离 UDF 字节映射、Emby→MPLS 章节换算及 HTTP 代理。独立于 Emby 插件、扫描、截图模块。

```powershell
dotnet run --project tests/EmbyBlurayHelper.VirtualDisc.Tests -c Release
dotnet run --project tests/EmbyBlurayHelper.VirtualDisc.Tests -c Release -- --local 12 --native
dotnet run --project tests/EmbyBlurayHelper.VirtualDisc.Tests -c Release -- --local 12 --remote
dotnet run --project tests/EmbyBlurayHelper.VirtualDisc.Tests -c Release -- --write-fixture artifacts/checks/virtual-udf-kernel.iso
node tests/web/infuse-chapters.test.cjs
```

原生 ffprobe 的 `bluray:` 是协议，不把章节暴露为 `AVChapter`；原生验证检查 UDF / libbluray 能选择正确正片并读取真实音视频。章节列表必须额外用 MPLS 解析器比对，不能把 ffprobe 空 chapters 当成虚拟目录失败。

本机真实验证：《少数派报告》24 章、单 M2TS；《奇异博士2》29 章、三个 M2TS。全部章节经虚拟 UDF 与 BDInfo 解析回读，误差小于 45 kHz 标记精度。原版 ffprobe/libbluray 能从预览稀疏镜像选定正确播放列表并读取视频、音频、PGS；测试临时镜像仅写导航及片段首尾样本，检查后删除。HTTP 验证包括虚拟导航读取、媒体首/中/末字节一致、HEAD/Range/If-Range、416、旧指纹 409、无认证 401 和非 Infuse 透传。回归测试还检查 UDF 2.50 版本、两个分区映射、主/镜像 Metadata File Entry 和已关闭的卷完整性描述符。
