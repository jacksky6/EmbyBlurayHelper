# Emby 插件更新

Helper 与 Emby 插件是两个进程、两套版本。升级 Helper 的 EXE、便携包或 SPK 后，Helper 会从自身内嵌资源提供配套 DLL；插件由 Emby 管理员在 Emby 内点击更新。首次手动安装 **0.1.7 或更新版本** 后，才能使用本功能；旧版 0.1.4 / 0.1.6 不具备更新入口，仍需最后手动替换一次并重启 Emby。

## 使用

1. 升级 Helper 程序或 SPK。
2. 在 Emby 控制台进入「插件 → 我的插件 → EmbyBlurayHelper」的配置页面。
3. Helper 同机部署时保留 `http://127.0.0.1:8099`，点击「保存并检查更新」。如果改过管理端口，填写实际端口；容器部署填写 **Emby 容器能够访问** 的 Helper 地址。
4. 页面分别显示当前已加载、磁盘已安装、Helper 提供的版本。发现新版本后点击「安装更新」。由 Emby 运行账号校验文件、备份并替换自身 DLL，不使用 Helper 账号写入 Emby 目录。
5. 更新成功后显示「等待重启」。在没有播放和批量任务时手动重启 **Emby Server**，再打开插件页确认已加载新版。页面不会自动重启 Emby，也不会定时轮询。

Helper 登录账号与 Emby 账号不同。插件访问更新源不需要 Helper 密码或 Emby API key；仅 Emby 管理员可以调用插件的检查、修改来源和安装接口。

## 文件与校验

- 更新清单：`GET http://127.0.0.1:8099/plugin-update/manifest.json`
- DLL：`GET http://127.0.0.1:8099/plugin-update/EmbyBlurayHelper.Plugin.dll`
- 两个公开地址只提供配套插件，不提供配置、API key、日志或任意本地文件。普通播放代理端口不承载这些资源。
- DLL 直接嵌入 Helper EXE，单独替换 EXE 也会更新它提供的 DLL，无需同步另一个下载目录。
- 安装只允许比已加载版本新的版本。重新读取清单，校验预先确认的版本、长度、SHA256、程序集名称和程序集版本，校验通过后才替换。
- 临时文件和目标文件在同一目录，采用原子替换。备份为 `EmbyBlurayHelper.Plugin.dll.previous`，不会改动 XML 配置、原盘、元数据或其他插件。
- 网络失败、文件损坏、来源版本变化或权限不足时显示错误并记录 Emby 日志，原文件不会因下载过程被截断。
- Windows 和 Linux 使用相同流程，更新后仍运行内存中的旧版本，直到重启。目录必须允许 Emby 运行账号写入；Helper 账号不需要 Emby 插件目录写权限。

## 回退

停止 Emby Server，将同目录中的 `EmbyBlurayHelper.Plugin.dll.previous` 复制回 `EmbyBlurayHelper.Plugin.dll`，保持 Emby 账号可读，然后启动 Emby。回退文件只在成功更新时生成；不要删除插件配置 XML。

## 维护与测试

更新模块位于 `EmbyPlugin/Updates`，Emby 配置页面位于 `EmbyPlugin/Web`，Helper 分发模块为 `App/BundledPluginUpdate.cs`。播放、语言、规格与章节模块不依赖更新器。

```sh
dotnet run --project tests/EmbyBlurayHelper.PluginUpdate.Tests -c Release
node tests/web/plugin-update.test.cjs
```

测试覆盖加载中的 DLL 原子替换、备份、版本归一化、待重启状态、损坏文件、错误程序集、来源变化、取消、禁止降级和配置页面行为。应用集成测试另外校验自包含 EXE 内的 DLL、清单和下载响应。

本次实现还在隔离的 Windows Emby Server 4.10.2.0 上验证了管理员权限限制、真实 DLL 更新、配置保留、待重启通知、重启后加载新版本，以及桌面和手机尺寸的配置页面。更新核心也通过了 Linux 测试；SPK 的安装、升级和跨账户访问使用本地生命周期测试验证。以上不代表已在实际 DSM 机器上完成部署验证，正式更新群晖后仍应检查加载版本及 Emby 日志。
