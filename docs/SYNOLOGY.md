# EmbyBlurayHelper：DSM 7.2 x86_64

此包适用于 Intel/AMD 64 位群晖的 DSM 7.2 及以上系统，包含 .NET 8 运行时。
不需要 Docker、Web Station、Python 或在 NAS 上安装 .NET。
首次发布标记为测试版；包结构和生命周期在 WSL 中验证，仍需 DSM 真机安装验证。

## 安装与连接

1. 在 DSM「套件中心 → 手动安装」中选择 `EmbyBlurayHelper-0.2.0-25-dsm7.2-x86_64.spk`，完成安装并启动。已安装旧版可直接升级，保留设置、账号和原盘语言任务记录。
2. 打开 `http://NAS-IP:8099`，用户名 `admin`，密码 `123456`。也可从 DSM 的应用图标打开。
3. 在 DSM「控制面板 → 共享文件夹 → 电影所在共享文件夹 → 编辑 → 权限」中，
   选择「系统内部用户」，给 `embyblurayhelper` **只读**权限。对所有需要读取的媒体共享文件夹重复此步骤。
   如果某个子目录已有明确拒绝规则，还需在 File Station 的目录权限中检查继承。
4. 在工具的「连接与账号」中填写 `http://127.0.0.1:8096` 和 Emby 管理员 API key。
   原版 ffprobe 路径可留空，点击「保存并测试连接」会从 Emby 自动获取并保存可访问的路径，再点击「检查 ffprobe」。已有手动配置会保留；也可单独点击「从 Emby 获取」或「检测本机 Emby」。
5. 在「单片检查」中输入影片 ID。NAS 路径应类似 `/volume1/media/电影`，不能填 Windows UNC 路径。
6. 在左侧「语言与时长」页打开「Emby 探测语言补全」开关，工具会自动备份并切换探测器，确认显示「已接入 Emby」。
7. Infuse 的 Emby 连接地址填写 `http://NAS-IP:8097`，使用原 Emby 账号。

已有影片的语言、规格和章节在左侧「媒体库处理」中补全：先检查媒体库，再勾选媒体库和需要补全的内容，按语言与时长 → 音视频规格 → 原盘章节的顺序在后台逐项执行，同一时间只有一项读取原盘。
支持任务进度、停止、继续和逐部核验，详见[批量检查与补全](LIBRARY-LANGUAGE.md)。

管理端口为 8099，播放代理端口为 8097，原 Emby 仍使用 8096。
启用 DSM 防火墙时，可在「内置应用程序」中选择本套件的管理、播放服务并按需允许来源。
如果在 Web 设置中更改端口，需要相应调整防火墙，并在套件中心停止、启动本套件；DSM 应用图标固定指向默认 8099。

## 数据与权限

- 主程序：`/var/packages/EmbyBlurayHelper/target/EmbyBlurayHelper`
- Emby 自定义探测器：`/var/packages/EmbyBlurayHelper/target/probe.sh`
- 持久数据：`/var/packages/EmbyBlurayHelper/var/data/`
- 启动日志：`/var/packages/EmbyBlurayHelper/var/startup.log`
- DSM 套件操作日志：`/var/log/packages/EmbyBlurayHelper.log`

套件以独立的 `embyblurayhelper` 系统用户运行，不以 root 运行，不修改媒体文件或原 Emby 可执行文件。
用户名和密码明文存放在持久数据中的 `account.json`，可在「Emby 连接 → 管理账号」修改。
API key 保存在 `settings.json`，登录后页面默认显示保存的值。

Emby 调用 `probe.sh` 时，只读取单独导出的 `probe-settings.json`，其中只有原版 ffprobe 路径、语言补全开关、规格识别开关及独立增强 ffprobe 路径。
它不读取 Web 账号、API key、会话密钥，也不需要写入本套件的数据目录。
探测模式的错误信息由 Emby 捕获到自身日志，Web 服务及 Infuse 代理日志显示在工具页面中。
即使辅助服务暂时停止，已导出的探测配置仍可被 Emby 使用。

原版 ffprobe 及其父目录需要可被 `embyblurayhelper` 执行、读取；若「检查 ffprobe」报权限错误，
保留错误及实际路径排查，不要递归放开 Emby 整个数据目录的权限。

从 `0.2.0-2` 起，检查和实际探测都会自动从 Emby 套件根目录启动使用相对加载器的原版 ffprobe。
这修正了文件存在、权限正常却报 `No such file or directory` 的问题，无需扩大 Emby 套件目录权限。
探测器启动失败时，Web 提示和日志会显示具体的系统错误；依赖库错误也会保留原版程序的错误输出。

从 `0.2.0-5` 起，连接测试与「从 Emby 获取」优先读取 `OriginalProbePath`，不会把已接入的辅助探测器当成原版 ffprobe。该自动获取逻辑同样适用于 Windows、Docker、Linux、macOS，输入框下提供各平台路径参考。远程主机或容器的路径若在本工具环境不可访问，只显示提示，不自动填入保存。详见[路径参考](WEB-CONSOLE.md#原版-ffprobe-路径参考)。

从 `0.2.0-4` 起，「Emby 探测语言补全」开关立即生效并自动保存，无需再单独接入或点击「保存设置」：

- 打开时检查原版 ffprobe、备份当前探测器配置，再切换为本工具并启用语言补全。
- 关闭时恢复本次接入前的探测器配置，并关闭补全。若原来使用其他自定义探测器，会恢复该探测器。
- 若当前正在使用其他自定义探测器，打开开关会先提示确认。关闭功能不会覆盖其他探测器的设置。
- Emby 拒绝修改、无法连接或备份缺失时会显示失败原因，并重新读取实际状态，不会将失败标记成成功。

接入状态仅在登录、进入概览或「Emby 探测语言补全」页、修改配置后以及手动刷新时读取，不再定时查询 Emby。运行日志和本工具的统计仍按原频率刷新。

旧版“已接入但补全已暂停”的配置会保留，可打开开关重新启用，也可使用提示下的“恢复原探测器”操作。已接入表示 Emby 已配置为调用本工具，已有影片需再次发生实际媒体探测才会更新语言信息。

## 升级与卸载

### 0.2.0-25：插件封面

随附插件更新为 **0.1.8**，新增突出 Blu-ray、UHD 原盘增强与 Infuse 的 16:9 封面。图片嵌入 DLL，Emby 插件列表直接显示，不依赖外部图片地址。已安装 0.1.7 的用户升级本套件后，可在 Emby 插件页面检查并安装 0.1.8，再重启 Emby，无需手动复制 DLL。

### 0.2.0-24：Emby 插件内检查并安装更新

随附插件更新为 **0.1.7**。本次需要最后手动替换一次 DLL 并重启 Emby，旧版 0.1.4 / 0.1.6 没有更新入口。之后升级 SPK 或 Helper 程序时，可在 Emby「插件 → 我的插件 → EmbyBlurayHelper」中点击「保存并检查更新」「安装更新」，由 Emby 账号完成 DLL 校验、备份和替换，再手动重启 Emby。默认来源为同机 `http://127.0.0.1:8099`，可修改端口或容器地址；不需要 Helper 登录密码或 API key。具体流程见随包 [插件更新](PLUGIN-UPDATES.md)。

Helper 的更新 DLL 嵌入主程序，无需额外维护下载目录。更新器独立于原盘读取和播放模块，不自动重启服务，也不定时检查；升级 SPK 仍保留账号、设置、任务和日志。

### 0.2.0-23：概览横幅与媒体库操作改进

概览页新增 Emby / BlurayHelper / Infuse 横幅，产品图标随程序嵌入，无需浏览器联网加载；媒体库信息、功能状态、待办提醒和最近活动按区块展示，适配桌面与手机。随包提供 `BRAND-ASSETS.md` 记录图标来源。

媒体库列表将影片 ID 独立为第一列，调整列宽和行高，增加悬停高亮并修正不可用分页按钮的鼠标样式。「语言与时长」支持选择「含已核验」，可对已有完整信息重新核验；统计区分电影总数、蓝光原盘、UHD 蓝光、1080P 蓝光和规格未识别的原盘。

在套件中心直接手动安装本版 SPK 升级，**不要先卸载**。配置、登录账号、API key、日志和任务记录保留；端口不变。完成后打开 `http://NAS-IP:8099/#overview` 并强制刷新页面。随附 Emby 插件仍为 **0.1.6**，已加载该版本的无需替换 DLL 或重启 Emby；本次仅更新 Helper。

### 0.2.0-22：Infuse 原盘章节，管理页面重新整理

管理页面按「Emby 媒体库 / Infuse 播放 / 工具 / 设置」分组，页面改名为「语言与时长」「音视频规格」「章节与截图」「播放增强」「单片检查」「连接与账号」。所有开关点击后立即保存；路径、地址和账号在各自卡片内保存，不再有页面顶部的「保存设置」。语言、规格、章节三个批量任务使用同一套控件和措辞；「单片检查」一次显示一部影片的语言、规格、章节和 Infuse 呈现方式。新增「媒体库处理」：三项补全共用一次检查和一个媒体库选择，在后台按顺序执行，关闭浏览器不影响；Helper 重启会中止尚未开始的后续项，已完成的结果保留。概览显示 Emby 连接、探测器、插件版本和 Infuse 代理地址，以及需要处理的事项。

左侧「播放增强」页的「Infuse 原盘章节」开关**默认关闭**，升级后不会自动改变 Infuse 的播放方式。开启后，经过 8097 的 Infuse 会把带 Emby 章节的 BDMV 视为一张虚拟 ISO，从中读取这些章节。不预先生成文件，不转码，不 remux，视频、音频、字幕仍按需从原文件读取。真实 ISO 和其他客户端不受影响。

- Infuse 用的是 Emby 已保存的章节。NAS 上自动生成的「章节 1、2……」不是原盘章节，开启前先在「章节与截图」页对相关媒体库补全一次。
- 随附插件仍为 **0.1.6**。从 0.2.0-17 或更早版本升级时，要替换 Emby 插件 DLL 并重启 Emby（见 0.2.0-18、0.2.0-19 说明）。
- 开启或关闭后，在 Infuse 中强制退出 App 再打开，让它重新同步媒体库。Infuse 按同步时得到的格式决定读取方式。
- 开启后，符合条件的 BDMV 不再经过「Infuse UHD 语言补写」和「多片段原盘兼容」，音轨、字幕语言由 Infuse 从虚拟 ISO 内的 MPLS / CLPI 读取。
- 运行日志每个 Infuse 媒体请求都有一行 `Infuse metadata:`，列出改写和未处理的条目、分页及耗时。无法唯一匹配正片的原盘列为未处理，保持原播放方式。
- 每部原盘只在第一次同步时读取导航文件（最多同时 4 部）。结果保存在数据目录的 `infuse-disc-listings.json`，重启和升级后保留。之后的同步只用 Emby 数据库里的记录，不访问媒体硬盘。只有 Emby 重新扫描、该片的路径、大小、时长或章节有变化，或者实际播放这部片时，才会重新读取。目录暂时不可访问（硬盘未挂载、权限错误）时，6 小时后再试。
- 已在 Windows 本机 Emby 4.10.2.0 与 iPhone Infuse 8.5.9 验证单片段和多片段 BDMV 的起播、章节数、跳转及重复播放。DSM 真机与 Apple TV 尚未验证。

### 0.2.0-19：原盘章节截图，跟随 Emby 设置

新增 BDMV / Blu-ray ISO 章节截图适配，是否提取、何时提取、章节/秒数间隔及 BIF 保存位置均使用 Emby 每库原有设置。Emby 原生提取任务成功后自动运行无独立定时器的原盘补充任务，显示进度并可停止。HDR / 兼容 Dolby Vision 底层截图使用原版 Emby ffmpeg 的色调映射；缺失映射时明确跳过，避免偏色。随附插件 **0.1.6**，必须替换 DLL 并重启 Emby。参见随包 `DISC-THUMBNAILS.md`；Windows 已实测，DSM 仍需升级真机验证。

### 0.2.0-18：原盘原生章节补全

新增左侧「原盘章节」独立页面、自动入库开关、按媒体库批量处理、进度/停止/继续和详细日志。仅 BDMV / Blu-ray ISO，直接读取 MPLS 导航，无需 ffprobe。保留自定义章节和片头片尾标记。随附插件为 **0.1.5**，升级后需要替换 Emby 插件 DLL 并重启 Emby；详见 [原盘章节补全](NATIVE-CHAPTERS.md)。

### 0.2.0-17：原盘规格识别与按媒体库批量处理

本版新增左侧一级页面「音视频规格」，只处理 BDMV 原盘目录和蓝光 ISO，普通 MKV、MP4 和独立 M2TS 不进入处理计划。可在 Emby 入库扫描时自动补充真实确认的 Atmos / Dolby Vision，也可对已有影片按媒体库选择、全选、查看进度、停止并继续；结果和核验记录持久保存。已有语言、时长、编码、轨道顺序和自定义标题会保留。

**本版随附插件为 `0.1.4`，升级 SPK 后还需替换 Emby 插件 DLL 并重启 Emby。** 从 `/var/packages/EmbyBlurayHelper/target/emby-plugin/EmbyBlurayHelper.Plugin.dll` 复制到实际 Emby 数据目录的 `plugins`，确认文件对 Emby 运行用户可读。SPK 升级不会自动写入 Emby 插件目录；本版批量规格核验依赖新版插件能力。

规格识别需要另外配置可在 DSM x86_64 运行的现代 ffprobe；**此 SPK 不附带增强 ffprobe**。将它放在独立且运行账号可读取、执行的位置，在「音视频规格」填写路径并检查后开启。不能填写 Windows 的 `.exe` 路径，也不要覆盖 Emby 自带的 ffprobe / ffmpeg。基础探测仍调用原版，现代 ffprobe 仅为原盘补充新增规格。增强引擎缺失时可继续使用已有语言补全和 Infuse 功能。

已有影片：进入「音视频规格」→「检查媒体库」→ 勾选所需库或全选 →「补全所选媒体库」。默认跳过有效的已核验记录；没有发现额外规格的成功检测也会保留记录，避免重复探测。逐片结果和运行日志会说明实际识别内容。详细说明见随包 `MEDIA-FEATURES.md`。

本版功能已在 Windows 本地 Emby 4.10.2.0 完成真实 BDMV/ISO 批量验证；Linux 安装包另做 WSL 包结构和生命周期检查。DSM 真机的现代增强引擎及完整规格识别仍需部署后验证。

`0.2.0-14` 将 ISO 探测统一为内置 UDF/M2TS 读取，不再需要原版 ffprobe 的 bluray 输入协议，也不保留原生路径重试。只提取小型导航文件，按需将 ISO 内媒体提供给 Emby 原版 ffprobe；补全整片时长、音轨和字幕语言，保留 DV 增强层及 HD/core 音轨。无需挂载、整盘解压、remux 或转码。临时 localhost 端口仅用于探测，结束即关闭，不需要对外开放新端口。BDMV 核验窗口调整为每部最多 15 秒，每秒检查一次，避免单部异常音轨让批量任务等待 5 分钟。

原盘补全页显示实际加载的插件版本、时长能力和原版 ffprobe 的 HTTP/TCP 输入能力。进入页面、修改连接配置、检查电影库完成或手动刷新时读取，不定时轮询。含 ISO 的批量计划开始前再次检查；条件不满足时跳过 ISO，BDMV 仍可补全。缺轨、读取失败会显示具体原因，不会计入成功。插件未安装、不可读或未重启等情况会显示「未加载」，不凭 API 断言唯一原因。

`0.2.0-11` 包含 ISO 音轨、字幕语言和缺失时长、大小的补全，以及按媒体库检查、按版本核验的统一原盘补全计划。已有有效时长和大小保留，不修改原盘文件，不修复或改变 Emby 的 ISO 播放方式。功能模块划分见随包的 `ARCHITECTURE.md`。

ISO 批量补全、异常 BDMV 修复需要安装或更新随附的 Emby 插件 0.1.2：

1. 从 `/var/packages/EmbyBlurayHelper/target/emby-plugin/EmbyBlurayHelper.Plugin.dll` 复制 DLL 到 **Emby 数据目录下的 `plugins`**。实际数据目录以 Emby 控制台显示为准，只复制此 DLL。
2. 重启 Emby，然后在本工具重新检查电影库，再运行所选媒体库的补全任务。套件升级不会自动写入 Emby 的插件目录。

旧版在 DSM Emby 4.10.2.0 上因 `bluray: Protocol not found` 失败，单纯重启无效。新版统一读取路径已通过 Windows 及 WSL Linux 配合官方 DSM 4.10.2.0 原版 ffprobe 的真实 ISO 测试；NAS 套件安装后的端到端结果仍需真机核验，不能用 WSL 代替。不要用通用 ffprobe 替换 Emby 原文件。

`0.2.0-16` 随附插件更新为 `0.1.2`，本次需要再次替换 DLL 并重启 Emby。新增异常 BDMV 直接探测、唯一 M2TS 的损坏导航处理、菜单重复时间区间过滤和旧 ISO 结果修复。更新后重新检查电影库，旧 ISO 核验记录会列入一次性待处理计划；启动补全才会重读正片轨道和时长，保留外部字幕。原盘明确标为 und 的轨道仍显示未识别。未更新 DLL 时继续使用旧 BDMV 刷新，无法获得新的异常盘修复；页面会说明已加载的版本和能力。

从 `0.2.0-6` 起，「运行日志」页提供「清除全部」，确认后删除本工具的当前及历史运行日志，并同步清空页面记录。清除不受日志级别筛选限制，不影响账号、配置和运行统计；后续日志正常记录。Emby 日志、DSM 套件操作日志和 `startup.log` 不在清除范围内。

从 `0.2.0-7` 起，新增独立的「Infuse 多片段原盘兼容」功能，默认关闭，可在左侧「Infuse 播放」页面开启，立即保存并生效；概览和 Infuse 页面显示状态，附工作方式说明，与语言补写分别控制。此功能兼容能唯一匹配播放列表的多片段原盘静态读取 404/500。

`0.2.0-8` 修正 DSM 兼容读取：Emby DSM 4.10.2.0 的 ffprobe/ffmpeg 未启用 libbluray，会拒绝 `-playlist`；旧版仅检查文件存在未发现此能力差异。新版用内置读取器验证 MPLS 和 CLPI，确认未加密、单角度、单时间域、完整片段、无缝连接、包数量及文件长度后直接按需读取，不启动 ffmpeg，不编码、不重新封装、不写临时视频文件。仍需完整原盘读取权限。复杂或裁剪标题暂不兼容，保留原响应并记录错误详情。旧版原生读取路径已移除；除 DSM 缺少协议外，原生定位也可能退回访问点，无法保证精确的 HTTP 字节范围。升级保留开关和原有配置。

更新时在套件中心手动安装新版本 SPK，直接升级；持久目录中的账号、API key、日志和探测器备份会保留。
包中不携带本机账号、API key 或日志。

卸载前请先在 Web 中关闭「Emby 探测语言补全」，确认已切回原探测器。直接卸载时，套件也会尝试恢复仍指向自身的探测器设置，
只恢复备份中的自定义探测器字段，不改 Emby 其他编码选项。若 Emby 不可达或 API key 失效，卸载会停止，
请先恢复连接并完成探测器恢复，避免 Emby 留下失效路径。升级流程不会执行这个恢复操作。
完整卸载前可自行备份持久数据；卸载脚本不删除任何媒体或 Emby 文件。

## 启动失败

查看上述启动日志及 DSM 套件操作日志。安装阶段会先检查打包的 .NET 运行时能否启动；
缺失的系统库会显示在日志中。还需确认 8099、8097 未被其他程序占用。

## 重复构建

Windows 的 .NET SDK 放在 `E:\ProgramFiles\dotnet-8`。在项目根目录运行：

```powershell
.\scripts\build-synology.ps1
# 发布后仅重新打包：
.\scripts\build-synology.ps1 -SkipPublish
# 下一次版本升级必须递增最后的套件构建号：
.\scripts\build-synology.ps1 -Version 0.2.0-26
```

打包使用现有 WSL `Ubuntu-26.04` 的 Python 标准库；无需额外安装 SPK SDK。
结果位于 `artifacts/downloads/`，旁边提供 SHA256 校验文件。
Linux / GitHub Actions 在发布 `linux-x64` 后可直接运行：

```sh
python3 scripts/package-synology.py --version 0.2.0-25
python3 tests/synology/check-spk.py artifacts/downloads/EmbyBlurayHelper-0.2.0-25-dsm7.2-x86_64.spk
```

生命周期及跨用户探测测试可在 Linux 运行；加 `--cross-user` 时用 root 启动测试，
测试会在临时目录内分别以现有的 `nobody`、`daemon` 身份模拟两个套件，不创建系统账号：

```sh
python3 tests/synology/check-spk.py artifacts/downloads/EmbyBlurayHelper-0.2.0-25-dsm7.2-x86_64.spk --lifecycle --cross-user
```

打包依据群晖官方 [套件格式](https://help.synology.com/developer-guide/synology_package/INFO.html)、
[权限配置](https://help.synology.com/developer-guide/privilege/privilege_config.html)、
[生命周期](https://help.synology.com/developer-guide/synology_package/scripts.html) 和
[DSM 7 变更](https://help.synology.com/developer-guide/breaking_changes.html)。
