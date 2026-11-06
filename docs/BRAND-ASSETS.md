# 概览页品牌图标

概览页使用本地嵌入的产品图标，不在浏览器中请求第三方资源。新增资源通过 `Web/*` 嵌入应用，并由管理端口提供，因此 Windows、DSM 和 Docker 使用同一套页面。

于 2026-11-06 获取：

- `Web/emby-icon.png`：Emby 官网提供的 192 × 192 图标，来源 `https://emby.media/android-chrome-192x192.png`。
- `Web/infuse-icon.jpg`：Firecore 官网链接的 App Store 产品 Infuse（ID `1136220934`）所提供的 512 × 512 图标。产品页 `https://apps.apple.com/app/infuse/id1136220934`；通过 Apple Lookup API 获取 artworkUrl512 后下载。

Emby 与 Infuse 的名称、图标及商标归各自所有者所有。图标用于标识本辅助工具所配合使用的产品，不表示官方合作或背书。

## Emby 插件封面

`EmbyPlugin/Artwork/thumbnail.svg` 为本项目绘制的 16:9 封面源图，使用原创光盘图形与上述 Infuse 图标，突出 Blu-ray、UHD 原盘增强和 Infuse。导出的 `thumbnail.png` 为 1280 × 720，直接嵌入插件 DLL，经 `IHasThumbImage` 提供；运行时不加载外部网站或字体。

源图使用本机 Segoe UI、Microsoft YaHei 字体。修改后可使用本地 Chrome 和 Playwright Core 执行 `node scripts/render-plugin-thumbnail.cjs` 重新导出；正常编译使用仓库内的 PNG，无需这些绘图工具。Blu-ray 名称用于指示所支持的媒体格式，不表示官方认证。
