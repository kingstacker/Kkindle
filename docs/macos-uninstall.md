# 卸载 macOS 版 Kkindle

macOS 没有内置的“卸载程序”。把 `Kkindle.app` 拖到废纸篓只会删除应用本身，
书库、设置、备份、缓存和 Keychain 中的加密密钥都会保留下来。

## 完整卸载

在仓库中运行：

```sh
scripts/uninstall-macos.sh
```

默认删除 `/Applications/Kkindle.app` 以及以下位置：

- `~/Library/Application Support/Kkindle`（书库、设置、备份、缓存、日志）
- `~/Library/Preferences/io.github.kingstacker.kkindle.plist`
- `~/Library/Saved Application State/io.github.kingstacker.kkindle.savedState`
- `~/Library/Caches/io.github.kingstacker.kkindle`
- `~/Library/Logs/Kkindle`
- 登录钥匙串中服务名为 `Kkindle.SecretProtectionKey` 的条目

如果需要同时删除其他路径的应用包：

```sh
scripts/uninstall-macos.sh "/Users/me/Applications/Kkindle.app"
```

## 保留数据

只想删除应用、保留书库和设置时：

```sh
scripts/uninstall-macos.sh --keep-data
```

脚本会先请求 Kkindle 退出，避免数据库或云同步状态在退出时被截断。删除是
不可恢复的；需要保留书库时请先使用应用内的备份功能。
