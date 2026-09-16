const REPOSITORY = "kingstacker/Kkindle";
const RELEASE_PAGE = `https://github.com/${REPOSITORY}/releases/latest`;
const RELEASE_API = `https://api.github.com/repos/${REPOSITORY}/releases/latest`;

const translations = {
  zh: {
    skipToContent: "跳到主要内容",
    menuLabel: "打开菜单",
    navProduct: "产品",
    navFeatures: "功能",
    navDownload: "下载",
    navChangelog: "更新日志",
    navAbout: "关于",
    headerGitHub: "GitHub",
    heroKicker: "KINDLE, WITHOUT THE FRICTION.",
    heroTitle: "把书库、阅读与 Kindle 连接起来。",
    heroLead: "Kkindle 是一款安静、跨平台的电子书书库与 Kindle 工作台。整理你的书，专注地读，然后把它们带到真正的阅读设备上。",
    heroDownload: "下载 Kkindle",
    heroSource: "查看源码",
    heroFactPlatforms: "桌面平台",
    heroFactFormats: "常用格式",
    heroFactLicense: "开源许可",
    signalLocal: "LOCAL-FIRST",
    signalFormats: "EPUB / PDF / MOBI / AZW3",
    signalKindle: "KINDLE READY",
    signalAi: "AI-ASSISTED READING",
    featuresKicker: "THE WHOLE READING DESK",
    featuresTitle: "从一本书开始，直到它真正被读完。",
    featuresLead: "书库、阅读器、设备和阅读记录在同一处工作。没有多余的云端流程，也不打断你的阅读节奏。",
    libraryKicker: "LOCAL LIBRARY",
    libraryTitle: "把混乱的文件，变成真正的书库。",
    libraryBody: "导入 EPUB、PDF、MOBI 和 AZW3，自动识别元数据与封面，按作者、标签、格式和阅读状态找到下一本书。",
    libraryTag1: "拖放导入",
    libraryTag2: "封面与元数据",
    libraryTag3: "本地 SQLite",
    readerKicker: "KREADER",
    readerTitle: "让排版退后，让内容走到前面。",
    readerBody: "支持横排、竖排、滚动、分页和双栏阅读。书签、搜索、脚注、批注与阅读进度都围绕正文展开。",
    readerTag1: "原生排版",
    readerTag2: "竖排阅读",
    readerTag3: "笔记与批注",
    kindleKicker: "KINDLE WORKFLOW",
    kindleTitle: "从桌面书库，到手里的 Kindle。",
    kindleBody: "连接 Kindle 后查看书籍、容量、字体和词典，批量发送、导出和安全退出设备，把传书变成一个清晰的动作。",
    kindleTag1: "USB / WPD / MTP",
    kindleTag2: "字体与词典",
    kindleTag3: "安全传输",
    aiKicker: "READING TOOLS",
    aiTitle: "读得更深，也记得更久。",
    aiBody: "用 AI 解释选文、总结章节或讨论全书；用批注、词典、阅读数据和导出记录，把阅读留下来。",
    aiTag1: "AI 阅读助手",
    aiTag2: "词典查询",
    aiTag3: "阅读数据",
    aboutKicker: "ABOUT KKINDLE",
    aboutTitle: "面向个人阅读的开源桌面工作台。",
    aboutBody: "Kkindle 是一款面向 Windows、Linux 和 macOS 的开源电子书书库与 Kindle 管理工具。它将书库整理、阅读、批注和设备传输放在同一个本地优先的工作流中。",
    aboutPoint1: "Windows / Linux / macOS",
    aboutPoint2: "EPUB / PDF / MOBI / AZW3",
    aboutPoint3: "MIT 开源许可",
    aboutSource: "查看 GitHub 项目",
    qqGroupLabel: "QQ 用户群",
    qqGroupCopyHint: "点击复制群号",
    qqGroupCopied: "已复制群号 1109898894",
    qqGroupCopyFailed: "复制失败，请手动记录群号 1109898894。",
    qqGroupButtonLabel: "复制 QQ 用户群号 1109898894",
    downloadKicker: "DOWNLOAD Kkindle",
    downloadTitle: "选一个平台，开始建立你的书库。",
    downloadLead: "下载区会自动选择 GitHub Releases 的最新稳定版本，国内用户也可通过百度网盘下载。",
    baiduKicker: "国内用户备用下载",
    baiduNote: "GitHub 下载不便时，可从百度网盘获取安装包。",
    baiduLink: "百度网盘下载",
    baiduCodeLabel: "提取码",
    stableLabel: "最新稳定版本",
    checking: "正在获取…",
    releasePage: "查看完整 Release ↗",
    releaseUnavailable: "暂无版本信息",
    recommended: "推荐",
    windowsTitle: "Windows",
    windowsNote: "Windows 11 / x64，自带运行时。",
    windowsInstaller: "安装版",
    windowsPortable: "便携版",
    linuxTitle: "Linux",
    linuxNote: "Debian / Ubuntu，或其它 x64 / arm64 发行版。",
    linuxDebX64: ".deb · x64",
    linuxDebArm64: ".deb · arm64",
    linuxTarX64: "tar.gz · x64",
    linuxTarArm64: "tar.gz · arm64",
    macTitle: "macOS",
    macNote: "macOS 12+，提供 Intel 与 Apple Silicon 包。",
    macArm64: "Apple Silicon",
    macX64: "Intel",
    loadingAsset: "正在读取文件…",
    checksum: "SHA-256 校验值",
    releaseLoading: "正在连接 GitHub Releases…",
    releaseLoaded: "下载地址已同步自 GitHub Releases。",
    releaseFallback: "暂时无法读取版本信息，请打开 Release 页面选择下载文件。",
    unavailable: "当前版本未提供",
    changelogKicker: "CHANGELOG",
    changelogTitle: "每一次更新，都让阅读更顺手。",
    changelogLead: "网页更新日志自动从 CHANGELOG.md 与 CHANGELOG.en.md 生成，展示最近 3 个版本；完整内容见仓库。",
    changelogFull: "查看完整更新日志",
    changelogPlaceholder: "发布网页时，会自动从 CHANGELOG.md 与 CHANGELOG.en.md 生成更新日志。",
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
    footerLicense: "MIT License",
    footerNote: "网页与下载链接由 GitHub Pages 和 GitHub Releases 提供。",
    libraryAlt: "Kkindle 电脑书库界面",
    libraryGridAlt: "Kkindle 书库网格视图",
    readerAlt: "Kreader 阅读器界面",
    kindleAlt: "Kindle 设备书库管理界面",
    aiAlt: "Kkindle AI 阅读助手"
  },
  en: {
    skipToContent: "Skip to content",
    menuLabel: "Open menu",
    navProduct: "Product",
    navFeatures: "Features",
    navDownload: "Download",
    navChangelog: "Changelog",
    navAbout: "About",
    headerGitHub: "GitHub",
    heroKicker: "KINDLE, WITHOUT THE FRICTION.",
    heroTitle: "Bring your library, reading, and Kindle together.",
    heroLead: "Kkindle is a quiet, cross-platform ebook library and Kindle workspace. Organize your books, read with focus, and move them to the device you actually read on.",
    heroDownload: "Download Kkindle",
    heroSource: "View source",
    heroFactPlatforms: "desktop platforms",
    heroFactFormats: "common formats",
    heroFactLicense: "open-source license",
    signalLocal: "LOCAL-FIRST",
    signalFormats: "EPUB / PDF / MOBI / AZW3",
    signalKindle: "KINDLE READY",
    signalAi: "AI-ASSISTED READING",
    featuresKicker: "THE WHOLE READING DESK",
    featuresTitle: "Start with a book. Stay until it is actually read.",
    featuresLead: "Your library, reader, device, and reading records work in one place—with no extra cloud workflow to interrupt the page.",
    libraryKicker: "LOCAL LIBRARY",
    libraryTitle: "Turn a folder of files into a real library.",
    libraryBody: "Import EPUB, PDF, MOBI, and AZW3 files. Parse metadata and covers, then find the next book by author, tag, format, or reading status.",
    libraryTag1: "Drag and drop",
    libraryTag2: "Covers and metadata",
    libraryTag3: "Local SQLite",
    readerKicker: "KREADER",
    readerTitle: "Let the typesetting recede. Let the words lead.",
    readerBody: "Read horizontally or vertically, in scroll, paginated, or two-page layouts. Bookmarks, search, footnotes, annotations, and progress stay close to the text.",
    readerTag1: "Native layout",
    readerTag2: "Vertical writing",
    readerTag3: "Notes and annotations",
    kindleKicker: "KINDLE WORKFLOW",
    kindleTitle: "From desktop library to the Kindle in your hand.",
    kindleBody: "Inspect books, capacity, fonts, and dictionaries on a connected Kindle. Send, export, and safely eject in one clear workflow.",
    kindleTag1: "USB / WPD / MTP",
    kindleTag2: "Fonts and dictionaries",
    kindleTag3: "Safe transfers",
    aiKicker: "READING TOOLS",
    aiTitle: "Read deeper. Keep more of it.",
    aiBody: "Use AI to explain a passage, summarize a chapter, or discuss a whole book. Keep the reading with annotations, dictionaries, data, and exports.",
    aiTag1: "AI reading assistant",
    aiTag2: "Dictionary lookup",
    aiTag3: "Reading data",
    aboutKicker: "ABOUT KKINDLE",
    aboutTitle: "An open-source desktop workspace for personal reading.",
    aboutBody: "Kkindle is an open-source ebook library and Kindle management tool for Windows, Linux, and macOS. It brings library organization, reading, annotations, and device transfer into one local-first workflow.",
    aboutPoint1: "Windows / Linux / macOS",
    aboutPoint2: "EPUB / PDF / MOBI / AZW3",
    aboutPoint3: "MIT License",
    aboutSource: "View the project on GitHub",
    qqGroupLabel: "QQ Group",
    qqGroupCopyHint: "Click to copy group number",
    qqGroupCopied: "Copied QQ group number 1109898894",
    qqGroupCopyFailed: "Copy failed. Please note group number 1109898894 manually.",
    qqGroupButtonLabel: "Copy QQ user group number 1109898894",
    downloadKicker: "DOWNLOAD Kkindle",
    downloadTitle: "Choose a platform. Start your library.",
    downloadLead: "The download area selects the latest stable GitHub release automatically. Users in China can also download via Baidu Netdisk.",
    baiduKicker: "Alternative download for China",
    baiduNote: "If GitHub downloads are inconvenient, get the installer from Baidu Netdisk.",
    baiduLink: "Download from Baidu Netdisk",
    baiduCodeLabel: "Extraction code",
    stableLabel: "Latest stable release",
    checking: "Loading…",
    releasePage: "View full Release ↗",
    releaseUnavailable: "Release unavailable",
    recommended: "Recommended",
    windowsTitle: "Windows",
    windowsNote: "Windows 11 / x64, runtime included.",
    windowsInstaller: "Installer",
    windowsPortable: "Portable",
    linuxTitle: "Linux",
    linuxNote: "Debian / Ubuntu, or other x64 / arm64 distributions.",
    linuxDebX64: ".deb · x64",
    linuxDebArm64: ".deb · arm64",
    linuxTarX64: "tar.gz · x64",
    linuxTarArm64: "tar.gz · arm64",
    macTitle: "macOS",
    macNote: "macOS 12+, with Intel and Apple Silicon packages.",
    macArm64: "Apple Silicon",
    macX64: "Intel",
    loadingAsset: "Reading asset…",
    checksum: "SHA-256 checksums",
    releaseLoading: "Connecting to GitHub Releases…",
    releaseLoaded: "Download links are synced from GitHub Releases.",
    releaseFallback: "Release data is temporarily unavailable. Open the Release page to choose a file.",
    unavailable: "Not provided in this release",
    changelogKicker: "CHANGELOG",
    changelogTitle: "Every update makes reading feel easier.",
    changelogLead: "The latest 3 releases are generated from CHANGELOG.md and CHANGELOG.en.md; see the repository for the full history.",
    changelogFull: "View the full changelog",
    changelogPlaceholder: "Release notes are generated from CHANGELOG.md and CHANGELOG.en.md when the site is deployed.",
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
    footerLicense: "MIT License",
    footerNote: "The site and download links are provided by GitHub Pages and GitHub Releases.",
    libraryAlt: "Kkindle desktop library",
    libraryGridAlt: "Kkindle library grid view",
    readerAlt: "Kreader reading view",
    kindleAlt: "Kindle library management view",
    aiAlt: "Kkindle AI reading assistant"
  }
};

const assetMatchers = {
  "windows-installer": /-win-x64-setup\.exe$/i,
  "windows-portable": /-win-x64-portable\.zip$/i,
  "linux-deb-x64": /_amd64\.deb$/i,
  "linux-deb-arm64": /_arm64\.deb$/i,
  "linux-tar-x64": /-linux-x64\.tar\.gz$/i,
  "linux-tar-arm64": /-linux-arm64\.tar\.gz$/i,
  "mac-arm64": /-osx-arm64\.tar\.gz$/i,
  "mac-x64": /-osx-x64\.tar\.gz$/i,
  checksums: /^SHA256SUMS\.txt$/i
};

let currentLanguage = "zh";
let latestRelease = null;
let releaseFailed = false;

function getStoredLanguage() {
  try {
    const stored = window.localStorage.getItem("kkindle-site-language");
    if (stored === "zh" || stored === "en") return stored;
  } catch {
    // Local storage is optional.
  }

  return navigator.language && navigator.language.toLowerCase().startsWith("en") ? "en" : "zh";
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

function applyLanguage(language) {
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

  document.title = currentLanguage === "en"
    ? "Kkindle — Personal ebook & Kindle workspace"
    : "Kkindle — 个人电子书与 Kindle 工作台";

  try {
    window.localStorage.setItem("kkindle-site-language", currentLanguage);
  } catch {
    // Local storage is optional.
  }
}

function setStatus(key) {
  const status = document.querySelector("#release-status");
  if (!status) return;
  status.dataset.i18n = key;
  status.textContent = translations[currentLanguage][key] || key;
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

function formatBytes(bytes) {
  if (!Number.isFinite(bytes) || bytes <= 0) return "";
  const units = ["B", "KB", "MB", "GB"];
  let value = bytes;
  let index = 0;
  while (value >= 1024 && index < units.length - 1) {
    value /= 1024;
    index += 1;
  }
  return `${value.toFixed(index === 0 ? 0 : 1)} ${units[index]}`;
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
  const versionElement = document.querySelector("#latest-version");
  if (versionElement) {
    versionElement.removeAttribute("data-i18n");
    versionElement.textContent = translations[currentLanguage].releaseUnavailable;
  }
  document.querySelectorAll("[data-download-key]").forEach((link) => {
    link.href = RELEASE_PAGE;
    link.classList.remove("disabled");
    link.removeAttribute("aria-disabled");
  });
  document.querySelectorAll("[data-file-meta]").forEach((element) => {
    element.removeAttribute("data-i18n");
    element.textContent = currentLanguage === "en" ? "Open release page" : "打开 Release 页面";
  });
  document.querySelectorAll("[data-release-link]").forEach((link) => {
    link.href = RELEASE_PAGE;
  });
  setStatus("releaseFallback");
  resyncHashTarget();
}

function detectPlatform() {
  const platform = (navigator.userAgentData?.platform || navigator.platform || navigator.userAgent || "").toLowerCase();
  if (platform.includes("win")) return "windows";
  if (platform.includes("mac") || platform.includes("iphone") || platform.includes("ipad")) return "macos";
  if (platform.includes("linux")) return "linux";
  return null;
}

function markRecommendedPlatform() {
  const detected = detectPlatform();
  document.querySelectorAll("[data-platform]").forEach((card) => {
    const recommended = detected && card.dataset.platform === detected;
    card.classList.toggle("is-recommended", Boolean(recommended));
    const badge = card.querySelector(".platform-badge");
    if (badge) badge.hidden = !recommended;
  });
}

function renderRelease(release) {
  releaseFailed = false;
  latestRelease = release;
  const versionElement = document.querySelector("#latest-version");
  const publishedElement = document.querySelector("#release-published");
  const version = release.tag_name || release.name || "";

  if (versionElement) versionElement.textContent = version;
  if (publishedElement) publishedElement.textContent = formatReleaseDate(release.published_at);
  document.querySelectorAll("[data-release-link]").forEach((link) => {
    link.href = release.html_url || RELEASE_PAGE;
  });

  const assets = Array.isArray(release.assets) ? release.assets : [];
  Object.entries(assetMatchers).forEach(([key, matcher]) => {
    const asset = assets.find((item) => matcher.test(item.name || ""));
    document.querySelectorAll(`[data-download-key="${key}"]`).forEach((link) => {
      const meta = document.querySelector(`[data-file-meta="${key}"]`);
      if (asset?.browser_download_url) {
        link.href = asset.browser_download_url;
        link.classList.remove("disabled");
        link.removeAttribute("aria-disabled");
        if (meta) {
          meta.removeAttribute("data-i18n");
          meta.textContent = [formatBytes(asset.size), asset.name].filter(Boolean).join(" · ");
        }
      } else {
        link.href = release.html_url || RELEASE_PAGE;
        link.classList.add("disabled");
        link.setAttribute("aria-disabled", "true");
        if (meta) {
          meta.removeAttribute("data-i18n");
          meta.textContent = translations[currentLanguage].unavailable;
        }
      }
    });
  });
  applyChangelogLanguage();

  const primary = document.querySelector("[data-primary-download]");
  if (primary) primary.href = "#platform-downloads";
  setStatus("releaseLoaded");
  resyncHashTarget();
}

async function loadLatestRelease() {
  try {
    const response = await fetch(RELEASE_API, {
      headers: { Accept: "application/vnd.github+json" },
      cache: "default"
    });
    if (!response.ok) throw new Error(`GitHub returned ${response.status}`);
    const release = await response.json();
    renderRelease(release);
  } catch {
    setFallbackLinks();
  }
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
}

function setupQQGroupCopy() {
  const button = document.querySelector("#qq-group-copy");
  const status = document.querySelector("#qq-group-copy-status");
  if (!button || !status) return;

  button.addEventListener("click", async () => {
    const copy = translations[currentLanguage];
    try {
      await navigator.clipboard.writeText("1109898894");
      status.textContent = copy.qqGroupCopied;
    } catch {
      status.textContent = copy.qqGroupCopyFailed;
    }
  });
}

document.addEventListener("DOMContentLoaded", () => {
  applyLanguage(getStoredLanguage());
  markRecommendedPlatform();
  setupNavigation();
  setupQQGroupCopy();

  document.querySelector("#language-toggle")?.addEventListener("click", () => {
    applyLanguage(currentLanguage === "en" ? "zh" : "en");
    markRecommendedPlatform();
    if (latestRelease) renderRelease(latestRelease);
    else if (releaseFailed) setFallbackLinks();
    else resyncHashTarget();
  });

  window.addEventListener("hashchange", resyncHashTarget);
  loadLatestRelease();
});
