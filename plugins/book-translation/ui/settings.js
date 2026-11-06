(function () {
  'use strict';
  const kk = window.kkindle;
  const zh = !(kk.context.language || 'zh-CN').toLowerCase().startsWith('en');
  const text = zh ? {
    title: '书籍翻译与注音',
    windowTitle: '设置',
    lead: '这里只保存翻译和注音窗口的默认选项。AI 服务的地址、模型、密钥和网络开关请在应用设置里配置。',
    provider: '默认翻译服务',
    providerHint: '免费服务不需要 AI 设置。',
    providerAi: 'AI 翻译',
    providerBing: 'Bing 免费翻译',
    providerGoogle: 'Google 免费翻译',
    target: '默认目标语言',
    output: '默认生成结果',
    translated: '单译版',
    bilingual: '双语对照版',
    rpm: 'AI 翻译每分钟请求上限',
    rpmHint: '只对 AI 翻译生效，0 表示不限制。',
    engine: '默认注音引擎',
    engineHint: '选择 g2pW 时，请在「生成注音书」窗口里下载模型。',
    engineLocal: '内置拼音引擎',
    engineG2pw: 'g2pW v2（高精度）',
    style: '默认注音样式',
    styleMarked: '带声调符号（zhōng）',
    styleNumber: '数字声调（zhong1）',
    styleNone: '不加声调（zhong）',
    aiReview: '默认使用 AI 复核多音字',
    aiHint: '只发送本机标记的多音字候选，数量很少。',
    save: '保存',
    close: '关闭',
    saved: '已保存。默认选项会应用到下次打开的翻译和注音窗口。',
    saveFailed: '保存失败：{0}',
    noOutput: '请至少保留一种翻译结果。',
    badRpm: '每分钟请求上限需要是 0–65536 之间的整数。'
  } : {
    title: 'Book translation & pinyin',
    windowTitle: 'Settings',
    lead: 'Defaults for the translation and pinyin windows. Configure the AI endpoint, model, key and network access in the app settings.',
    provider: 'Default translation service',
    providerHint: 'Free services do not need the AI settings.',
    providerAi: 'AI translation',
    providerBing: 'Bing free',
    providerGoogle: 'Google free',
    target: 'Default target language',
    output: 'Default output',
    translated: 'Translated only',
    bilingual: 'Bilingual',
    rpm: 'AI requests per minute',
    rpmHint: 'Applies to AI translation only; 0 means unlimited.',
    engine: 'Default pinyin engine',
    engineHint: 'When using g2pW, download its model from the pinyin window.',
    engineLocal: 'Built-in engine',
    engineG2pw: 'g2pW v2 (accurate)',
    style: 'Default annotation style',
    styleMarked: 'Tone marks (zhōng)',
    styleNumber: 'Tone numbers (zhong1)',
    styleNone: 'No tones (zhong)',
    aiReview: 'Review polyphones with AI by default',
    aiHint: 'Only locally flagged polyphone candidates are sent; there are very few.',
    save: 'Save',
    close: 'Close',
    saved: 'Saved. Defaults apply the next time a translation or pinyin window opens.',
    saveFailed: 'Could not save: {0}',
    noOutput: 'Keep at least one translation output.',
    badRpm: 'Requests per minute must be a whole number between 0 and 65536.'
  };

  const LANGUAGES = [
    ['zh-CN', '中文（简体） / Chinese (Simplified)'], ['zh-TW', '中文（繁体） / Chinese (Traditional)'],
    ['en', 'English'], ['ja', '日本語'], ['ko', '한국어'], ['fr', 'Français'], ['de', 'Deutsch'],
    ['es', 'Español'], ['it', 'Italiano'], ['pt', 'Português'], ['ru', 'Русский'],
    ['ar', 'العربية'], ['th', 'ไทย'], ['vi', 'Tiếng Việt'], ['id', 'Bahasa Indonesia'],
    ['tr', 'Türkçe'], ['pl', 'Polski'], ['nl', 'Nederlands']
  ];

  const dom = {
    title: document.getElementById('title'),
    lead: document.getElementById('lead'),
    providerTitle: document.getElementById('providerTitle'),
    provider: document.getElementById('provider'),
    providerAi: document.getElementById('providerAi'),
    providerBing: document.getElementById('providerBing'),
    providerGoogle: document.getElementById('providerGoogle'),
    providerHint: document.getElementById('providerHint'),
    targetTitle: document.getElementById('targetTitle'),
    target: document.getElementById('target'),
    outputTitle: document.getElementById('outputTitle'),
    translated: document.getElementById('translated'),
    translatedLabel: document.getElementById('translatedLabel'),
    bilingual: document.getElementById('bilingual'),
    bilingualLabel: document.getElementById('bilingualLabel'),
    rpmTitle: document.getElementById('rpmTitle'),
    rpm: document.getElementById('rpm'),
    rpmHint: document.getElementById('rpmHint'),
    engineTitle: document.getElementById('engineTitle'),
    engine: document.getElementById('engine'),
    engineLocal: document.getElementById('engineLocal'),
    engineG2pw: document.getElementById('engineG2pw'),
    engineHint: document.getElementById('engineHint'),
    styleTitle: document.getElementById('styleTitle'),
    style: document.getElementById('style'),
    styleMarked: document.getElementById('styleMarked'),
    styleNumber: document.getElementById('styleNumber'),
    styleNone: document.getElementById('styleNone'),
    aiReviewTitle: document.getElementById('aiReviewTitle'),
    aiReviewHint: document.getElementById('aiReviewHint'),
    aiReview: document.getElementById('aiReview'),
    save: document.getElementById('save'),
    close: document.getElementById('close'),
    status: document.getElementById('status')
  };

  function selectValue(select, value, fallback) {
    const values = Array.from(select.options).map(option => option.value);
    select.value = values.includes(value) ? value : fallback;
  }

  function parseRpm(value) {
    const raw = String(value == null ? '' : value).trim();
    if (raw === '') return 0;
    const number = Number(raw);
    return Number.isInteger(number) && number >= 0 && number <= 65536 ? number : null;
  }

  function showStatus(message, isError) {
    dom.status.textContent = message || '';
    dom.status.classList.toggle('error', Boolean(isError));
  }

  function applyText() {
    dom.title.textContent = text.title;
    document.title = text.title;
    dom.lead.textContent = text.lead;
    dom.providerTitle.textContent = text.provider;
    dom.providerAi.textContent = text.providerAi;
    dom.providerBing.textContent = text.providerBing;
    dom.providerGoogle.textContent = text.providerGoogle;
    dom.providerHint.textContent = text.providerHint;
    dom.targetTitle.textContent = text.target;
    dom.outputTitle.textContent = text.output;
    dom.translatedLabel.textContent = text.translated;
    dom.bilingualLabel.textContent = text.bilingual;
    dom.rpmTitle.textContent = text.rpm;
    dom.rpmHint.textContent = text.rpmHint;
    dom.engineTitle.textContent = text.engine;
    dom.engineLocal.textContent = text.engineLocal;
    dom.engineG2pw.textContent = text.engineG2pw;
    dom.engineHint.textContent = text.engineHint;
    dom.styleTitle.textContent = text.style;
    dom.styleMarked.textContent = text.styleMarked;
    dom.styleNumber.textContent = text.styleNumber;
    dom.styleNone.textContent = text.styleNone;
    dom.aiReviewTitle.textContent = text.aiReview;
    dom.aiReviewHint.textContent = text.aiHint;
    dom.save.textContent = text.save;
    dom.close.textContent = text.close;
    // The host already prefixes the plugin name.
    kk.ui.setTitle(text.windowTitle).catch(() => {});
    dom.target.innerHTML = '';
    LANGUAGES.forEach(([value, label]) => {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = label;
      dom.target.appendChild(option);
    });
  }

  async function load() {
    applyText();
    let stored = {};
    try { stored = (await kk.storage.get('settings')) || {}; } catch (error) { /* defaults */ }
    const translate = Object.assign({
      provider: 'ai', target: 'zh-CN', translated: true, bilingual: false, aiRpm: 0
    }, stored.translate || {});
    const pinyin = Object.assign({
      engine: 'dotnet-g2p', style: 'tone-marked', aiReview: true
    }, stored.pinyin || {});
    selectValue(dom.provider, translate.provider, 'ai');
    selectValue(dom.target, translate.target, 'zh-CN');
    dom.translated.checked = translate.translated !== false;
    dom.bilingual.checked = translate.bilingual === true;
    if (!dom.translated.checked && !dom.bilingual.checked) dom.translated.checked = true;
    const rpm = parseRpm(translate.aiRpm);
    dom.rpm.value = String(rpm == null ? 0 : rpm);
    selectValue(dom.engine, pinyin.engine, 'dotnet-g2p');
    selectValue(dom.style, pinyin.style, 'tone-marked');
    dom.aiReview.checked = pinyin.aiReview !== false;
  }

  async function save() {
    if (!dom.translated.checked && !dom.bilingual.checked) {
      showStatus(text.noOutput, true);
      return;
    }
    const rpm = parseRpm(dom.rpm.value);
    if (rpm == null || dom.rpm.validity.badInput) {
      showStatus(text.badRpm, true);
      dom.rpm.focus();
      return;
    }
    dom.save.disabled = true;
    try {
      let stored = {};
      try { stored = (await kk.storage.get('settings')) || {}; } catch (error) { /* defaults */ }
      if (typeof stored !== 'object' || Array.isArray(stored)) stored = {};
      const settings = Object.assign({}, stored, {
        translate: Object.assign({}, stored.translate || {}, {
          provider: dom.provider.value,
          target: dom.target.value,
          translated: dom.translated.checked,
          bilingual: dom.bilingual.checked,
          aiRpm: rpm
        }),
        pinyin: Object.assign({}, stored.pinyin || {}, {
          engine: dom.engine.value,
          style: dom.style.value,
          aiReview: dom.aiReview.checked
        })
      });
      await kk.storage.set('settings', settings);
      dom.rpm.value = String(rpm);
      showStatus(text.saved);
    } catch (error) {
      showStatus(text.saveFailed.replace('{0}', (error && error.message) || String(error)), true);
    } finally {
      dom.save.disabled = false;
    }
  }

  dom.save.addEventListener('click', () => void save());
  dom.close.addEventListener('click', () => { kk.ui.close().catch(() => {}); });
  load().catch(error => showStatus((error && error.message) || String(error), true));
})();
