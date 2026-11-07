# Emby 播放状态上报兼容修复

日期：2026-11-07。分支：`feature/infuse-dolby-vision`。
修复前源码检查点：`106417d`。

## 现象与根因

Apple TV / Infuse-Direct 8.5.9 在 BDMV 和 ISO 的 P8.1 实机测试中播放、
DV 输出及拖动正常，但本机 Emby 4.10.2.0 的 `POST /Sessions/Playing`
在 14:44–14:48（UTC+08:00）返回 12 次 HTTP 400。
异常为 `ArgumentNullException (Parameter 'key')`。

检查本机 DLL 的 `SessionInfo.GetOrAddPlaySessionInfo` 确认该字典键来自
`PlaybackProgressInfo.PlaySessionId`。代理原先把 Kestrel 请求体包装为
`StreamContent`，却丢弃入站 `Content-Length`；非可定位请求流无法被
`HttpClient` 推断长度，最终改用分块传输。Emby 4.10 此接口的绑定器会
漏读分块 JSON，导致会话 ID 等字段为空。早于 DV 功能的样本测试也有
此异常，因此不是本轮 DV 转换新增的错误，但确实是现有代理兼容问题。

同一份明确包含会话 ID 的 JSON，修复前对照结果如下：

| 方式 | 结果 |
| --- | --- |
| 直连 8096，明确 Content-Length | 204 |
| 经过原 8097 代理，入站明确长度 | 400，空键异常 |
| 直连 8096，分块传输 | 400，相同异常 |
| 原代理上报，额外将会话 ID 放入查询参数作诊断 | 204 |

`/Sessions/Playing/Progress` 同样受影响。旧停止上报返回 204 并不能证明
请求体已读取或播放会话已移除。

## 修复范围

- `EmbyProxy.CreateRequest` 保留已知请求体长度，继续流式转发，不缓存
  上传或媒体流。适用于统一 App 和独立 Proxy 共用的代码。
- 对 `POST /Sessions/Playing`、`/Progress`、`/Stopped`，包括 `/emby`
  前缀、大小写和尾随斜杠变体，若入站没有长度则有限缓冲，再带准确
  长度转发。也覆盖 HTTP/2 无 Content-Length / Transfer-Encoding 的请求。
- 仅上述无长度上报限制为 1 MiB，超过返回 413，不转发部分请求；取消
  会停止读取。已知长度请求及其他上传保持流式，不受此缓冲限制。
- 保留原始 JSON 字节、调用者认证、查询参数和内容类型，不生成会话 ID、
  不重写进度，也不把 Emby 的拒绝响应伪装为成功。未更改媒体转换算法。

## 验证与本机更新

- `tests/EmbyBlurayHelper.DolbyVision.Tests/PlaybackReportChecks.cs`：
  新增 83 项请求转发检查，包含真实不可定位输入流、三种入站 framing、
  三个生命周期接口、原始 JSON/认证/查询参数保留、缓冲边界、取消及错误
  透传；与现有 DV 核心检查合计 113 项通过。CI 已包含该测试。
- 原有 transport / playlist 169 项、发布程序的 App 集成 138 项通过；
  独立 Proxy Release 构建通过，0 警告、0 错误。
- `node tests/check-local-playback-reports.cjs` 对运行中的 Emby 8096 和
  Helper 8097 验证 60 项。直连参考，以及代理的有/无 `/emby` 前缀、
  明确长度/分块传输四种组合，开始、进度、停止均返回 204。每次回读
  `/Sessions` 核对实际位置、暂停和音量，停止后确认诊断播放会话移除，
  不仅检查 HTTP 状态码。
- 测试使用独立设备会话，不指定 `ItemId`，不改变电影的观看计数、已看
  状态或续播位置。认证信息只在内存中使用，不写入报告。
- 16:18 已更新本机 `artifacts/app/win-x64`，管理 8099、代理 8097。
  仅重启 Helper，Emby 和插件 DLL 保持运行；设置和 libdovi 保留。
  更新前程序与配置由脚本备份至
  `artifacts/checks/local-update-20261107-161815-fca4a3`。

这证明服务端能收到并处理播放状态；Apple TV 实际电影退出后续播的设备
验证仍需另外记录。直连 Emby 自身的分块绑定缺陷并未改写，修复仅在
Helper 转发时兼容。DSM 实例本轮未部署，不宣称 NAS 已获得此修复。
