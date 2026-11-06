# 概览页品牌图标

概览页使用本地嵌入的产品图标，不在浏览器中请求第三方资源。新增资源通过 `Web/*` 嵌入应用，并由管理端口提供，因此 Windows、DSM 和 Docker 使用同一套页面。

于 2026-11-06 获取：

- `Web/emby-icon.png`：Emby 官网提供的 192 × 192 图标，来源 `https://emby.media/android-chrome-192x192.png`。
- `Web/infuse-icon.jpg`：Firecore 官网链接的 App Store 产品 Infuse（ID `1136220934`）所提供的 512 × 512 图标。产品页 `https://apps.apple.com/app/infuse/id1136220934`；通过 Apple Lookup API 获取 artworkUrl512 后下载。

Emby 与 Infuse 的名称、图标及商标归各自所有者所有。图标用于标识本辅助工具所配合使用的产品，不表示官方合作或背书。
