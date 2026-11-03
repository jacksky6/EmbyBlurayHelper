# 蓝光原盘中文字幕备注与 Infuse 显示研究

记录日期：2026-10-30（北京时间）。状态：按用户要求暂缓，保存研究结果，等待后续继续。

本文覆盖菜单备注提取、缓存方案及 Windows 本机 Infuse 显示试验。它是研究记录，不是已发布功能或部署说明；以下内容更新了分析目录中较早的可行性判断。

## 1. 目标与当前结论

用户主要在 Apple TV 上通过 Infuse 播放 Emby 中的 UHD / 1080p 蓝光文件夹原盘，无法使用原盘菜单。多个中文字幕只显示“中文”时，难以区分“国配简体特效”“简英特效”等版本。

目标仅为提取 DIY 菜单中与中文字幕对应的备注，并让播放中的字幕列表显示；不处理音轨备注，不扩展英文字幕命名。优先研究 BD-J，早期 HDMV 菜单暂不在首批范围。样本主要来自 CHDBits，但不能以发布后缀判断菜单框架。

截至暂停时：

| 环节 | 已验证结果 | 尚未完成或不能据此推断的内容 |
| --- | --- | --- |
| 布达佩斯原盘菜单分析 | 已恢复位图文字、按钮指令、播放列表字幕号、PID 的完整对应 | 不能推广为任意 BD-J 原盘均已支持 |
| 四条中文图片识别 | Windows OCR 能识别，四张预处理图片识别约 151 ms | 不是完整扫描耗时，也不是 DSM 性能数据 |
| 扫描一次、长期缓存 | 架构可行，已提出数据组织方案 | 后台任务、持久化索引和管理页面尚未实现 |
| Emby API 字幕标题补充 | 临时代理确实向实际 Infuse 返回了四条 Title / DisplayTitle | 本次 Infuse-Direct/8.5.9 未显示 |
| M2TS 流内名称补充 | 临时代理确实执行了 ATSC 0xA3 名称描述符补丁 | 本次 Infuse-Direct/8.5.9 仍未显示 |
| 现有 MKV 对照样本 | 用户确认 Infuse 可以显示详细中文字幕名称；文件内部也有对应 title | 尚未将研究原盘封装为 MKV 验证，更未实现按需封装 |

当前主要障碍是把名称交给 Infuse 并让它显示，而不是已经核实的这张盘无法提取文字。两条轻量试验未达到目标，因此暂停继续开发。现有辅助工具的语言补全与多片段原盘播放修复是另外的功能，不应因本研究结果而改动。

## 2. 环境与样本定位

| 项目 | 本次环境 |
| --- | --- |
| 项目目录 | `D:\GIT\Emby插件\EmbyBlurayHelper` |
| 研究时分支 | `feature/web-console`；工作区已有大量其他修改，本研究不提交或覆盖它们 |
| Windows Emby | 4.10.2.0，`D:\GIT\Emby插件\embyserver-win-x64-4.10.2.0` |
| Emby 本机地址 | `http://127.0.0.1:8096` |
| 辅助工具管理界面 / 原有代理 | 8099 / 8097 |
| Windows 局域网 IP | 研究时为 `192.168.1.10`；以后恢复前重新核实 |
| 实际客户端标识 | `Infuse-Direct/8.5.9`；本次结果针对直连模式，未覆盖全部客户端版本与媒体库模式 |
| 群晖 Emby | `192.168.1.109:8096`，本轮显示试验未修改 DSM 部署 |
| 布达佩斯 Emby 条目 | Windows `13`；DSM `31219`；条目编号不能跨服务器通用 |
| MKV 对照条目 | Windows `71`，《流浪地球 2》 |

不在本文保存 API 密钥、认证令牌或密码。临时验证程序从既有本机辅助工具设置中读取密钥；日志不记录查询参数、请求体或认证头。

研究原盘路径：

```text
\\192.168.1.109\web\webHlink\布达佩斯大饭店.The.Grand.Budapest.Hotel.2014.2160p.USA.UHD.Blu-ray.DV.HDR.HEVC.DTS-HD.MA.5.1-sh@CHDBits
```

## 3. 菜单文字在哪里，如何对应字幕轨

这张盘使用 `com.disc_art_authoring.bdjmenu` 与 HD Cookbook GRIN。四条中文备注保存在 `BDMV/JAR/00000/m9.png` 的像素中，不是 Java 中文字符串，也不是 MPLS 中已有的轨道标题。

| 文件 | 本次分析中的作用 |
| --- | --- |
| `BDMV/BDJO/00000.bdjo` | BD-J 应用描述 |
| `BDMV/JAR/00000.jar` | 菜单 Java 程序与数据资源 |
| JAR 内 `MenuXlet.class` | 指定外部图片目录、加载资源映射 |
| JAR 内 `images_menu.map` | 逻辑图片名称、大图文件和裁切矩形 |
| JAR 内 `menu.grin` | GRIN v27 状态、遥控器事件、图片及命令引用 |
| JAR 内 `XletShowCommands.class` | 菜单按钮触发后的选轨逻辑 |
| `BDMV/JAR/00000/m9.png` | 1438 × 522 菜单文字拼图 |
| `BDMV/PLAYLIST/00005.mpls` | 正片播放列表及 STN 轨道 PID、语言码 |
| `BDMV/STREAM/00009.m2ts` | 正片主片段，时长约 01:39:55.615 |

中文备注的确认链路为：原始 PNG 裁切 → GRIN 菜单状态 → Java 命令 → 播放列表内字幕号 → MPLS 轨道 PID。不能只凭四条中文轨道的顺序猜名称。

| 菜单文字 | 图片裁切 `(x,y,w,h)` | Java 命令 | 播放列表内字幕号 | 字幕 PID | 本机 Emby Index |
| --- | --- | --- | --- | --- | --- |
| 国配简体特效 | `(0,86,132,22)`，`img_0318.png` | `grinCommand410` | 3 | `0x12A2` / 4770 | 7 |
| 简英特效 | `(0,152,88,22)`，`img_0321.png` | `grinCommand411` | 4 | `0x12A3` / 4771 | 8 |
| 国配繁體特效 | `(0,218,132,22)`，`img_0324.png` | `grinCommand412` | 5 | `0x12A4` / 4772 | 9 |
| 繁英特效 | `(0,284,88,22)`，`img_0327.png` | `grinCommand413` | 6 | `0x12A5` / 4773 | 10 |

这四轨均为 PGS（编码类型 `0x90`），MPLS 语言为 `zho`。例如命令 410 调用 `manager.selectSubtitleStream(5, 3, true, i == 5)`，最终进入蓝光 `SubtitleControl.selectStreamNumber()`；参数中的 5 是播放列表，3 是列表内字幕号。这四个中文选项只切字幕，不自动切音轨。

盘内另有两条英文 PGS：字幕号 1 / PID `0x12A0` 与字幕号 2 / PID `0x12A1`。菜单同一个 ENGLISH 按钮根据是否使用评论音轨选择不同轨道，说明按钮数量和实际字幕轨数可能不一致。它们不属于拟新增的中文备注功能范围。

已对主菜单 MM 与播放中弹出菜单 PM 的字幕选择路径做静态核对，结果保存在 `analysis/validated-subtitle-routes.json`。未执行原盘 Java 应用，未复制整部正片用于菜单分析。

## 4. OCR 与菜单适配难度

在已裁好的文字图片上放大四倍并加白底，使用本机 Windows 简体中文 OCR：

| 原图文字 | OCR 输出 | 图片读取、解码及识别耗时 |
| --- | --- | --- |
| 国配简体特效 | 国 配 简 体 特 效 | 96 ms |
| 简英特效 | 简 英 特 效 | 13 ms |
| 国配繁體特效 | 國 配 繁 體 特 效 | 14 ms |
| 繁英特效 | 繁 英 特 效 | 28 ms |

合计 151 ms，不包括引擎初始化、菜单定位、裁切及预处理。“国”被识别为“國”的结果需要确认或规范化；不能称为逐字完全正确。DSM 后续需要单独选择、打包并测试跨平台 OCR，Windows OCR 不可直接用于群晖。

同一共享目录抽查到至少三种 BD-J 数据结构：

| 样本 | 观察结果 |
| --- | --- |
| 布达佩斯大饭店 / sh@CHDBits | Disc Art Authoring + GRIN；本次已还原中文按钮到 PID |
| 奇异博士 2 / sGnb@CHDBits、剪刀手爱德华 / sh@CHDBits | ByDeluxe / Disney MenuFramework，含外部合成 PNG |
| 少数派报告 / sh@CHDBits、极限审判 / LINMENG@CHDBits | 含 `.kthwomp`、`bluray_project.bin`、`resources.xml` 与合成 PNG |
| 斯诺登、穿越时空的少女 | 抽查未发现 JAR，本次未确认菜单类型 |

少数派报告的 `resources.xml` 有 `<!--ADD-->` 区块及类似 `en_button_setsubtitle_jtys_n_bt2020_hdr` 的图片名、裁切坐标，有助于定位中文选项。但其他两类框架尚未完成选轨关系还原，文件名缩写不能替代位图文字和指令核对。

## 5. Infuse 试验一：修改 Emby API 返回字段

临时入口：`http://192.168.1.10:8107`。请求转发方向为 Infuse → 8107 → 8097 → Emby 8096。

对本机条目 13 的精确媒体路径、媒体源 ID、字幕类型、语言和索引核对后，在影片详情、条目列表、GET / POST `PlaybackInfo` 的 JSON 中为四条中文字幕补充：

```json
{
  "Index": 7,
  "Type": "Subtitle",
  "Language": "zho",
  "Title": "国配简体特效",
  "DisplayTitle": "国配简体特效 (PGSSUB)"
}
```

未修改数据库、探测器、源盘或媒体字节。原 Emby 再次读取的流信息、其他影片、音视频和英文字幕字段均核对保持原样；未认证请求仍为 401。

结果：用户实际 Infuse 未显示这些备注。2026-10-30 19:12:51 的真实 `Infuse-Direct/8.5.9` 请求中，`POST /Items/13/PlaybackInfo` 与影片详情均记录 `titles-injected` 并返回 200，随后播放请求经过该入口。本次失败不能解释为根本没有连接测试代理。

结论边界：仅补这两个 API 字段未解决本次蓝光播放显示问题；不能据此断言所有 Infuse 模式、版本或容器都不使用 Emby 轨道字段。

## 6. Infuse 试验二：直接修改 M2TS 输出流

临时入口：`http://192.168.1.10:8117`。请求转发方向为 Infuse → 8117 → 8107 → 8097 → Emby 8096；保留前一次 API 标题补充和既有语言补丁，新增变量为 PMT 名称描述符。

既有 `TransportLanguagePatcher` 写入 ISO 639 描述符 `0x0A`，使客户端识别 `zho` 等三字母语言。这个字段不能直接放任意中文备注，因此本次在四个 PGS 的 PMT ES 描述符循环内追加 ATSC `component_name_descriptor`（`0xA3`）：

- A/65 `multiple_string_structure`，每个名称含中文和英文两个字符串。
- 中文字符串语言 `chi`，无压缩 UTF-16BE，mode `0x3F`。
- 英文字符串语言 `eng`，无压缩 mode `0x00`，对应 `CHS Dub FX`、`CHS ENG FX`、`CHT Dub FX`、`CHT ENG FX`。
- 轨道原有语言 `zho` 保留；字符串语言不等于改动轨道语言。

因为实际 PMT 空间不足，补丁借用后面完全相同的重复 PMT 作为续包。使用固定绝对窗口 4,620,288 字节，与既有语言补丁窗口对齐；保持包数、总长度、M2TS 到达时间戳和连续计数器，只修改 PSI、必要的 PUSI 标志、PMT 版本和 CRC。

这是按请求执行的内存实验，没有调用 ffmpeg、没有 remux、没有提前处理整部电影。窗口内无法找到足够的相同 PMT 续包时保留原 PMT；因此不保证每个 PMT 都含名称。ATSC 描述符被放入蓝光 TS 的兼容性也只做了结构试验，没有证明属于播放器必需支持的蓝光命名机制。

服务器侧验证：

- 对比 9,240,576 字节实际媒体样本，修改前后长度一致。
- 独立 PSI 解析器读到 4 个含全部四条名称的 PMT，CRC 有效。
- 48,119 个非 PMT 包逐字节相同，音视频与 PGS 载荷未变。
- 非对齐、跨窗口、重叠 Range 与对应完整样本位置字节一致；重复补丁不会重复添加。
- HEAD、后缀 Range、越界 416、错误认证 401 和其他条目转发通过检查。
- Emby 自带 ffprobe 仍能识别样本的 2 个视频、3 个音频、6 个字幕流及原有语言，但没有输出新增 title。

用户结果：仍未显示名称。2026-10-30 20:36 起的实际 `Infuse-Direct/8.5.9` 请求命中 8117：

- 首部、尾部 Range 及后续拖动请求都在日志中。
- 两个完成的尾部 Range 记录四个目标 PID 和 `patched: 6`。
- 起始播放请求结束时记录 `patched: 278`、`noSpace: 50`，管道处理约 744 MB；这是服务端管道统计，不等于客户端解析或显示名称的证明。

结论：实际客户端确实进入补丁链路，所试的 ATSC 名称描述符未解决本次显示问题。仍有部分 PMT 未修改、后续表更新和其他命名机制未测试，不能升级为“所有 TS 内嵌名称方案均不可能”。

## 7. MKV 对照样本及需要修正的推断

用户提供本机条目 71《流浪地球 2》，确认同一测试入口下 Infuse 可以显示详细字幕名称。文件为现有 MKV，并非本项目刚从蓝光生成的文件：

```text
\\192.168.1.109\web\webHlink\The.Wandering.Earth.II.2023.CHN.IMAX.BluRay.2160p.Atmos.TrueHD7.1.DoVi.HDR.x265.10bit-KKYY@CHDBits\The.Wandering.Earth.II.2023.CHN.IMAX.BluRay.2160p.Atmos.TrueHD7.1.DoVi.HDR.x265.10bit-KKYY@CHDBits.mkv
```

对照 Emby API 与直接运行原版 ffprobe，均读到下列轨道标题：

| 本机 Index | 语言 | 文件内 `stream.tags.title` |
| --- | --- | --- |
| 2 | chi | 简中国配特效HDR@KKYY |
| 3 | chi | 繁中国配特效HDR@KKYY |
| 4 | chi | 简英双语特效HDR@KKYY |
| 5 | chi | 繁英双语特效HDR@KKYY |

Emby 的 `DisplayTitle` 仍是通用的 `Chinese (默认 PGSSUB)`，`Title` 才是这些详细备注。日志记录了实际 Infuse 的 `/Items/71/PlaybackInfo` 和 `/Videos/71/stream` 请求；本次代理未对条目 71 打名称补丁。

这个结果支持继续研究 Matroska 轨道名称，但存在一个重要边界：文件内部和 Emby API 同时有相同标题，还没有通过“只改 API”或“只改容器名称”的对照试验把两者分开。因此，先前对话中“已经确定 Infuse 只读 MKV 内部标题”的说法过强，应以本段为准。

同样没有完成“布达佩斯转为 MKV 后能显示”的直接验证，没有实现原盘按需生成 MKV、SSD 缓存或播放代理集成。暂不能把现有 MKV 的成功描述为拟议封装方案已经完成。

## 8. 如果以后继续，缓存与功能应如何组织

用户要求播放按需，不接受一开始就强制处理整部原盘；优先内存处理，必要时允许 SSD。禁止以重新编码牺牲画质来换取显示。新增功能应独立开关、说明状态，并保留当前语言补全和多片段播放行为。

菜单提取适合后台完成，播放只读取索引：

1. 扫描原盘时提交去重任务，不让每次 ffprobe 同步执行反编译或 OCR。
2. 按实际菜单框架解析，恢复按钮到播放列表字幕 PID 的关系，对候选图片识字。
3. 在辅助工具持久数据目录保存每盘 JSON，如 `data/subtitle-labels/`。
4. 后续只在相关菜单文件、MPLS 或解析器版本变化时重算，播放请求读取已有结果。

索引至少包含原盘标识、相关文件指纹、解析器版本、播放列表号、PID、经核实的 Emby 流索引关联、名称、来源图片及裁切区域、提取状态、置信信息和手工修正。以播放列表与 PID 为主键，不能仅记录“第几个中文字幕”。

文件指纹仅针对 JAR、菜单图片、配置和相关 MPLS，不对整部 M2TS 求哈希。无菜单、暂不支持可缓存；权限或网络错误应可重试。手工修正与自动结果分开保存，重扫保留。首次未完成提取时仍可正常播放，并在管理界面显示状态。

DSM 数据可沿用套件的 `SYNOPKG_PKGVAR` 和现有升级保留机制。这些是后续设计建议，当前没有对应的新生产实现或 SPK。

显示链路若恢复研究，优先完成最小对照再扩展：

- 用现有 MKV 区分 API 标题与容器轨道名各自对 Infuse 的影响，覆盖直连/媒体库模式。
- 如继续测试 MKV，先对研究原盘制作几十秒小样本，仅复制选定流并加入名称，验证实际 Infuse；不先生成整部电影。
- `-c copy` 表示不重新编码，不自动保证蓝光双层 Dolby Vision、HDR 元数据、全部音轨与 PGS 均正确保留。需按具体封装器和轨道组合核实；不能仅凭命令宣称完整原盘体验无损。
- 仅当显示验证成功，再研究按需封装、拖动、Range / 时间定位、缓存淘汰及多客户端复用；不能把“边读边写 MKV”直接等同于已解决随机访问。
- 如继续 TS，应先消除未修改 PMT 或构造可控小样本，并核对目标客户端支持的描述符，避免反复让用户测试同一假设。

上述为恢复工作时的候选路线，当前不执行，也没有要求用户立即选择。

## 9. 本地研究文件与重现入口

主分析目录在仓库外：`D:\GIT\Emby插件\scratch\GrandBudapestMenu-20261030`。从本文所在目录可通过 `../../scratch/GrandBudapestMenu-20261030/` 访问。只 clone 本仓库不会取得这些脚本、原盘资源、反编译产物或日志；如果以后迁移机器，应另行保留该目录。

| 相对于分析目录的路径 | 内容 |
| --- | --- |
| `REPORT.md` | 最初的菜单与指令分析，包含资源哈希及细节 |
| `FEASIBILITY.md` | 初期 OCR、菜单框架抽查与缓存设计；显示判断以本文更新为准 |
| `inspect_resources.py` | JAR 字符串、资源与拼图分析 |
| `parse_menu.py` | GRIN v27、图片映射与播放列表解析 |
| `summarize_mapping.py` | 菜单状态、命令、轨号和 PID 核对 |
| `check-ocr.ps1` | Windows OCR 小验证 |
| `analysis/subtitle-track-mapping.json` / `.png` | 已核实名称、裁切与 PID；PNG 可直接目视复核 |
| `analysis/validated-subtitle-routes.json` | MM / PM 字幕选择路径核对 |
| `analysis/grin.json`、`image-map.json`、`playlists.json` | 结构化解析产物 |
| `input/`、`extracted/`、`decompiled/` | 原盘菜单元数据副本、解包及反编译文件 |
| `infuse-title-test/proxy.mjs` | 8107，仅修改 API JSON 的临时代理 |
| `infuse-title-test/requests.jsonl` | 第一轮实际和模拟请求日志 |
| `infuse-pmt-name-test/pmt-names.mjs` | ATSC 名称描述符、PMT 修改及固定窗口流处理 |
| `infuse-pmt-name-test/proxy.mjs` | 8117，媒体输出名称试验代理 |
| `infuse-pmt-name-test/validate.mjs` | 从本机设置读取密钥，验证实际媒体和 Range |
| `infuse-pmt-name-test/validation.json` | 服务器侧验证结果 |
| `infuse-pmt-name-test/sample-first-blocks.m2ts` | 9,240,576 字节诊断样本；不是完整影片 |
| `infuse-pmt-name-test/requests.jsonl` | 第二轮实际和模拟请求日志 |

工具版本：Python 3.12.0、Pillow 12.3.0、Node.js v22.18.0、.NET SDK 8.0.427。Java 使用分析目录中便携 Zulu JRE 17.0.21.0.3，反编译器 CFR 0.152；没有为此安装系统 Java。

菜单分析重现（在分析目录执行，需已有 `input/`）：

```powershell
python .\inspect_resources.py
python .\parse_menu.py
python .\summarize_mapping.py
powershell -File .\check-ocr.ps1
```

两个测试入口不是开机服务。恢复前检查端口和进程，避免重复启动；启动顺序为原 Emby 8096、辅助工具 8097、API 代理 8107、PMT 代理 8117：

```powershell
& 'E:\ProgramFiles\nodejs\node.exe' 'D:\GIT\Emby插件\scratch\GrandBudapestMenu-20261030\infuse-title-test\proxy.mjs'
# 在另一个终端执行；上一个命令会保持运行。
& 'E:\ProgramFiles\nodejs\node.exe' 'D:\GIT\Emby插件\scratch\GrandBudapestMenu-20261030\infuse-pmt-name-test\proxy.mjs'
# 需要两个代理已运行；在 PMT 试验目录的另一个终端执行。
& 'E:\ProgramFiles\nodejs\node.exe' .\validate.mjs
```

2026-10-31 已清理运行实例：8107 / 8117 研究代理、multiclip-test、batch-release9、ui-tabs-preview、ui-top-level-preview 均已停止，相关配置、日志和研究文件保留；未发现这些实例的开机自启项。本机仅保留 Emby 8096、主辅助工具管理页 8099 和代理 8097，三个入口均已验证返回 HTTP 200。Infuse 的辅助工具连接统一使用 8097；上述研究端口只有显式恢复研究时才重新启动。

## 10. 相关代码与资料

项目代码：

- `src/EmbyBlurayHelper.Proxy/TransportLanguagePatcher.cs`：已有 ISO 639 语言补丁，支持固定边界 Range；不能直接承载任意中文标题。
- `src/EmbyBlurayHelper.Proxy/EmbyProxy.cs`：既有 Infuse 媒体代理，生产实现未加入本研究的名称补丁。
- `src/EmbyBlurayHelper.ProbeWrapper/BluRayLanguageAugmenter.cs`：已有 ffprobe 语言补全，不含菜单备注提取。
- `packaging/synology/scripts/preupgrade`：现有 DSM 升级数据保留逻辑，可供后续缓存设计参考。

本机源码证据：`D:\GIT\Emby插件\_dnspy_mediaencoding_tmp\Emby.Server.MediaEncoding\Probing\ProbeResultNormalizer.cs` 约第 732 行，把 `streamInfo.tags["title"]` 写入 `MediaStream.Title`。这是服务端接入字段的证据，不代表已为菜单备注实现扫描持久化或客户端支持。

本机旧版 Emby FFmpeg 源码：`D:\GIT\Emby插件\ffmpeg-2022_05_27_public\libavformat\mpegts.c`。它和本次检查的上游 MPEG-TS 解复用代码均未发现把本次 ATSC 0xA3 映射到通用轨道 title 的逻辑；不能用它替代对闭源 Infuse 内部实现的核实。

公开参考：

- [TSDuck ATSC ComponentNameDescriptor](https://github.com/tsduck/tsduck/blob/master/src/libtsduck/dtv/descriptors/atsc/tsComponentNameDescriptor.cpp)
- [TSDuck ATSC multiple_string_structure 编解码](https://github.com/tsduck/tsduck/blob/master/src/libtsduck/dtv/atsc/tsATSCMultipleString.cpp)
- [FFmpeg MPEG-TS 解复用器](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mpegts.c)
- [Infuse 论坛：字幕命名讨论](https://community.firecore.com/t/19630)
- [Infuse 论坛：轨道名称讨论](https://community.firecore.com/t/17694)

论坛讨论仅作线索；外置字幕文件名或其他格式的轨道显示行为，不能直接证明蓝光内嵌 PGS 会采用同一信息来源。
