const REPOSITORY = "kingstacker/Kkindle";
const RELEASE_PAGE = `https://github.com/${REPOSITORY}/releases/latest`;
const RELEASE_API = `https://api.github.com/repos/${REPOSITORY}/releases/latest`;
const SITE_RELEASE_JSON = "_release.json";
const SHOWCASE_INTERVAL = 6000;

const PLATFORM_NAMES = { windows: "Windows", macos: "macOS", linux: "Linux", android: "Android", ios: "iOS" };

const translations = {
  zh: {
    pageTitle: "Kkindle — 个人电子书与 Kindle 工作台",
    pageDescription: "Kkindle 是一款跨平台的个人电子书与 Kindle 设备管理器。",
    ogDescription: "把书库、阅读、批注和 Kindle 设备连接起来。",
    locale: "zh_CN", localeAlternate: "en_US",
    skipToContent: "跳到主要内容", navFeatures: "功能", navPreview: "界面", navDownload: "下载", navManual: "使用手册",
    heroTitle: "让每一本书，都有自己的位置。", heroLead: "整理书库，沉浸阅读，连接 Kindle。把喜欢的书，留在触手可及的地方。",
    heroDownloadTitle: "选择平台下载", platformNote: "桌面与移动端", recommended: "你的设备",
    showcaseLabel: "Kkindle 界面预览", showcaseTitle: "每一个屏幕，都恰到好处。", showcasePrevious: "上一张截图", showcaseNext: "下一张截图", showcaseSelect: "选择平台截图", showcaseMac: "显示 macOS 截图", showcaseWindows: "显示 Windows 截图", windowsAlt: "Kkindle Windows 书库界面", showcaseIos: "显示 iOS 截图", showcaseAndroid: "显示 Android 截图", macosPreview: "桌面版书库", mobilePreview: "移动端书库", macosAlt: "Kkindle macOS 书库界面", iosAlt: "Kkindle iOS 书库界面", androidAlt: "Kkindle Android 书库界面",
    downloadTitle: "在你喜欢的设备上阅读。", downloadLead: "选择你的平台，让阅读从这里开始。",
    stableLabel: "最新稳定版本", checking: "正在获取…", releaseUnavailable: "暂无版本信息",
    inDevelopment: "开发内测中", downloadUnavailable: "暂时无法获取下载信息", downloadNow: "下载",
    downloadFor: "下载 {platform} 版", downloadLatest: "下载最新版本",
    formatInstaller: "安装包", formatPortable: "便携版", formatArchive: "压缩包",
    heroAllDownloads: "全部版本与架构", heroBaidu: "百度网盘备用下载（kxas）",
    featuresTitle: "阅读的事，简单一点。", featuresLead: "从整理到阅读，再到带上 Kindle，把时间留给真正重要的内容。",
    libraryTitle: "你的私人书库", libraryBody: "导入 EPUB、PDF、MOBI 和 AZW3，自动识别元数据与封面，按作者、标签、格式和阅读状态找到下一本书。",
    readerTitle: "专注于阅读", readerBody: "支持横排、竖排、滚动、分页和双栏阅读，书签、搜索、脚注、批注与阅读进度都围绕正文展开。",
    kindleTitle: "和 Kindle 在一起", kindleBody: "连接 Kindle 后查看书籍、容量、字体和词典，批量发送、导出书籍并安全退出设备。",
    aiTitle: "读得更深，也记得更久。", aiBody: "用 AI 解释选文、总结章节或讨论全书；用批注、词典、阅读数据和导出记录，把阅读留下来。",
    readingCaption: "翻开一本书，慢下来。", updatesTitle: "一直在变得更好。", updatesLead: "每一次更新，都为了更好的阅读体验。",
    updatesSummary: "查看最近更新", changelogFull: "查看完整更新日志", changelogLatest: "最新", changelogImproved: "优化",
    changelogFixed: "修复", changelogAdded: "新增", changelogChanged: "变更", changelogSecurity: "安全",
    changelogDeprecated: "弃用", changelogRemoved: "移除", changelogDocumentation: "文档", changelogMaintenance: "维护", changelogPerformance: "性能",
    footerTagline: "让书回到它该在的地方。", qqGroupLabel: "QQ 用户群", qqGroupButtonLabel: "复制 QQ 用户群号 1109898894",
    qqGroupCopied: "已复制群号 1109898894", qqGroupCopyFailed: "复制失败，请手动记录群号 1109898894。",
    libraryAlt: "Kkindle 桌面版书库界面", libraryGridAlt: "Kkindle 书库网格视图", readerAlt: "Kreader 阅读器界面", kindleAlt: "Kindle 设备书库管理界面", aiAlt: "Kkindle AI 阅读助手"
  },
  en: {
    pageTitle: "Kkindle — Personal ebook & Kindle workspace",
    pageDescription: "Kkindle is a cross-platform ebook library and Kindle device manager.",
    ogDescription: "Connect your library, reading, annotations, and Kindle device in one local-first workflow.",
    locale: "en_US", localeAlternate: "zh_CN",
    skipToContent: "Skip to content", navFeatures: "Features", navPreview: "Interface", navDownload: "Download", navManual: "Chinese manual",
    heroTitle: "A place for every book.", heroLead: "Organize your library, settle into a book, and connect your Kindle. Keep the books you love close at hand.",
    heroDownloadTitle: "Choose your platform", platformNote: "Desktop & mobile", recommended: "Your device",
    showcaseLabel: "Kkindle interface previews", showcaseTitle: "Right at home on every screen.", showcasePrevious: "Previous screenshot", showcaseNext: "Next screenshot", showcaseSelect: "Choose a platform screenshot", showcaseMac: "Show macOS screenshot", showcaseWindows: "Show Windows screenshot", windowsAlt: "Kkindle Windows library", showcaseIos: "Show iOS screenshot", showcaseAndroid: "Show Android screenshot", macosPreview: "Desktop library", mobilePreview: "Mobile library", macosAlt: "Kkindle macOS library", iosAlt: "Kkindle iOS library", androidAlt: "Kkindle Android library",
    downloadTitle: "Read on your favorite device.", downloadLead: "Choose your platform and start reading.",
    stableLabel: "Latest stable release", checking: "Loading…", releaseUnavailable: "Release unavailable",
    inDevelopment: "In development · testing", downloadUnavailable: "Download information unavailable", downloadNow: "Download",
    downloadFor: "Download for {platform}", downloadLatest: "Download latest version",
    formatInstaller: "Installer", formatPortable: "Portable", formatArchive: "Archive",
    heroAllDownloads: "All releases & architectures", heroBaidu: "Baidu Netdisk backup (kxas)",
    featuresTitle: "A simpler way to read.", featuresLead: "From organizing and reading to your Kindle, spend more time with what matters.",
    libraryTitle: "Your personal library", libraryBody: "Import EPUB, PDF, MOBI, and AZW3 files, with metadata and covers. Find your next book by author, tag, format, or reading status.",
    readerTitle: "Room to focus", readerBody: "Read horizontally or vertically, with scrolling, paginated, or two-page layouts. Keep bookmarks, search, footnotes, annotations, and progress close to the text.",
    kindleTitle: "Together with Kindle", kindleBody: "Explore books, storage, fonts, and dictionaries on your Kindle. Send or export books in batches and safely eject the device.",
    aiTitle: "Read deeper. Keep more of it.", aiBody: "Use AI to explain a passage, summarize a chapter, or discuss a whole book. Keep the reading with annotations, dictionaries, data, and exports.",
    readingCaption: "Open a book. Take your time.", updatesTitle: "Always getting better.", updatesLead: "Every update makes room for a better reading experience.",
    updatesSummary: "See the latest updates", changelogFull: "View the full changelog", changelogLatest: "Latest", changelogImproved: "Improved",
    changelogFixed: "Fixed", changelogAdded: "Added", changelogChanged: "Changed", changelogSecurity: "Security",
    changelogDeprecated: "Deprecated", changelogRemoved: "Removed", changelogDocumentation: "Docs", changelogMaintenance: "Maint.", changelogPerformance: "Perf.",
    footerTagline: "Put books back where they belong.", qqGroupLabel: "QQ Group", qqGroupButtonLabel: "Copy QQ user group number 1109898894",
    qqGroupCopied: "Copied QQ group number 1109898894", qqGroupCopyFailed: "Copy failed. Please note group number 1109898894 manually.",
    libraryAlt: "Kkindle desktop library", libraryGridAlt: "Kkindle library grid view", readerAlt: "Kreader reading view", kindleAlt: "Kindle library management view", aiAlt: "Kkindle AI reading assistant"
  }
};

// Prefer common installers, but accept alternate packages when those are absent.
const assetMatchers = {
  windows: [/-win-x64-setup\.exe$/i, /-win.*\.(exe|msi|zip)$/i],
  macos: [/-osx-arm64\.tar\.gz$/i, /(?:osx|macos|mac)[-_].*\.(?:dmg|pkg|zip|tar\.gz)$/i],
  linux: [/_amd64\.deb$/i, /-linux-x64\.tar\.gz$/i, /(?:linux.*\.(?:deb|rpm|appimage|tar\.gz)|_arm64\.deb)$/i],
  android: [/\.apk$/i],
  ios: [/\.ipa$/i]
};
let currentLanguage = "zh";
let latestRelease = null;
let releaseFailed = false;
let recommendedPlatform = null;

function getStoredLanguage() {
  try {
    const requested = new URLSearchParams(window.location.search).get("lang");
    if (requested === "zh" || requested === "en") return requested;
    const stored = window.localStorage.getItem("kkindle-site-language");
    if (stored === "zh" || stored === "en") return stored;
  } catch { /* Language preferences are optional. */ }
  return navigator.language && navigator.language.toLowerCase().startsWith("en") ? "en" : "zh";
}

function detectPlatform() {
  const ua = navigator.userAgent || "";
  if (/Android/i.test(ua)) return "android";
  if (/iPhone|iPad|iPod/i.test(ua)) return "ios";
  if (/Windows/i.test(ua)) return "windows";
  if (/Macintosh|Mac OS X/i.test(ua)) return navigator.maxTouchPoints > 1 ? "ios" : "macos";
  if (/Linux|X11/i.test(ua)) return "linux";
  return null;
}

function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes <= 0) return "";
  const megabytes = bytes / 1048576;
  if (megabytes >= 1024) return `${(megabytes / 1024).toFixed(1)} GB`;
  return `${Math.round(megabytes)} MB`;
}

function formatLabel(name, copy) {
  const asset = (name || "").toLowerCase();
  if (/setup\.exe$|\.msi$|-setup\./.test(asset)) return copy.formatInstaller;
  if (/\.zip$/.test(asset)) return copy.formatPortable;
  if (/\.tar\.gz$|\.tgz$/.test(asset)) return copy.formatArchive;
  if (/\.deb$/.test(asset)) return "DEB";
  if (/\.rpm$/.test(asset)) return "RPM";
  if (/\.appimage$/.test(asset)) return "AppImage";
  if (/\.apk$/.test(asset)) return "APK";
  if (/\.ipa$/.test(asset)) return "IPA";
  if (/\.dmg$/.test(asset)) return "DMG";
  return "";
}

function assetMeta(asset, copy) {
  const parts = [formatLabel(asset.name, copy), formatBytes(asset.size)].filter(Boolean);
  return parts.join(" · ");
}

function findAssetFor(key, assets) {
  const matchers = assetMatchers[key] || [];
  const match = matchers
    .map((matcher) => assets.find((item) => matcher.test(item.name || "") && item.browser_download_url))
    .find(Boolean);
  return match || null;
}

function applyLanguage(language, updateUrl = false) {
  currentLanguage = language === "en" ? "en" : "zh";
  const copy = translations[currentLanguage];
  document.documentElement.lang = currentLanguage === "en" ? "en" : "zh-CN";
  document.querySelectorAll("[data-i18n]").forEach((element) => {
    if (copy[element.dataset.i18n]) element.textContent = copy[element.dataset.i18n];
  });
  document.querySelectorAll("[data-i18n-alt]").forEach((element) => {
    if (copy[element.dataset.i18nAlt]) element.alt = copy[element.dataset.i18nAlt];
  });
  document.querySelectorAll("[data-i18n-label]").forEach((element) => {
    if (copy[element.dataset.i18nLabel]) element.setAttribute("aria-label", copy[element.dataset.i18nLabel]);
  });
  document.querySelectorAll("[data-changelog-item]").forEach((item) => {
    const variants = Array.from(item.querySelectorAll("[data-changelog-lang]"));
    const selected = variants.find((variant) => variant.dataset.changelogLang === currentLanguage) || variants[0];
    variants.forEach((variant) => { variant.hidden = variant !== selected; });
  });
  document.querySelectorAll("[data-changelog-date]").forEach((element) => {
    const date = new Date(`${element.dataset.changelogDate}T12:00:00`);
    if (!Number.isNaN(date.getTime())) element.textContent = date.toLocaleDateString(currentLanguage === "en" ? "en-US" : "zh-CN", { year: "numeric", month: "short", day: "numeric" });
  });
  document.querySelectorAll("[data-changelog-link]").forEach((link) => {
    link.href = `https://github.com/${REPOSITORY}/blob/master/${currentLanguage === "en" ? "CHANGELOG.en.md" : "CHANGELOG.md"}`;
  });
  const toggle = document.querySelector("#language-toggle");
  toggle.textContent = currentLanguage === "en" ? "中文" : "EN";
  toggle.setAttribute("aria-label", currentLanguage === "en" ? "Switch to Chinese" : "切换为英文");
  document.title = copy.pageTitle;
  [["name", "description", copy.pageDescription], ["property", "og:title", copy.pageTitle], ["property", "og:description", copy.ogDescription], ["property", "og:locale", copy.locale], ["property", "og:locale:alternate", copy.localeAlternate], ["name", "twitter:title", copy.pageTitle], ["name", "twitter:description", copy.ogDescription]].forEach(([attribute, name, value]) => {
    document.querySelector(`meta[${attribute}="${name}"]`)?.setAttribute("content", value);
  });
  try { window.localStorage.setItem("kkindle-site-language", currentLanguage); } catch { /* Optional. */ }
  if (updateUrl && window.history?.replaceState) {
    const url = new URL(window.location.href);
    if (currentLanguage === "en") url.searchParams.set("lang", "en");
    else url.searchParams.delete("lang");
    window.history.replaceState(null, "", `${url.pathname}${url.search}${url.hash}`);
  }
  if (latestRelease) renderRelease(latestRelease);
  else if (releaseFailed) setFallbackLinks();
}

function markRecommendedPlatform() {
  recommendedPlatform = detectPlatform();
  document.querySelectorAll("[data-download-key]").forEach((link) => {
    link.classList.toggle("is-recommended", link.dataset.downloadKey === recommendedPlatform);
  });
  updateRecommendedBadges();
}

// Highlight the row that matches the visitor's system, even while that build is
// still in development, so the platform list always answers "which one is mine?".
function updateRecommendedBadges() {
  document.querySelectorAll("[data-download-key]").forEach((link) => {
    const badge = link.querySelector("[data-recommended-badge]");
    if (!badge) return;
    badge.hidden = link.dataset.downloadKey !== recommendedPlatform;
  });
}

function setFallbackLinks() {
  releaseFailed = true;
  document.querySelectorAll("[data-release-version]").forEach((element) => {
    element.removeAttribute("data-i18n");
    element.textContent = translations[currentLanguage].releaseUnavailable;
  });
  const primary = document.querySelector("[data-primary-download]");
  const primaryLabel = document.querySelector("[data-primary-label]");
  if (primary && primaryLabel) {
    primary.href = RELEASE_PAGE;
    primaryLabel.removeAttribute("data-i18n");
    primaryLabel.textContent = translations[currentLanguage].downloadLatest;
  }
  document.querySelectorAll("[data-download-key]").forEach((link) => {
    link.removeAttribute("href");
    link.setAttribute("aria-disabled", "true");
    link.removeAttribute("title");
    link.querySelector(".platform-pick-meta").textContent = translations[currentLanguage][
      ["android", "ios"].includes(link.dataset.downloadKey) ? "inDevelopment" : "downloadUnavailable"
    ];
  });
  updateRecommendedBadges();
}

function renderRelease(release) {
  releaseFailed = false;
  latestRelease = release;
  const copy = translations[currentLanguage];
  document.querySelectorAll("[data-release-version]").forEach((element) => {
    element.removeAttribute("data-i18n");
    element.textContent = release.tag_name || release.name || copy.releaseUnavailable;
  });
  document.querySelectorAll("[data-release-link]").forEach((link) => { link.href = release.html_url || RELEASE_PAGE; });

  const assets = Array.isArray(release.assets) ? release.assets : [];
  const available = {};

  Object.keys(assetMatchers).forEach((key) => {
    const asset = findAssetFor(key, assets);
    available[key] = asset;
    document.querySelectorAll(`[data-download-key="${key}"]`).forEach((link) => {
      const meta = link.querySelector(".platform-pick-meta");
      if (asset) {
        link.href = asset.browser_download_url;
        link.removeAttribute("aria-disabled");
        link.title = asset.name;
        meta.removeAttribute("data-i18n");
        meta.textContent = assetMeta(asset, copy);
      } else {
        link.removeAttribute("href");
        link.setAttribute("aria-disabled", "true");
        link.removeAttribute("title");
        meta.setAttribute("data-i18n", "inDevelopment");
        meta.textContent = copy.inDevelopment;
      }
    });
  });

  updateRecommendedBadges();

  const primary = document.querySelector("[data-primary-download]");
  const primaryLabel = document.querySelector("[data-primary-label]");
  if (primary && primaryLabel) {
    primaryLabel.removeAttribute("data-i18n");
    const platform = recommendedPlatform && available[recommendedPlatform] ? recommendedPlatform : null;
    if (platform) {
      primary.href = available[platform].browser_download_url;
      primaryLabel.textContent = copy.downloadFor.replace("{platform}", PLATFORM_NAMES[platform]);
    } else {
      primary.href = release.html_url || RELEASE_PAGE;
      primaryLabel.textContent = copy.downloadLatest;
    }
  }
}

async function fetchRelease(url, options = {}) {
  const response = await fetch(url, { cache: "no-cache", ...options });
  if (!response.ok) throw new Error(`${url} returned ${response.status}`);
  const release = await response.json();
  if (!release || typeof release !== "object" || !Array.isArray(release.assets)) throw new Error("Invalid release payload");
  return release;
}

async function loadLatestRelease() {
  let rendered = false;
  try { renderRelease(await fetchRelease(SITE_RELEASE_JSON)); rendered = true; } catch { /* Try GitHub. */ }
  try {
    renderRelease(await fetchRelease(RELEASE_API, { cache: "default", headers: { Accept: "application/vnd.github+json" } }));
    rendered = true;
  } catch { /* Keep the snapshot when the API is unavailable. */ }
  if (!rendered) setFallbackLinks();
}

function setupQQGroupCopy() {
  const button = document.querySelector("#qq-group-copy");
  const status = document.querySelector("#qq-group-copy-status");
  if (!button || !status) return;
  let timer;
  button.addEventListener("click", async () => {
    try { await navigator.clipboard.writeText("1109898894"); status.textContent = translations[currentLanguage].qqGroupCopied; }
    catch { status.textContent = translations[currentLanguage].qqGroupCopyFailed; }
    status.classList.add("is-visible");
    window.clearTimeout(timer);
    timer = window.setTimeout(() => status.classList.remove("is-visible"), 2400);
  });
}

function setupHeader() {
  const header = document.querySelector("#site-header");
  if (!header) return;
  const update = () => header.classList.toggle("is-scrolled", window.scrollY > 8);
  update();
  window.addEventListener("scroll", update, { passive: true });
}

function setupShowcase() {
  const showcase = document.querySelector("#preview");
  if (!showcase) return;
  const slides = Array.from(showcase.querySelectorAll("[data-showcase-slide]"));
  const selectors = Array.from(showcase.querySelectorAll("[data-showcase-index]"));
  const previous = showcase.querySelector("[data-showcase-previous]");
  const next = showcase.querySelector("[data-showcase-next]");
  const status = showcase.querySelector("[data-showcase-status]");
  const reducedMotion = window.matchMedia("(prefers-reduced-motion: reduce)");
  let selected = 0;
  let paused = reducedMotion.matches;
  let hovering = false;
  let focused = false;
  let visible = true;
  let timer;

  function schedule() {
    window.clearTimeout(timer);
    if (paused || hovering || focused || !visible || document.hidden) return;
    timer = window.setTimeout(() => show(selected + 1), SHOWCASE_INTERVAL);
  }

  function show(index, announce = false) {
    selected = (index + slides.length) % slides.length;
    slides.forEach((slide, i) => { slide.hidden = i !== selected; });
    selectors.forEach((button, i) => button.setAttribute("aria-pressed", String(i === selected)));
    if (announce) status.textContent = slides[selected].querySelector("figcaption").textContent.trim();
    schedule();
  }

  previous.addEventListener("click", () => show(selected - 1, true));
  next.addEventListener("click", () => show(selected + 1, true));
  selectors.forEach((button, index) => button.addEventListener("click", () => show(index, true)));
  showcase.addEventListener("mouseenter", () => { hovering = true; schedule(); });
  showcase.addEventListener("mouseleave", () => { hovering = false; schedule(); });
  showcase.addEventListener("focusin", () => { focused = true; schedule(); });
  showcase.addEventListener("focusout", (event) => {
    if (!showcase.contains(event.relatedTarget)) { focused = false; schedule(); }
  });
  showcase.addEventListener("keydown", (event) => {
    if (event.key === "ArrowLeft" || event.key === "ArrowRight") {
      event.preventDefault();
      show(selected + (event.key === "ArrowLeft" ? -1 : 1), true);
    }
  });
  document.addEventListener("visibilitychange", schedule);
  reducedMotion.addEventListener("change", (event) => {
    paused = event.matches;
    schedule();
  });
  if ("IntersectionObserver" in window) {
    new IntersectionObserver((entries) => {
      visible = entries.some((entry) => entry.isIntersecting);
      schedule();
    }, { threshold: 0.15 }).observe(showcase);
  }
  showcase.querySelectorAll("[data-showcase-controls]").forEach((element) => { element.hidden = false; });
  show(0);
}

document.addEventListener("DOMContentLoaded", () => {
  applyLanguage(getStoredLanguage());
  markRecommendedPlatform();
  setupQQGroupCopy();
  setupHeader();
  setupShowcase();
  document.querySelector("#language-toggle").addEventListener("click", () => applyLanguage(currentLanguage === "en" ? "zh" : "en", true));
  loadLatestRelease();
});
