# Kkindle 使用手册

中文手册的 Markdown 源文件位于 `zh-CN/`，配图放在 `zh-CN/images/`。网站使用不依赖在线服务的静态 HTML，生成文件保存在 `website/manual/`。

修改正文或截图后，在仓库根目录运行：

```powershell
python scripts\generate_website_manual.py
```

生成器只使用 Python 标准库。请把源 Markdown、截图和生成后的网页一起提交，这样 GitHub 页面和 VPS 静态站点都能直接提供完整手册。
