# Kkindle 官网

纯静态站点，无构建依赖即可预览；发布前用脚本重新生成手册、更新日志和版本快照。

## 本地预览

```bash
python3 -m http.server -d website 8765
# http://127.0.0.1:8765/
```

## 发布构建

```bash
./scripts/build_website.sh [输出目录，默认 artifacts/website]
```

输出目录会原地覆盖（不会先清空）；发布前建议传入一个全新的目录。

脚本会：

1. 从 `docs/manual/zh-CN` 重新生成 `website/manual/`；
2. 从 GitHub 最新 Release 刷新 `website/_release.json`；
3. 把最新更新日志写入 `website/index.html`；
4. 输出可部署目录 `artifacts/website/`（不包含 `dev/` 调试页）。

把 `artifacts/website/` 部署到 `kkindle.stacker.beauty` 即可。`_release.json` 必须一并部署：页面优先读取这份同源快照，避免 GitHub API 的未认证限流（60 次/小时/IP），并在后台向 GitHub 复核版本。

## 图片资源

```bash
python3 -m pip install pillow
python3 scripts/optimize_website_assets.py
```

生成响应式 WebP（640w / 1024w）、`library-grid` 书库网格截图、1200×630 分享图与 Apple Touch Icon。

## 目录

- `index.html` — 首页（首屏包含三平台直下）
- `manual/` — 由脚本生成，请勿直接编辑 `manual/*.html`
- `assets/` — 截图与优化后的图片变体
- `dev/` — 本地检查用页面（`noindex`，构建产物中排除）
- `styles.css` / `app.js` — 样式与交互
