const REPOSITORY = "kingstacker/Kkindle";
const RELEASE_PAGE = `https://github.com/${REPOSITORY}/releases/latest`;
const RELEASE_API = `https://api.github.com/repos/${REPOSITORY}/releases/latest`;
// Same-origin snapshot refreshed by scripts/refresh_website_release.py.
const SITE_RELEASE_JSON = "_release.json";

const translations = {
  zh: {
    pageTitle: "Kkindle — 个人电子书与 Kindle 工作台",
    pageDescription: "Kkindle 是一款面向 Windows、Linux 和 macOS 的个人电子书与 Kindle 设备管理器。",
    ogTitle: "Kkindle — 个人电子书与 Kindle 工作台",
    ogDescription: "把书库、阅读、批注和 Kindle 设备连接起来。",
    locale: "zh_CN",
    localeAlternate: "en_US",
    skipToContent: "跳到主要内容",
    menuLabel: "打开菜单",
    navFeatures: "功能",
    navDownload: "下载",
    navManual: "使用手册",
    navChangelog: "更新日志",
    headerGitHub: "GitHub",
    heroKicker: "KINDLE, WITHOUT THE FRICTION.",
    heroTitle: "把书库、阅读与 Kindle 连接起来。",
    heroLead: "Kkindle 是一款安静、跨平台的电子书书库与 Kindle 工作台。整理你的书，专注地读，然后把它们带到真正的阅读设备上。",
    heroDownloadTitle: "选择平台下载",
    heroWindowsMeta: "Windows 11 · x64",
    heroLinuxMeta: ".deb · x64",
    heroMacMeta: "Apple Silicon",
    heroAllDownloads: "全部架构",
    heroChecksum: "SHA-256 校验值",
    heroBaidu: "百度网盘备用下载（kxas）",
    featuresKicker: "THE WHOLE READING DESK",
    featuresTitle: "从一本书开始，直到它真正被读完。",
    featuresLead: "书库、阅读器、设备和阅读记录在同一处工作。没有多余的云端流程，也不打断你的阅读节奏。",
    libraryKicker: "LOCAL LIBRARY",
    libraryTitle: "把混乱的文件，变成真正的书库。",
    libraryBody: "导入 EPUB、PDF、MOBI 和 AZW3，自动识别元数据与封面，按作者、标签、格式和阅读状态找到下一本书。",
    readerKicker: "KREADER",
    readerTitle: "让排版退后，让内容走到前面。",
    readerBody: "支持横排、竖排、滚动、分页和双栏阅读。书签、搜索、脚注、批注与阅读进度都围绕正文展开。",
    kindleKicker: "KINDLE WORKFLOW",
    kindleTitle: "从桌面书库，到手里的 Kindle。",
    kindleBody: "连接 Kindle 后查看书籍、容量、字体和词典，批量发送、导出和安全退出设备，把传书变成一个清晰的动作。",
    aiKicker: "READING TOOLS",
    aiTitle: "读得更深，也记得更久。",
    aiBody: "用 AI 解释选文、总结章节或讨论全书；用批注、词典、阅读数据和导出记录，把阅读留下来。",
    qqGroupLabel: "QQ 用户群",
    qqGroupCopied: "已复制群号 1109898894",
    qqGroupCopyFailed: "复制失败，请手动记录群号 1109898894。",
    qqGroupButtonLabel: "复制 QQ 用户群号 1109898894",
    stableLabel: "最新稳定版本",
    checking: "正在获取…",
    releaseUnavailable: "暂无版本信息",
    openRelease: "查看 Release",
    changelogKicker: "CHANGELOG",
    changelogTitle: "每一次更新，都让阅读更顺手。",
    changelogFull: "查看完整更新日志",
    changelogLatest: "最新",
    changelogImproved: "优化",
    changelogFixed: "修复",
    changelogAdded: "新增",
    changelogChanged: "变更",
    changelogSecurity: "安全",
    changelogDeprecated: "弃用",
    changelogRemoved: "移除",
    changelogDocumentation: "文档",
    changelogMaintenance: "维护",
    changelogPerformance: "性能",
    faqKicker: "BEFORE YOU START",
    faqTitle: "下载前，先知道这几件事。",
    faq1Question: "Kkindle 支持哪些文件格式？",
    faq1Answer: "可以导入和阅读 EPUB、PDF、MOBI 与 AZW3。格式转换依赖用户自行安装的 Calibre。",
    faq2Question: "我的书和 API Key 会上传吗？",
    faq2Answer: "书籍、封面、阅读记录默认保存在本机。AI 请求只发送相关片段，API Key 使用系统安全存储。",
    faq3Question: "macOS 下载后如何打开？",
    faq3Answer: "请按照仓库中的 macOS 安装说明操作。发布包是否经过公证，以对应版本的发布说明为准。",
    faq4Question: "在哪里反馈问题或查看更新？",
    faq4Answer: "可以在 GitHub 仓库提交 Issue，或者查看 Releases 和更新日志。",
    footerTagline: "让书回到它该在的地方。",
    footerGitHub: "GitHub",
    footerChangelog: "更新日志",
    footerLicense: "GPL-3.0 License",
    footerNote: "官网由 kkindle.stacker.beauty 提供，安装包由 GitHub Releases 托管。",
    libraryAlt: "Kkindle 电脑书库界面",
    libraryGridAlt: "Kkindle 书库网格视图",
    readerAlt: "Kreader 阅读器界面",
    kindleAlt: "Kindle 设备书库管理界面",
    aiAlt: "Kkindle AI 阅读助手"
  },
  en: {
    pageTitle: "Kkindle — Personal ebook & Kindle workspace",
    pageDescription: "Kkindle is a personal ebook library and Kindle device manager for Windows, Linux, and macOS.",
    ogTitle: "Kkindle — Personal ebook & Kindle workspace",
    ogDescription: "Connect your library, reading, annotations, and Kindle device in one local-first workflow.",
    locale: "en_US",
    localeAlternate: "zh_CN",
    skipToContent: "Skip to content",
    menuLabel: "Open menu",
    navFeatures: "Features",
    navDownload: "Download",
    navManual: "Chinese manual",
    navChangelog: "Changelog",
    headerGitHub: "GitHub",
    heroKicker: "KINDLE, WITHOUT THE FRICTION.",
    heroTitle: "Bring your library, reading, and Kindle together.",
    heroLead: "Kkindle is a quiet, cross-platform ebook library and Kindle workspace. Organize your books, read with focus, and move them to the device you actually read on.",
    heroDownloadTitle: "Choose your platform",
    heroWindowsMeta: "Windows 11 · x64",
    heroLinuxMeta: ".deb · x64",
    heroMacMeta: "Apple Silicon",
    heroAllDownloads: "All architectures",
    heroChecksum: "SHA-256 checksums",
    heroBaidu: "Baidu Netdisk backup (kxas)",
    featuresKicker: "THE WHOLE READING DESK",
    featuresTitle: "Start with a book. Stay until it is actually read.",
    featuresLead: "Your library, reader, device, and reading records work in one place—with no extra cloud workflow to interrupt the page.",
    libraryKicker: "LOCAL LIBRARY",
    libraryTitle: "Turn a folder of files into a real library.",
    libraryBody: "Import EPUB, PDF, MOBI, and AZW3 files. Parse metadata and covers, then find the next book by author, tag, format, or reading status.",
    readerKicker: "KREADER",
    readerTitle: "Let the typesetting recede. Let the words lead.",
    readerBody: "Read horizontally or vertically, in scroll, paginated, or two-page layouts. Bookmarks, search, footnotes, annotations, and progress stay close to the text.",
    kindleKicker: "KINDLE WORKFLOW",
    kindleTitle: "From desktop library to the Kindle in your hand.",
    kindleBody: "Inspect books, capacity, fonts, and dictionaries on a connected Kindle. Send, export, and safely eject in one clear workflow.",
    aiKicker: "READING TOOLS",
    aiTitle: "Read deeper. Keep more of it.",
    aiBody: "Use AI to explain a passage, summarize a chapter, or discuss a whole book. Keep the reading with annotations, dictionaries, data, and exports.",
    qqGroupLabel: "QQ Group",
    qqGroupCopied: "Copied QQ group number 1109898894",
    qqGroupCopyFailed: "Copy failed. Please note group number 1109898894 manually.",
    qqGroupButtonLabel: "Copy QQ user group number 1109898894",
    stableLabel: "Latest stable release",
    checking: "Loading…",
    releaseUnavailable: "Release unavailable",
    openRelease: "Open release",
    changelogKicker: "CHANGELOG",
    changelogTitle: "Every update makes reading feel easier.",
    changelogFull: "View the full changelog",
    changelogLatest: "Latest",
    changelogImproved: "Improved",
    changelogFixed: "Fixed",
    changelogAdded: "Added",
    changelogChanged: "Changed",
    changelogSecurity: "Security",
    changelogDeprecated: "Deprecated",
    changelogRemoved: "Removed",
    changelogDocumentation: "Docs",
    changelogMaintenance: "Maint.",
    changelogPerformance: "Perf.",
    faqKicker: "BEFORE YOU START",
    faqTitle: "A few things to know before downloading.",
    faq1Question: "Which file formats does Kkindle support?",
    faq1Answer: "You can import and read EPUB, PDF, MOBI, and AZW3. Format conversion requires Calibre to be installed separately.",
    faq2Question: "Are my books or API keys uploaded?",
    faq2Answer: "Books, covers, and reading records stay local by default. AI requests include relevant excerpts only, and API keys use secure system storage.",
    faq3Question: "How do I open the macOS download?",
    faq3Answer: "Follow the macOS installation notes in the repository. Check the release notes for the signing and notarization status of each package.",
    faq4Question: "Where can I report an issue or see updates?",
    faq4Answer: "Open an Issue in the GitHub repository, or visit Releases and the changelog.",
    footerTagline: "Put books back where they belong.",
    footerGitHub: "GitHub",
    footerChangelog: "Changelog",
    footerLicense: "GPL-3.0 License",
    footerNote: "The official site is provided by kkindle.stacker.beauty; installers are hosted on GitHub Releases.",
    libraryAlt: "Kkindle desktop library",
    libraryGridAlt: "Kkindle library grid view",
    readerAlt: "Kreader reading view",
    kindleAlt: "Kindle library management view",
    aiAlt: "Kkindle AI reading assistant"
  }
};

const assetMatchers = {
  "windows-installer": /-win-x64-setup\.exe$/i,
  "linux-deb-x64": /_amd64\.deb$/i,
  "mac-arm64": /-osx-arm64\.tar\.gz$/i,
  checksums: /^SHA256SUMS\.txt$/i
};

let currentLanguage = "zh";
let latestRelease = null;
let releaseFailed = false;

function getStoredLanguage() {
  try {
    const requested = new URLSearchParams(window.location.search).get("lang");
    if (requested === "zh" || requested === "en") return requested;
  } catch {
    // Query strings are optional.
  }

  try {
    const stored = window.localStorage.getItem("kkindle-site-language");
    if (stored === "zh" || stored === "en") return stored;
  } catch {
    // Local storage is optional.
  }

  return navigator.language && navigator.language.toLowerCase().startsWith("en") ? "en" : "zh";
}

function updateMetaContent(attribute, name, value) {
  if (!value) return;
  const element = document.querySelector(`meta[${attribute}="${name}"]`);
  if (element) element.setAttribute("content", value);
}

function updateLanguageUrl() {
  if (!window.history || typeof window.history.replaceState !== "function") return;
  const url = new URL(window.location.href);
  if (currentLanguage === "en") url.searchParams.set("lang", "en");
  else url.searchParams.delete("lang");
  if (url.href !== window.location.href) {
    window.history.replaceState(null, "", `${url.pathname}${url.search}${url.hash}`);
  }
}

function applyChangelogLanguage() {
  const preferredLanguage = currentLanguage === "en" ? "en" : "zh";
  document.querySelectorAll("[data-changelog-item]").forEach((item) => {
    const variants = Array.from(item.querySelectorAll("[data-changelog-lang]"));
    if (variants.length === 0) return;
    const preferred = variants.find((variant) => variant.dataset.changelogLang === preferredLanguage);
    const fallback = variants.find((variant) => variant.dataset.changelogLang === "zh") || variants[0];
    variants.forEach((variant) => {
      variant.hidden = variant !== (preferred || fallback);
    });
  });

  document.querySelectorAll("[data-changelog-link]").forEach((link) => {
    link.href = currentLanguage === "en"
      ? "https://github.com/kingstacker/Kkindle/blob/master/CHANGELOG.en.md"
      : "https://github.com/kingstacker/Kkindle/blob/master/CHANGELOG.md";
  });
}

function applyLanguage(language, updateUrl = false) {
  currentLanguage = language === "en" ? "en" : "zh";
  const copy = translations[currentLanguage];

  document.documentElement.lang = currentLanguage === "en" ? "en" : "zh-CN";
  document.querySelectorAll("[data-i18n]").forEach((element) => {
    const key = element.dataset.i18n;
    if (copy[key]) element.textContent = copy[key];
  });
  document.querySelectorAll("[data-changelog-date]").forEach((element) => {
    const date = formatReleaseDate(element.dataset.changelogDate);
    if (date) element.textContent = date;
  });
  document.querySelectorAll("[data-i18n-alt]").forEach((element) => {
    const key = element.dataset.i18nAlt;
    if (copy[key]) element.alt = copy[key];
  });
  document.querySelectorAll("[data-i18n-label]").forEach((element) => {
    const key = element.dataset.i18nLabel;
    if (copy[key]) element.setAttribute("aria-label", copy[key]);
  });
  applyChangelogLanguage();

  const languageToggle = document.querySelector("#language-toggle");
  if (languageToggle) {
    languageToggle.textContent = currentLanguage === "en" ? "中文" : "EN";
    languageToggle.setAttribute("aria-label", currentLanguage === "en" ? "Switch to Chinese" : "切换为英文");
  }

  document.title = copy.pageTitle;
  updateMetaContent("name", "description", copy.pageDescription);
  updateMetaContent("property", "og:title", copy.ogTitle);
  updateMetaContent("property", "og:description", copy.ogDescription);
  updateMetaContent("property", "og:locale", copy.locale);
  updateMetaContent("property", "og:locale:alternate", copy.localeAlternate);
  updateMetaContent("name", "twitter:title", copy.ogTitle);
  updateMetaContent("name", "twitter:description", copy.ogDescription);

  if (updateUrl) updateLanguageUrl();

  try {
    window.localStorage.setItem("kkindle-site-language", currentLanguage);
  } catch {
    // Local storage is optional.
  }
}

function resyncHashTarget() {
  const id = decodeURIComponent(window.location.hash.replace(/^#/, ""));
  if (!id) return;

  const target = document.getElementById(id);
  if (!target) return;

  window.requestAnimationFrame(() => {
    const headerHeight = document.querySelector(".site-header")?.getBoundingClientRect().height || 0;
    const targetTop = target.getBoundingClientRect().top + window.scrollY;
    window.scrollTo({ top: Math.max(0, targetTop - headerHeight - 16), behavior: "auto" });
  });
}

function formatReleaseDate(value) {
  if (!value) return "";
  const date = /^\d{4}-\d{2}-\d{2}$/.test(value)
    ? new Date(`${value}T12:00:00`)
    : new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleDateString(currentLanguage === "en" ? "en-US" : "zh-CN", {
    year: "numeric",
    month: "short",
    day: "numeric"
  });
}

function setFallbackLinks() {
  releaseFailed = true;
  document.querySelectorAll("[data-release-version]").forEach((element) => {
    element.removeAttribute("data-i18n");
    element.textContent = translations[currentLanguage].releaseUnavailable;
  });
  document.querySelectorAll("[data-download-key]").forEach((link) => {
    link.href = RELEASE_PAGE;
    link.classList.remove("disabled");
    link.removeAttribute("aria-disabled");
  });
  document.querySelectorAll("[data-release-link]").forEach((link) => {
    link.href = RELEASE_PAGE;
  });
  markRecommendedPlatform();
  resyncHashTarget();
}

function detectPlatform() {
  const platform = (navigator.userAgentData?.platform || navigator.platform || navigator.userAgent || "").toLowerCase();
  if (platform.includes("win")) return "windows";
  // iPadOS reports "MacIntel"; it cannot run the desktop macOS package.
  if (platform.includes("mac") && navigator.maxTouchPoints > 1) return null;
  if (platform.includes("mac")) return "macos";
  if (platform.includes("linux")) return "linux";
  return null;
}

function markRecommendedPlatform() {
  const detected = detectPlatform();
  document.querySelectorAll("[data-platform]").forEach((card) => {
    const unavailable =
      card.classList.contains("disabled") ||
      Boolean(card.querySelector("[data-download-key].disabled"));
    const recommended = Boolean(detected) && card.dataset.platform === detected && !unavailable;
    card.classList.toggle("is-recommended", recommended);
    const badge = card.querySelector(".platform-badge");
    if (badge) badge.hidden = !recommended;
  });
}

function renderRelease(release) {
  releaseFailed = false;
  latestRelease = release;
  const version = release.tag_name || release.name || "";

  document.querySelectorAll("[data-release-version]").forEach((element) => {
    element.removeAttribute("data-i18n");
    element.textContent = version || translations[currentLanguage].releaseUnavailable;
  });
  document.querySelectorAll("[data-release-link]").forEach((link) => {
    link.href = release.html_url || RELEASE_PAGE;
  });

  const assets = Array.isArray(release.assets) ? release.assets : [];
  Object.entries(assetMatchers).forEach(([key, matcher]) => {
    const asset = assets.find((item) => matcher.test(item.name || ""));
    document.querySelectorAll(`[data-download-key="${key}"]`).forEach((link) => {
      const pickMeta = link.querySelector(".platform-pick-meta");
      if (asset?.browser_download_url) {
        link.href = asset.browser_download_url;
        link.classList.remove("disabled");
        link.removeAttribute("aria-disabled");
        if (pickMeta?.dataset.i18n) {
          pickMeta.textContent =
            translations[currentLanguage][pickMeta.dataset.i18n] || pickMeta.textContent;
        }
      } else {
        link.href = release.html_url || RELEASE_PAGE;
        link.classList.add("disabled");
        // First-screen picks still open a useful page (the release), so they are
        // not announced as disabled; detailed asset rows are.
        if (link.classList.contains("platform-pick")) {
          link.removeAttribute("aria-disabled");
          if (pickMeta) pickMeta.textContent = translations[currentLanguage].openRelease;
        } else {
          link.setAttribute("aria-disabled", "true");
        }
      }
    });
  });
  applyChangelogLanguage();

  markRecommendedPlatform();
  resyncHashTarget();
}

async function fetchRelease(url, options = {}) {
  const response = await fetch(url, { cache: "no-cache", ...options });
  if (!response.ok) throw new Error(`${url} returned ${response.status}`);
  return response.json();
}

function isReleasePayload(release) {
  return Boolean(
    release &&
      typeof release === "object" &&
      (Array.isArray(release.assets) || typeof release.html_url === "string")
  );
}

async function loadLatestRelease() {
  let rendered = false;

  // Prefer the same-origin snapshot: no rate limit, works when GitHub is slow
  // or blocked, and renders the version before the page has fully painted.
  try {
    const cached = await fetchRelease(SITE_RELEASE_JSON);
    if (isReleasePayload(cached)) {
      renderRelease(cached);
      rendered = true;
    }
  } catch {
    // The snapshot is optional; fall through to the live API.
  }

  // Revalidate in the background so a stale snapshot still converges on the
  // latest release when GitHub is reachable.
  try {
    const live = await fetchRelease(RELEASE_API, {
      cache: "default",
      headers: { Accept: "application/vnd.github+json" }
    });
    if (isReleasePayload(live)) {
      if (!latestRelease || live.tag_name !== latestRelease.tag_name) renderRelease(live);
      rendered = true;
    }
  } catch {
    // Rate limited or unreachable; the snapshot (if any) is already rendered.
  }

  if (!rendered) setFallbackLinks();
}

function setupNavigation() {
  const toggle = document.querySelector("#menu-toggle");
  const nav = document.querySelector("#main-nav");
  if (!toggle || !nav) return;

  toggle.addEventListener("click", () => {
    const isOpen = nav.classList.toggle("is-open");
    toggle.setAttribute("aria-expanded", String(isOpen));
  });

  nav.querySelectorAll("a").forEach((link) => {
    link.addEventListener("click", () => {
      nav.classList.remove("is-open");
      toggle.setAttribute("aria-expanded", "false");
    });
  });

  document.addEventListener("keydown", (event) => {
    if (event.key !== "Escape" || !nav.classList.contains("is-open")) return;
    nav.classList.remove("is-open");
    toggle.setAttribute("aria-expanded", "false");
    toggle.focus();
  });
}

function setupQQGroupCopy() {
  const button = document.querySelector("#qq-group-copy");
  const status = document.querySelector("#qq-group-copy-status");
  if (!button || !status) return;

  let statusTimer;
  const showStatus = (message, isError = false) => {
    status.textContent = message;
    status.classList.toggle("is-error", isError);
    status.classList.add("is-visible");
    window.clearTimeout(statusTimer);
    statusTimer = window.setTimeout(() => {
      status.classList.remove("is-visible");
    }, 2400);
  };

  button.addEventListener("click", async () => {
    const copy = translations[currentLanguage];
    try {
      await navigator.clipboard.writeText("1109898894");
      showStatus(copy.qqGroupCopied);
    } catch {
      showStatus(copy.qqGroupCopyFailed, true);
    }
  });
}

document.addEventListener("DOMContentLoaded", () => {
  applyLanguage(getStoredLanguage());
  markRecommendedPlatform();
  setupNavigation();
  setupQQGroupCopy();

  document.querySelector("#language-toggle")?.addEventListener("click", () => {
    applyLanguage(currentLanguage === "en" ? "zh" : "en", true);
    markRecommendedPlatform();
    if (latestRelease) renderRelease(latestRelease);
    else if (releaseFailed) setFallbackLinks();
    else resyncHashTarget();
  });

  window.addEventListener("hashchange", resyncHashTarget);
  loadLatestRelease();
});
