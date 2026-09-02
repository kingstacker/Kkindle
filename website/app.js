const REPOSITORY = "kingstacker/Kkindle";
const RELEASE_PAGE = `https://github.com/${REPOSITORY}/releases/latest`;
const RELEASE_API = `https://api.github.com/repos/${REPOSITORY}/releases/latest`;

const translations = {
  zh: {
    brandDescriptor: "个人电子书与 Kindle 工作台",
    navProduct: "产品",
    navFeatures: "功能",
    navDownload: "下载",
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
    heroVisualLabel: "YOUR LIBRARY, YOUR WAY",
    heroVisualNote: "A quiet place for a busy library.",
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
    philosophyKicker: "A SMALL TOOL WITH A LONG VIEW",
    philosophyTitle: "本地优先，安静可靠，属于你的阅读空间。",
    philosophyBody: "Kkindle 将书籍、封面、阅读进度和批注保存在本机。网络功能按需开启，AI 只发送相关片段，不上传整本书。",
    philosophyPoint1: "跨平台桌面应用",
    philosophyPoint2: "开放格式与开源许可",
    philosophyPoint3: "数据留在你的设备上",
    downloadKicker: "DOWNLOAD Kkindle",
    downloadTitle: "选一个平台，开始建立你的书库。",
    downloadLead: "官网只负责展示与指路，安装包来自 GitHub Releases。下载区会自动选择最新稳定版本。",
    stableLabel: "最新稳定版本",
    checking: "正在获取…",
    releasePage: "查看完整 Release ↗",
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
    brandDescriptor: "Personal ebook & Kindle workspace",
    navProduct: "Product",
    navFeatures: "Features",
    navDownload: "Download",
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
    heroVisualLabel: "YOUR LIBRARY, YOUR WAY",
    heroVisualNote: "A quiet place for a busy library.",
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
    philosophyKicker: "A SMALL TOOL WITH A LONG VIEW",
    philosophyTitle: "Local-first, quiet, reliable—and yours.",
    philosophyBody: "Kkindle keeps books, covers, reading progress, and annotations on your machine. Network features are opt-in, and AI receives relevant excerpts—not entire books.",
    philosophyPoint1: "Cross-platform desktop app",
    philosophyPoint2: "Open formats and license",
    philosophyPoint3: "Your data stays with you",
    downloadKicker: "DOWNLOAD Kkindle",
    downloadTitle: "Choose a platform. Start your library.",
    downloadLead: "The site points the way; GitHub Releases hosts the packages. The download area selects the latest stable release automatically.",
    stableLabel: "Latest stable release",
    checking: "Loading…",
    releasePage: "View full Release ↗",
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

function applyLanguage(language) {
  currentLanguage = language === "en" ? "en" : "zh";
  const copy = translations[currentLanguage];

  document.documentElement.lang = currentLanguage === "en" ? "en" : "zh-CN";
  document.querySelectorAll("[data-i18n]").forEach((element) => {
    const key = element.dataset.i18n;
    if (copy[key]) element.textContent = copy[key];
  });
  document.querySelectorAll("[data-i18n-alt]").forEach((element) => {
    const key = element.dataset.i18nAlt;
    if (copy[key]) element.alt = copy[key];
  });

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
  const date = new Date(value);
  if (Number.isNaN(date.getTime())) return "";
  return date.toLocaleDateString(currentLanguage === "en" ? "en-US" : "zh-CN", {
    year: "numeric",
    month: "short",
    day: "numeric"
  });
}

function setFallbackLinks() {
  releaseFailed = true;
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

  const primary = document.querySelector("[data-primary-download]");
  const installer = assets.find((item) => assetMatchers["windows-installer"].test(item.name || ""));
  if (primary) primary.href = installer?.browser_download_url || "#downloads";
  setStatus("releaseLoaded");
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

document.addEventListener("DOMContentLoaded", () => {
  applyLanguage(getStoredLanguage());
  markRecommendedPlatform();
  setupNavigation();

  document.querySelector("#language-toggle")?.addEventListener("click", () => {
    applyLanguage(currentLanguage === "en" ? "zh" : "en");
    markRecommendedPlatform();
    if (latestRelease) renderRelease(latestRelease);
    else if (releaseFailed) setFallbackLinks();
  });

  loadLatestRelease();
});
