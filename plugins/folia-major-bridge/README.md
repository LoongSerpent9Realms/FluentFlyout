# Folia Major 歌词动效桥接插件

这个插件使用仓库内的真实上游项目 `third_party/folia-major` 的 Stage API，不复制或重实现 Folia Major 的歌词动效。

1. 在 Folia Major 设置中开启 Stage Mode，并复制 Bearer Token。
2. 编译本目录项目：`dotnet build plugins\folia-major-bridge\FoliaMajorBridge.csproj -c Release`。
3. 将 `plugin.json` 和 `folia-major-bridge.dll` 复制到 PulseFlyout 插件目录：`%LOCALAPPDATA%\PulseFlyout\Plugins\folia-major-bridge`。
4. 重启 PulseFlyout，在设置的“插件”页面打开 Folia Major，填写 Stage 地址和 Token，并勾选“启用歌词推送”。

默认地址是 `http://127.0.0.1:32107`。Token 只保存在插件自己的 `data\settings.json`，不会写入仓库。
