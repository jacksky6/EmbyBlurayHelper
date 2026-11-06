# EmbyBlurayHelper 0.2.0

统一版提供 Web 管理界面、Emby 探测语言补全、Infuse UHD 视频流语言补写和独立的多片段原盘兼容开关。
已有两个独立工具保留，统一版直接复用它们的解析和视频流处理源代码。

「媒体库处理」页可按媒体库检查当前电影的音轨、字幕语言，选择部分或全部媒体库安排补全，显示任务计划、逐库进度、核验结果和日志。详见[批量检查与补全](LIBRARY-LANGUAGE.md)。

「章节与截图」提供原生时间点补全，并只读展示 Emby 各媒体库的截图设置。插件 0.1.6 可跟随原有设置和任务生成 BDMV / ISO 章节截图，包含 HDR / 兼容杜比视界底层的 SDR 色调映射及安全检查，详见[原盘章节截图](DISC-THUMBNAILS.md)。

## Windows

Emby 插件从 **0.1.7** 起提供自己的更新配置页：先升级 Helper，再在 Emby「插件 → EmbyBlurayHelper」中检查并安装配套 DLL。首次仍需手动安装支持更新的 DLL，此后不必每次 SSH 复制；成功后手动重启 Emby 生效。默认更新来源为同机 `http://127.0.0.1:8099`，无需 Helper 密码。详见 [插件更新](PLUGIN-UPDATES.md)。

解压后运行 `EmbyBlurayHelper.exe`。浏览器打开 `http://localhost:8099`。
默认用户名 `admin`，默认密码 `123456`。登录后可在“Emby 连接 → 管理账号”中修改用户名和密码。
账号明文保存在 `data/account.json`，与 Emby 账号独立；修改后需重新登录。
从其他设备访问时，将 localhost 换成 Windows 电脑的局域网 IP。

## 群晖 DSM 7.2 SPK

Intel/AMD 64 位机型可使用 SPK，在套件中心手动安装。安装步骤、共享文件夹权限及构建方式见
[群晖安装说明](SYNOLOGY.md)。独立 Linux 便携包继续可用。

## Linux 便携包

根据处理器选择 linux-x64 或 linux-arm64 包。通过 SSH 解压到有写入权限的独立目录，运行：

```sh
chmod +x EmbyBlurayHelper start-linux.sh
./EmbyBlurayHelper --helper-version
./start-linux.sh
```

浏览器打开 `http://NAS-IP:8099`，默认使用 `admin / 123456` 登录。
初次测试建议前台运行，方便看到启动错误；运行账户必须可以读取原盘的
BDMV/PLAYLIST、CLIPINF 和 STREAM 目录及文件信息，并执行 Emby 原版 ffprobe。
同时，Emby 调用探测模式时也需要读写本工具的数据目录；本次原生测试建议让辅助工具与
Emby 使用同一运行账户。若使用不同账户，需要明确配置共享组/DSM ACL，
不能只让管理员账户可以打开目录。配置与账号文件默认仅创建它们的账户可读写。
切勿使用 Windows UNC 路径作为 Linux 路径；统一版需要能直接读取 Emby 返回的原盘目录。

程序自带 .NET 运行时，无需安装 .NET SDK。统一版使用 invariant globalization，
不依赖额外 ICU 包；仍需要与 CPU 架构匹配的 glibc、libstdc++ 等系统库。
WSL Ubuntu 原生运行验证不能替代 DSM 7.2 真机验证。遇到依赖错误时保留完整报错，
不要替换 DSM 系统库。SPK 已提供打包与模拟生命周期验证，尚需 DSM 真机安装验证。

## 在管理页面接入

1. 在“Emby 连接”页面填写 Emby 后台地址，默认 `http://127.0.0.1:8096`。
2. 填写 Emby 管理员 API key，点击“保存并测试连接”。ffprobe 路径留空时，连接成功会自动读取 Emby 的 `OriginalProbePath`，确认文件在本工具环境可访问后填入并保存。
3. 点击“检查 ffprobe”验证能否运行。也可点击“从 Emby 获取”使用表单中的地址和 API key 获取路径，或者使用“检测本机 Emby”和手动填写。已有手动路径不会被连接测试自动覆盖。
4. 在左侧“Emby 探测语言补全”页面打开开关。开关立即生效并自动保存，接入前会备份原探测器配置；
   关闭开关会恢复本次接入前的配置。只修改自定义 probe 字段，保留当前其余编码设置。
   旧版 ProbeWrapper 已经正常工作时，也可以继续使用，稍后再切换。
5. Infuse 连接 `http://NAS-IP:8097`，使用原 Emby 账户。
6. 「单片检查」输入影片 ID，一次查看这部影片的语言与时长、音视频规格、章节和 Infuse 呈现方式，以及原盘目录与可解析的语言映射；也可以在这里单独「识别并保存规格」。

当前默认端口：管理 8099、播放代理 8097、Emby 后台 8096。不同进程不能绑定同一 IP 的同一端口。
若希望客户端保留 8096，先自行将 Emby 的内部监听改为 8098，
然后将代理监听设为 8096、Emby 后台地址设为 `http://127.0.0.1:8098`。
还需确认 Emby 对外公布的地址指向代理入口。工具不会自动修改 Emby 端口或重启 Emby。

“Emby 探测语言补全”开关会立即切换探测器并自动保存。Infuse 开关、原版 ffprobe 路径、Emby 后台地址点击保存后生效。
接入状态只在登录、进入概览或“Emby 探测语言补全”页、修改后及手动刷新时查询，不定时检查 Emby。
管理或代理监听地址变更需要重新启动本工具。页面会显示重启提示。

## 清除运行日志

「运行日志」页右上角提供「清除全部」，确认后删除本工具保存的当前、历史及轮换日志，并清空日志表格和概览中的最近活动。此操作不受当前日志级别筛选限制，清除后无法恢复；账号、API key、功能配置和运行统计保持不变，后续日志继续正常记录。不会删除 Emby 日志或 DSM 启动、套件操作日志。

## 原版 ffprobe 路径参考

自动获取按 Emby API 返回的 `OriginalProbePath` 工作，同一逻辑适用于 Windows、DSM、Docker、Linux 和 macOS，不根据浏览器所在系统猜路径。需要该 Emby 版本和 API key 允许读取 `Encoding/FfmpegOptions`；旧版本未返回该字段时仍可手动配置。`ProbePath` / `CustomProbePath` 可能已经是辅助工具，不能用于识别原版文件。

| 平台 | 常见位置 |
| --- | --- |
| Windows | `%APPDATA%\Emby-Server\system\ffprobe.exe`；便携版为 Emby 解压目录下的 `ffprobe.exe` |
| 群晖 DSM | `/var/packages/EmbyServer/target/bin/ffprobe` |
| Docker | 以镜像内路径为准，例如 `/bin/ffprobe`、LinuxServer 镜像的 `/app/emby/bin/ffprobe` |
| Linux | `/opt/emby-server/bin/ffprobe`；部分安装为 `/usr/lib/emby-server/bin/ffprobe` |
| macOS | 解压安装为 EmbyServer 目录下的 `ffprobe`；App 安装版从 API 获取或在应用包中查找 |

这些是参考位置，安装方式和版本不同可能变化。Windows 的 `%APPDATA%` 是安装 Emby 的用户目录，手动填写时需展开为完整路径。macOS 路径参考不表示当前已发布 macOS 便携包。

IP 和 API key 能读取路径，不能让远程文件自动出现在本地。如果辅助工具与 Emby 不在同一主机或容器，返回路径可能不可用；界面会显示原始路径和提示，需要配置辅助工具实际可执行的路径及配套依赖。即使文件存在，也需“检查 ffprobe”验证加载器、依赖和执行权限。始终使用 Emby 自带版本。

## 文件与凭据

Web 页面内嵌在同一个可执行文件中，不需要前端服务器或额外数据库。
运行数据单独保存在程序旁的 `data` 目录：

- `settings.json`：设置与 Emby API key；登录后页面默认明文显示已保存的 API key，可编辑或清空后保存。
- `account.json`：明文保存管理用户名和密码，默认 `admin / 123456`；另含用于使旧会话失效的版本标记。
- `keys/`：管理会话密钥。
- `logs/`：按日和大小轮换的日志，单文件约 4 MiB，最多保留 14 个文件。
- `emby-probe-backup.json`：本次接入前的 probe 配置备份，重新开启时备份当时的实际配置。

升级时替换程序，保留 data。备份 API key 与会话数据时按私密文件保存。
从管理密钥版升级时，会自动创建默认账号，保留原 Emby 设置；旧 `management-key.txt` 不再用于登录。
管理界面使用登录会话，管理 API 的写操作还要求自定义请求头；
播放代理继续将客户端凭据交由 Emby 逐次验证。

可通过 `EMBY_BLURAY_HELPER_DATA` 指定数据目录。仅服务模式可使用
`--serve --data-dir /path`；若将程序作为 Emby probe 使用，两种模式必须指向同一数据目录。
便携版可保持程序旁的默认 data 目录。SPK 使用 DSM 的独立持久数据目录，
并单独导出不含凭据的探测配置供 Emby 读取，详见群晖安装说明。

## 同一个程序如何做 probe

无参数（或 `--serve`）启动 Web 控制台和代理。收到 ffprobe 参数时运行探测模式，
调用设置中的原版 ffprobe，并按开关补全 JSON 中缺失的语言。支持：

```text
EmbyBlurayHelper --helper-version
EmbyBlurayHelper --check-upstream
EmbyBlurayHelper -version
```

探测模式的 stdout 只输出 ffprobe 结果，日志单独写入 data/logs。
Emby 媒体探测在下一次实际执行时使用新配置，不会修改已缓存的媒体信息或清理媒体库。
已有文件必须触发实际重新探测，普通图片/简介刷新不一定执行 ffprobe。

Infuse 补写目前限定为带 Infuse 标识的 UHD（视频宽度 ≥3840）bluray 静态视频流。
保留原始视频、音频、字幕内容、时间戳和文件字节位置，无编码、无 remux、无临时视频文件。
## Infuse 多片段原盘兼容

这是独立的新功能，默认关闭，升级保留原开关状态。可在左侧「Infuse 播放」页面打开专属开关，立即生效并自动保存；概览显示独立状态，该页面提供触发条件、工作方式和工具配置说明。开启后显示内置读取器就绪，具体影片是否满足条件以运行日志为准。此功能和语言补写可分别开启或关闭；需要显示语言时另行开启语言补写。

多片段 UHD 原盘的静态读取若由 Emby 返回 404/500，工具会在能唯一匹配播放列表时启用兼容读取。使用内置读取器，验证 MPLS 顺序、连接方式以及 CLPI 的时间域、完整起止时间、包数量和文件长度后，直接按需读取完整片段。仅支持未加密、单角度、单时间域，后续片段须无缝连接；不猜测裁剪范围。无需启动 ffmpeg，也不依赖其蓝光协议。此方式用于修复 DSM Emby 4.10.2.0 自带工具缺少 libbluray、出现 `playlist: Option not found` 的兼容问题。

内置读取器不能确认边界时保留原响应，含裁剪、多角度或复杂时间域的标题暂不兼容。已移除旧版 FFmpeg / libbluray 读取路径：除 DSM 缺少协议外，原生字节定位还可能退回访问点，不适合直接保证 HTTP Range 的精确位置。内置读取器按文件位置精确定位，不编码、不重新封装、不生成临时视频文件，支持跨片段 Range；每个媒体请求仍由 Emby 校验客户端权限，不使用管理 API key 代替客户端。

本工具必须能读取完整原盘。大小、时长无法唯一匹配、权限不足、无法确认片段完整性，或原始响应为 401/403 时，保留原响应；失败日志包含具体错误。其他播放器、HLS 与单片段原盘继续使用已有处理。PMT 空间不足时可使用下一次相同 PMT 的重复包保存语言信息，保留原有媒体包、连续计数和文件字节位置。

## 构建

```sh
dotnet run --project tests/EmbyBlurayHelper.Proxy.Tests -c Release
dotnet publish src/EmbyBlurayHelper.App -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/app/linux-x64
dotnet run --project tests/EmbyBlurayHelper.App.Tests -c Release -- artifacts/app/linux-x64/EmbyBlurayHelper
sh scripts/package-portable.sh linux-x64
```

Windows 将 RID 换为 win-x64；ARM64 Linux 换为 linux-arm64。
GitHub Actions 为每个平台输出独立统一版下载包，不包含本地配置、密钥或日志。

SPK 可通过 `scripts/build-synology.ps1` 本地构建，GitHub Actions 也会输出 DSM 7.2 x86_64 安装包。
打包只需 .NET SDK 和 Python 标准库；具体步骤与真机验证边界见 [群晖安装说明](SYNOLOGY.md)。
