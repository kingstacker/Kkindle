const REPOSITORY = "kingstacker/Kkindle";
const RELEASE_PAGE = `https://github.com/${REPOSITORY}/releases/latest`;
const RELEASE_API = `https://api.github.com/repos/${REPOSITORY}/releases/latest`;
const SITE_RELEASE_JSON = "_release.json";

const translations = {
  zh: {
    skipToContent: "跳到主要内容",
    menuLabel: "打开菜单",
    navProduct: "产品",
    navFeatures: "功能",
    navMembership: "会员",
    navDownload: "下载",
    navManual: "使用手册",
    navChangelog: "更新日志",
    navAbout: "关于",
    headerGitHub: "GitHub",
    heroKicker: "KINDLE, WITHOUT THE FRICTION.",
    heroTitle: "把书库、阅读与 Kindle 连接起来。",
    heroLead: "Kkindle 是一款安静、跨平台的电子书书库与 Kindle 工作台。整理你的书，专注地读，然后把它们带到真正的阅读设备上。",
    heroDownload: "下载 Kkindle",
    heroSource: "查看源码",
    heroPlatformsLabel: "按平台下载",
    heroPlatformWindowsNote: "安装版",
    heroPlatformMacNote: "Apple Silicon / Intel",
    comingSoon: "即将推出",
    heroFactPlatforms: "平台",
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
    membershipKicker: "MEMBERSHIP",
    membershipTitle: "选择适合你的阅读方式。",
    membershipLead: "会员绑定 Kkindle 账号。中国区与国际区的账号、会员和同步数据相互独立，不互通。",
    membershipRegionLabel: "服务器区域",
    regionChina: "中国区",
    regionInternational: "国际区",
    regionUnavailable: "暂未上线",
    intlTitle: "国际区会员暂未上线",
    intlBody: "上线后将在这里公布国际区的价格与会员权益。",
    planFreeTagline: "让阅读自由开始",
    planFreePeriod: "永久免费",
    planFreeFeature1: "畅享完整本地阅读体验",
    planFreeFeature2: "USB Kindle 书籍双向同步",
    planFreeFeature3: "USB Kindle 批注双向同步",
    planFreeFeature4: "插件扩展与自定义 AI 接口",
    planFreeFoot: "不含跨端同步与官方云空间",
    planUnlockName: "功能解锁",
    planUnlockTagline: "你的书库，由你掌控",
    planUnlockPeriod: "一次购买，永久使用",
    planUnlockFeature1: "包含全部 Free 权益",
    planUnlockFeature2: "解锁自定义 S3 云同步",
    planUnlockFeature3: "进度与批注完整同步",
    planUnlockFeature4: "存储容量由自己掌控",
    planUnlockFeature5: "一次买断，无需续费",
    planUnlockFoot: "需自行配置 S3 兼容存储服务，存储与流量费用由用户承担",
    planPlusTagline: "让每一本书，随你而行",
    planPerYear: "/年",
    planPlusPeriod: "折合约 ¥10.67/月 · 按年支付",
    planPlusFeature1: "10GB 私人书籍云空间",
    planPlusFeature2: "书籍与阅读数据自动同步",
    planPlusFeature3: "无需配置存储桶，登录即用",
    planPlusFeature4: "多平台书库随时访问",
    planPlusFeature5: "订阅期内包含全部功能解锁权益",
    planPlusFoot: "10GB 云空间 · 每会员年度 50GB 官方云下载额度 · 订阅期内含自备 S3 / WebDAV · 最多同步 6 台设备；未开通过 Plus 可领取一次 7 天免费试用（100MB 云空间 · 200MB 累计下载）",
    compareCaption: "中国区会员权益对比",
    compareFeature: "功能",
    compareIncluded: "包含",
    compareExcluded: "不包含",
    comparePrice: "价格",
    compareFreePrice: "免费",
    compareUnlockPrice: "¥88 永久",
    comparePlusPrice: "¥128/年",
    compareLocal: "本地书库管理与阅读",
    compareUsbBooks: "USB Kindle 书籍双向同步",
    compareUsbNotes: "USB Kindle 批注双向同步",
    comparePlugins: "插件功能",
    compareAi: "自定义 AI 接口",
    compareProgress: "跨端阅读进度同步",
    compareNotes: "跨端书签、批注云同步",
    compareBooks: "跨端书籍文件云同步",
    compareDevices: "云同步设备数量",
    compareDeviceCount: "6 台",
    compareS3: "自定义 S3",
    compareWhileSubscribed: "订阅期内",
    compareStorage: "官方云空间",
    compareTraffic: "官方云下载流量",
    compareTrafficValue: "50GB/年",
    compareNote: "功能解锁用户使用自备 S3，同步存储与流量费用自行承担。Plus 的自备 S3 / WebDAV 仅在订阅期内可用，到期后收回；已购功能解锁永久有效。未开通过 Plus 的账号可领取一次 7 天免费试用，试用到期后云端数据保留 7 天，正式 Plus 到期后云端数据保留一个月。USB Kindle 双向批注同步的兼容范围因设备型号和书籍格式而异。",
    aboutKicker: "About Kkindle",
    aboutTitle: "面向个人阅读的开源桌面工作台。",
    aboutBody: "Kkindle 是一款面向 Windows、Linux、macOS、Android 和 iOS 的开源电子书书库与 Kindle 管理工具。它将书库整理、阅读、批注和设备传输放在同一个本地优先的工作流中。",
    aboutPoint1: "Windows / Linux / macOS / Android / iOS",
    aboutPoint2: "EPUB / PDF / MOBI / AZW3",
    aboutPoint3: "GPL-3.0 开源许可",
    aboutSource: "查看 GitHub 项目",
    qqGroupLabel: "QQ 用户群",
    qqGroupCopied: "已复制群号 1109898894",
    qqGroupCopyFailed: "复制失败，请手动记录群号 1109898894。",
    qqGroupButtonLabel: "复制 QQ 用户群号 1109898894",
    downloadKicker: "DOWNLOAD Kkindle",
    downloadTitle: "选一个平台，开始建立你的书库。",
    downloadLead: "下载区会自动选择最新稳定版，安装包优先通过本站镜像下载；也可通过百度网盘下载。",
    baiduKicker: "备用下载",
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
    androidNote: "The Android build is in preparation. Installers and download links will be published later.",
    iosNote: "The iOS build is in preparation. Installers and download links will be published later.",
    androidAssetNote: "No download link yet",
    iosAssetNote: "No download link yet",
    androidNote: "Android 版正在准备中，安装包和下载链接稍后公布。",
    iosNote: "iOS 版正在准备中，安装包和下载链接稍后公布。",
    androidAssetNote: "暂时没有下载链接",
    iosAssetNote: "暂时没有下载链接",
    loadingAsset: "正在读取文件…",
    checksum: "SHA-256 校验值",
    releaseLoading: "正在获取最新版本…",
    releaseLoaded: "下载地址来自本站镜像（同步自 GitHub Releases）。",
    releaseFallback: "暂时无法读取版本信息，请打开 Release 页面选择下载文件。",
    unavailable: "当前版本未提供",
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
    footerNote: "官网与镜像下载由 kkindle.stacker.beauty 提供。",
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
    navMembership: "Membership",
    navDownload: "Download",
    navManual: "Chinese manual",
    navChangelog: "Changelog",
    navAbout: "About",
    headerGitHub: "GitHub",
    heroKicker: "KINDLE, WITHOUT THE FRICTION.",
    heroTitle: "Bring your library, reading, and Kindle together.",
    heroLead: "Kkindle is a quiet, cross-platform ebook library and Kindle workspace. Organize your books, read with focus, and move them to the device you actually read on.",
    heroDownload: "Download Kkindle",
    heroSource: "View source",
    heroPlatformsLabel: "Download by platform",
    heroPlatformWindowsNote: "Installer",
    heroPlatformMacNote: "Apple Silicon / Intel",
    comingSoon: "Coming soon",
    heroFactPlatforms: "platforms",
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
    membershipKicker: "MEMBERSHIP",
    membershipTitle: "Choose the way you read.",
    membershipLead: "Memberships belong to your Kkindle account. Accounts, memberships and synced data are kept separate between the China and International regions.",
    membershipRegionLabel: "Server region",
    regionChina: "China",
    regionInternational: "International",
    regionUnavailable: "Not yet available",
    intlTitle: "International memberships are not available yet",
    intlBody: "Pricing and membership details for the International region will be published here at launch.",
    planFreeTagline: "Start reading freely",
    planFreePeriod: "Free forever",
    planFreeFeature1: "The full local reading experience",
    planFreeFeature2: "Two-way USB Kindle book sync",
    planFreeFeature3: "Two-way USB Kindle annotation sync",
    planFreeFeature4: "Plugins and custom AI APIs",
    planFreeFoot: "No sync across devices or official cloud storage",
    planUnlockName: "Feature Unlock",
    planUnlockTagline: "Your library, in your hands",
    planUnlockPeriod: "Buy once, use forever",
    planUnlockFeature1: "All Free features included",
    planUnlockFeature2: "Cloud sync with your own S3 storage",
    planUnlockFeature3: "Full sync of reading progress and annotations",
    planUnlockFeature4: "Storage capacity under your control",
    planUnlockFeature5: "One-time purchase, no renewals",
    planUnlockFoot: "Requires your own S3-compatible storage service; you cover its storage and traffic costs",
    planPlusTagline: "Your books, wherever you go",
    planPerYear: "/year",
    planPlusPeriod: "About ¥10.67/month · billed yearly",
    planPlusFeature1: "10GB of private cloud storage for books",
    planPlusFeature2: "Automatic book and reading data sync",
    planPlusFeature3: "No bucket to configure: just sign in",
    planPlusFeature4: "Your library on every platform, anytime",
    planPlusFeature5: "All Feature Unlock features included while subscribed",
    planPlusFoot: "10GB cloud storage · 50GB of official cloud downloads per membership year · S3 / WebDAV included while subscribed · sync up to 6 devices; accounts that never had Plus can claim one 7-day free trial (100MB storage · 200MB cumulative downloads)",
    compareCaption: "China region membership comparison",
    compareFeature: "Feature",
    compareIncluded: "Included",
    compareExcluded: "Not included",
    comparePrice: "Price",
    compareFreePrice: "Free",
    compareUnlockPrice: "¥88 lifetime",
    comparePlusPrice: "¥128/year",
    compareLocal: "Local library management and reading",
    compareUsbBooks: "Two-way USB Kindle book sync",
    compareUsbNotes: "Two-way USB Kindle annotation sync",
    comparePlugins: "Plugins",
    compareAi: "Custom AI APIs",
    compareProgress: "Reading progress sync across devices",
    compareNotes: "Bookmark and annotation cloud sync across devices",
    compareBooks: "Book file cloud sync across devices",
    compareDevices: "Cloud sync devices",
    compareDeviceCount: "6 devices",
    compareS3: "Custom S3",
    compareWhileSubscribed: "While subscribed",
    compareStorage: "Official cloud storage",
    compareTraffic: "Official cloud downloads",
    compareTrafficValue: "50 GB / year",
    compareNote: "Feature Unlock uses your own S3 storage; you cover its storage and traffic costs. Plus includes your own S3 / WebDAV only while subscribed; this access is revoked on expiry, while a purchased Feature Unlock remains permanent. Accounts that never had Plus can claim one 7-day free trial; trial cloud data is retained for 7 days after it ends, while paid Plus data is retained for one month. Two-way USB Kindle annotation sync support varies by device model and book format.",
    aboutKicker: "About Kkindle",
    aboutTitle: "An open-source desktop workspace for personal reading.",
    aboutBody: "Kkindle is an open-source ebook library and Kindle management tool for Windows, Linux, macOS, Android, and iOS. It brings library organization, reading, annotations, and device transfer into one local-first workflow.",
    aboutPoint1: "Windows / Linux / macOS / Android / iOS",
    aboutPoint2: "EPUB / PDF / MOBI / AZW3",
    aboutPoint3: "GPL-3.0 License",
    aboutSource: "View the project on GitHub",
    qqGroupLabel: "QQ Group",
    qqGroupCopied: "Copied QQ group number 1109898894",
    qqGroupCopyFailed: "Copy failed. Please note group number 1109898894 manually.",
    qqGroupButtonLabel: "Copy QQ user group number 1109898894",
    downloadKicker: "DOWNLOAD Kkindle",
    downloadTitle: "Choose a platform. Start your library.",
    downloadLead: "The download area selects the latest stable version automatically, with installers served primarily from this site's mirror. You can also download via Baidu Netdisk.",
    baiduKicker: "Alternative download",
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
    androidNote: "The Android build is in preparation. Installers and download links will be published later.",
    iosNote: "The iOS build is in preparation. Installers and download links will be published later.",
    androidAssetNote: "No download link yet",
    iosAssetNote: "No download link yet",
    androidNote: "Android 版正在准备中，安装包和下载链接稍后公布。",
    iosNote: "iOS 版正在准备中，安装包和下载链接稍后公布。",
    androidAssetNote: "暂时没有下载链接",
    iosAssetNote: "暂时没有下载链接",
    loadingAsset: "Reading asset…",
    checksum: "SHA-256 checksums",
    releaseLoading: "Fetching the latest version…",
    releaseLoaded: "Download links are served from this site's mirror, synced from GitHub Releases.",
    releaseFallback: "Release data is temporarily unavailable. Open the Release page to choose a file.",
    unavailable: "Not provided in this release",
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
    footerNote: "The official site and mirror downloads are provided by kkindle.stacker.beauty.",
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
  "mac-auto": /-osx-(?:arm64|x64)\.tar\.gz$/i,
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
  if (platform.includes("android")) return "android";
  if (platform.includes("iphone") || platform.includes("ipad") || platform.includes("ios")) return "ios";
  if (platform.includes("win")) return "windows";
  if (platform.includes("mac")) return "macos";
  if (platform.includes("linux")) return "linux";
  return null;
}

function markRecommendedPlatform() {
  const detected = detectPlatform();
  document.querySelectorAll("[data-platform]").forEach((card) => {
    const unavailable = card.getAttribute("aria-disabled") === "true";
    const recommended = !unavailable && detected && card.dataset.platform === detected;
    card.classList.toggle("is-recommended", Boolean(recommended));
    if (unavailable) return;
    const badge = card.querySelector(".platform-badge");
    if (badge) badge.hidden = !recommended;
  });
}

function findReleaseAsset(key, assets) {
  if (key === "mac-auto") {
    const architecture = navigator.userAgentData?.architecture || "";
    const preferX64 = /x86/i.test(architecture);
    const preferred = assets.find((item) =>
      (preferX64 ? /-osx-x64\.tar\.gz$/i : /-osx-arm64\.tar\.gz$/i).test(item.name || ""));
    if (preferred) return preferred;
  }
  const matcher = assetMatchers[key];
  return matcher ? assets.find((item) => matcher.test(item.name || "")) : undefined;
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
  Object.entries(assetMatchers).forEach(([key]) => {
    const asset = findReleaseAsset(key, assets);
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

  setStatus("releaseLoaded");
  resyncHashTarget();
}

async function fetchRelease(url, options = {}) {
  const response = await fetch(url, { cache: "no-cache", ...options });
  if (!response.ok) throw new Error(`HTTP ${response.status}`);
  const release = await response.json();
  if (!release || typeof release !== "object" || !Array.isArray(release.assets)) {
    throw new Error("Invalid release payload");
  }
  return release;
}

async function loadLatestRelease() {
  try {
    renderRelease(await fetchRelease(SITE_RELEASE_JSON));
    return;
  } catch {
    // Fall back to the GitHub API when the bundled snapshot is missing.
  }

  try {
    renderRelease(await fetchRelease(RELEASE_API, {
      headers: { Accept: "application/vnd.github+json" }
    }));
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

function setupMembershipRegions() {
  const tabs = Array.from(document.querySelectorAll("[data-region-tab]"));
  if (tabs.length === 0) return;

  // Tab pattern: one tab in the Tab order; arrows, Home and End move between tabs.
  const select = (tab, moveFocus) => {
    tabs.forEach((item) => {
      const selected = item === tab;
      item.setAttribute("aria-selected", String(selected));
      item.tabIndex = selected ? 0 : -1;
      const panel = document.getElementById(item.getAttribute("aria-controls"));
      if (panel) panel.hidden = !selected;
    });
    if (moveFocus) tab.focus();
  };

  tabs.forEach((tab, index) => {
    tab.addEventListener("click", () => select(tab, false));
    tab.addEventListener("keydown", (event) => {
      let next;
      if (event.key === "ArrowRight") next = tabs[(index + 1) % tabs.length];
      else if (event.key === "ArrowLeft") next = tabs[(index - 1 + tabs.length) % tabs.length];
      else if (event.key === "Home") next = tabs[0];
      else if (event.key === "End") next = tabs[tabs.length - 1];
      if (!next) return;
      event.preventDefault();
      select(next, true);
    });
  });
}

document.addEventListener("DOMContentLoaded", () => {
  applyLanguage(getStoredLanguage());
  markRecommendedPlatform();
  setupNavigation();
  setupQQGroupCopy();
  setupMembershipRegions();

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
