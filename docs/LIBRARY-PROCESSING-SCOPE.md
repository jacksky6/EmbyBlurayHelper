# 全局媒体库处理范围

在左侧「全局设置 → 媒体库处理范围」取消勾选不想处理的库，点击「保存处理范围」。
首次使用全部选中，新增媒体库也默认选中；电影、电视剧等类型仅作参考，不会自动排除某种类型。
「媒体库处理」里的勾选仅控制本次任务，不能扩大这里保存的全局范围。

保存后，对被排除的库：

- 不在「媒体库处理」显示，不参与检查、数量统计或批量补全。
- 新影片不执行 Helper 的语言与时长、音视频规格、原生章节或截图处理。
- 探测调用直接交给 Emby 原版 ffprobe，保留其原始输出。
- 已保存的媒体信息、章节、图片和核验记录保留；重新选中可以恢复处理。

实际补全仍只面向电影中的 BDMV / Blu-ray ISO；不扩展到普通 MKV、电视剧等格式。
Emby 自身的媒体库扫描和原有功能继续运行。Infuse 播放增强独立于此范围。
章节截图仍遵循 Emby 对应媒体库的开关和任务时机，允许范围是附加限制。

## 状态与保存

范围按服务器 ID 和媒体库 ID 保存在 `data/settings.json` 的 `excludedLibrariesByServer`，仅记录明确排除的 ID。
改库名不影响选择；切换服务器会使用该服务器自己的配置。删除后重建的库有新的 ID，按新增库处理。
升级程序或 SPK 会保留配置。`Scope` 缓存路径仅用于启动探测器；DSM 的公开探测配置不包含 API key。

进入全局设置或媒体库页面时读取一次，可点击「重新读取」；没有范围轮询。
媒体库目录调整后，请重新读取并保存范围，更新探测器使用的路径缓存。
有 Helper 检查或批量任务运行时，请先等待完成或停止，再保存范围。
范围改变使旧计划失效，需要重新检查；不因此重复探测，未变化影片的既有核验结果仍可沿用。
已提交的当前 Emby 工作可能完成，后续自动处理受新范围约束。

## 插件要求

需要配套 Emby 插件 **0.1.9** 或更新版本才能同步自动处理范围。
升级 Helper / SPK 后，到 Emby 插件的更新页检查并安装配套 DLL，再重启 Emby。
老 DLL 会明确显示升级提示并禁用范围保存，避免出现网页隐藏了库、后台仍处理的情况。
页面提供「打开 Emby 插件更新」入口；安装更新后重启 Emby，再点击「重新读取」。
按钮区分「已保存」「正在读取…」「正在保存…」「需要更新插件」，禁用按钮不会显示等待光标。
保存失败时保留选择并显示具体错误，可修正原因后重试。
首次配置范围应在 Emby 可连接且返回完整媒体库列表时进行。

## 验证

```powershell
dotnet run --project tests/EmbyBlurayHelper.Scope.Tests -c Release
dotnet run --project tests/EmbyBlurayHelper.Library.Tests -c Release
dotnet run --project tests/EmbyBlurayHelper.Features.Tests -c Release
dotnet run --project tests/EmbyBlurayHelper.Chapters.Tests -c Release
node tests/web/library-scope.test.cjs
node tests/web/library-tasks.test.cjs
```

将发布后的 Helper 路径作为 Scope.Tests 的参数，还会实际运行探测器调用，验证排除 ISO 的参数和输出原样透传。
