(function () {
  'use strict';
  const kk = window.kkindle;
  const zh = !(kk.context.language || 'zh-CN').toLowerCase().startsWith('en');
  const text = zh ? {
    lead: '整本书会按段落翻译，完成后自动把生成的文件加入书库。翻译进度保存在本机，中断后可继续。',
    provider: '翻译服务',
    providerHint: 'AI 使用应用设置里的接口、模型和密钥；免费服务无需配置。',
    providerAi: 'AI 翻译',
    providerBing: 'Bing 免费翻译',
    providerGoogle: 'Google 免费翻译',
    rpm: '每分钟请求数上限',
    rpmHint: '0 表示不限制。请求过快时在线服务可能暂时拒绝。',
    proxy: 'Google 代理地址',
    proxyHint: '仅 Google 免费翻译使用；留空时使用应用设置里的代理地址。',
    source: '原文语言',
    sourceHint: '一般保持自动检测即可。',
    target: '目标语言',
    targetHint: '',
    output: '生成结果（至少选择一项）',
    translatedOnly: '单译版',
    bilingual: '双语对照版',
    outputHint: '已有同名翻译版本会在成功后替换。',
    start: '开始翻译',
    cancel: '取消',
    cancelling: '正在取消…',
    running: '正在翻译，关闭窗口前会先确认，确认后停止任务。',
    done: '翻译完成：生成 {0} 个文件，加入书库 {1} 项，跳过 {2} 项，加入失败 {3} 项。',
    noFiles: '翻译完成，但没有生成新的文件。',
    files: '生成的文件：',
    cancelled: '已取消翻译，已完成的部分已保存，下次可以继续。',
    noBook: '没有选中的书籍。',
    noOutput: '请至少选择一种翻译结果。',
    noEpub: '这本书没有 EPUB 文件，目前只能翻译 EPUB。可以先在书库里把它转换成 EPUB。',
    sameLanguage: '原文语言和目标语言相同，请换一个目标语言。',
    badRpm: '每分钟请求数需要是 0–65536 之间的整数。',
    badProxy: '代理地址无效，请填写 http://127.0.0.1:7890 这样的 HTTP/HTTPS 地址。',
    pageTitle: '翻译书籍',
    prepare: '正在准备…',
    import: '正在加入书库…',
    finished: '完成',
    readBook: '无法读取书籍信息：{0}'
  } : {
    lead: 'The whole book is translated paragraph by paragraph, and the generated files are added to the library. Progress is cached on this computer so an interrupted run can resume.',
    provider: 'Translation service',
    providerHint: 'AI uses the service, model and key from the app settings; free services need no setup.',
    providerAi: 'AI translation',
    providerBing: 'Bing free',
    providerGoogle: 'Google free',
    rpm: 'Requests per minute',
    rpmHint: '0 means unlimited. Online services may throttle faster runs.',
    proxy: 'Google proxy',
    proxyHint: 'Used by Google free translation only; empty means use the app proxy setting.',
    source: 'Source language',
    sourceHint: 'Automatic detection is usually best.',
    target: 'Target language',
    targetHint: '',
    output: 'Output (choose at least one)',
    translatedOnly: 'Translated only',
    bilingual: 'Bilingual',
    outputHint: 'Existing translations of the same kind are replaced after a successful run.',
    start: 'Start translation',
    cancel: 'Cancel',
    cancelling: 'Cancelling…',
    running: 'Translating. Closing this window asks before stopping the task.',
    done: 'Translation finished: {0} file(s), {1} added to the library, {2} skipped, {3} could not be added.',
    noFiles: 'Translation finished, but no new file was generated.',
    files: 'Generated files:',
    cancelled: 'Translation cancelled. Finished parts are saved and can be resumed next time.',
    noBook: 'No book is selected.',
    noOutput: 'Choose at least one output.',
    noEpub: 'This book has no EPUB file. Only EPUB can be translated; convert it to EPUB in the library first.',
    sameLanguage: 'The source and target languages are the same. Choose another target language.',
    badRpm: 'Requests per minute must be a whole number between 0 and 65536.',
    badProxy: 'Invalid proxy address. Use an HTTP/HTTPS address such as http://127.0.0.1:7890.',
    pageTitle: 'Translate book',
    prepare: 'Preparing…',
    import: 'Adding to library…',
    finished: 'Finished',
    readBook: 'Unable to read the book: {0}'
  };

  const LANGUAGES = [
    ['auto', '自动检测 / Auto'], ['zh-CN', '中文（简体） / Chinese (Simplified)'],
    ['zh-TW', '中文（繁体） / Chinese (Traditional)'], ['en', 'English'],
    ['ja', '日本語'], ['ko', '한국어'], ['fr', 'Français'], ['de', 'Deutsch'],
    ['es', 'Español'], ['it', 'Italiano'], ['pt', 'Português'], ['ru', 'Русский'],
    ['ar', 'العربية'], ['th', 'ไทย'], ['vi', 'Tiếng Việt'], ['id', 'Bahasa Indonesia'],
    ['tr', 'Türkçe'], ['pl', 'Polski'], ['nl', 'Nederlands']
  ];

  const dom = {
    title: document.getElementById('title'),
    lead: document.getElementById('lead'),
    providerLabel: document.getElementById('providerLabel'),
    providerHint: document.getElementById('providerHint'),
    provider: document.getElementById('provider'),
    providerAi: document.getElementById('providerAi'),
    providerBing: document.getElementById('providerBing'),
    providerGoogle: document.getElementById('providerGoogle'),
    rpmRow: document.getElementById('rpmRow'),
    rpmLabel: document.getElementById('rpmLabel'),
    rpmHint: document.getElementById('rpmHint'),
    rpm: document.getElementById('rpm'),
    proxyRow: document.getElementById('proxyRow'),
    proxyLabel: document.getElementById('proxyLabel'),
    proxyHint: document.getElementById('proxyHint'),
    proxy: document.getElementById('proxy'),
    sourceLabel: document.getElementById('sourceLabel'),
    sourceHint: document.getElementById('sourceHint'),
    source: document.getElementById('source'),
    targetLabel: document.getElementById('targetLabel'),
    targetHint: document.getElementById('targetHint'),
    target: document.getElementById('target'),
    outputLabel: document.getElementById('outputLabel'),
    outputTranslated: document.getElementById('outputTranslated'),
    outputBilingual: document.getElementById('outputBilingual'),
    outputHint: document.getElementById('outputHint'),
    translated: document.getElementById('translated'),
    bilingual: document.getElementById('bilingual'),
    start: document.getElementById('start'),
    cancel: document.getElementById('cancel'),
    status: document.getElementById('status'),
    progress: document.getElementById('progress'),
    bar: document.getElementById('bar'),
    barFill: document.getElementById('barFill'),
    stage: document.getElementById('stage'),
    counter: document.getElementById('counter'),
    result: document.getElementById('result')
  };

  let book = null;
  let running = false;
  let cancelling = false;
  let defaults = {
    provider: 'ai', source: 'auto', target: 'zh-CN',
    translated: true, bilingual: false, aiRpm: 0, proxy: ''
  };

  function fillSelect(select, values, selected) {
    select.innerHTML = '';
    values.forEach(([value, label]) => {
      const option = document.createElement('option');
      option.value = value;
      option.textContent = label;
      if (value === selected) option.selected = true;
      select.appendChild(option);
    });
  }

  // Stored values may be stale or hand-edited; never leave a select empty,
  // the host rejects an empty target language.
  function selectValue(select, value, fallback) {
    const values = Array.from(select.options).map(option => option.value);
    select.value = values.includes(value) ? value : fallback;
  }

  function parseRpm(value) {
    const text = String(value == null ? '' : value).trim();
    if (text === '') return 0;
    const number = Number(text);
    return Number.isInteger(number) && number >= 0 && number <= 65536 ? number : null;
  }

  function normalizeProxy(value) {
    let address = String(value || '').trim().replace(/^"|"$/g, '');
    if (!address) return '';
    if (!address.includes('://')) address = 'http://' + address;
    try {
      const url = new URL(address);
      if ((url.protocol !== 'http:' && url.protocol !== 'https:') || !url.hostname) return null;
    } catch (error) {
      return null;
    }
    return address;
  }

  function errorText(error) {
    return (error && error.message) || String(error || '');
  }

  function showStatus(message, isError) {
    dom.status.textContent = message || '';
    dom.status.classList.toggle('error', Boolean(isError));
  }

  function showResult(message, isError, files) {
    dom.result.textContent = message;
    if (files && files.length) {
      const list = document.createElement('span');
      list.className = 'files';
      list.textContent = text.files + '\n' + files.join('\n');
      dom.result.appendChild(list);
    }
    dom.result.classList.add('visible');
    dom.result.classList.toggle('error', Boolean(isError));
  }

  function clearResult() {
    dom.result.textContent = '';
    dom.result.classList.remove('visible', 'error');
  }

  function setBar(percent, indeterminate) {
    dom.bar.classList.toggle('indeterminate', Boolean(indeterminate));
    dom.barFill.style.width = indeterminate ? '' : Math.max(0, Math.min(100, percent)) + '%';
  }

  function applyText() {
    dom.lead.textContent = text.lead;
    dom.providerLabel.textContent = text.provider;
    dom.providerHint.textContent = text.providerHint;
    dom.providerAi.textContent = text.providerAi;
    dom.providerBing.textContent = text.providerBing;
    dom.providerGoogle.textContent = text.providerGoogle;
    dom.rpmLabel.textContent = text.rpm;
    dom.rpmHint.textContent = text.rpmHint;
    dom.proxyLabel.textContent = text.proxy;
    dom.proxyHint.textContent = text.proxyHint;
    dom.sourceLabel.textContent = text.source;
    dom.sourceHint.textContent = text.sourceHint;
    dom.targetLabel.textContent = text.target;
    dom.targetHint.textContent = text.targetHint;
    dom.outputLabel.textContent = text.output;
    dom.outputTranslated.textContent = text.translatedOnly;
    dom.outputBilingual.textContent = text.bilingual;
    dom.outputHint.textContent = text.outputHint;
    dom.start.textContent = text.start;
    dom.cancel.textContent = text.cancel;
    dom.stage.textContent = text.prepare;
    dom.title.textContent = text.pageTitle;
    document.title = text.pageTitle;
  }

  function syncProviderRows() {
    const ai = dom.provider.value === 'ai';
    const google = dom.provider.value === 'google';
    dom.rpmRow.style.display = ai ? '' : 'none';
    dom.proxyRow.style.display = google ? '' : 'none';
  }

  function setRunning(value) {
    running = value;
    cancelling = false;
    dom.start.disabled = value || !book;
    dom.cancel.style.display = value ? '' : 'none';
    dom.cancel.disabled = false;
    dom.cancel.textContent = text.cancel;
    dom.provider.disabled = value;
    dom.source.disabled = value;
    dom.target.disabled = value;
    dom.translated.disabled = value;
    dom.bilingual.disabled = value;
    dom.rpm.disabled = value;
    dom.proxy.disabled = value;
    dom.progress.classList.toggle('visible', value);
    if (value) clearResult();
  }

  // Host stage ids; anything else is the engine's own (Chinese) stage text.
  function stageText(p) {
    if (p.stage === 'prepare') return text.prepare;
    if (p.stage === 'import') return text.import;
    if (p.stage === 'done') return text.finished;
    const stage = p.stage || '';
    const message = p.message && p.message !== stage ? p.message : '';
    return stage && message ? stage + ' · ' + message : (stage || message || text.prepare);
  }

  function updateProgress(p) {
    // Progress is posted asynchronously and can arrive after the call has
    // already resolved; ignore it so the final state is not overwritten.
    if (!p || p.task !== 'translation' || !running || cancelling) return;
    const percent = Math.max(0, Math.min(100, Number(p.percent) || 0));
    setBar(percent, p.indeterminate);
    dom.stage.textContent = stageText(p);
    const total = Number(p.total) || 0;
    const processed = Number(p.processed) || 0;
    dom.counter.textContent = total > 0
      ? processed + ' / ' + total + ' (' + Math.round(percent) + '%)'
      : !p.indeterminate && percent > 0 ? Math.round(percent) + '%' : '';
  }

  async function load() {
    applyText();
    fillSelect(dom.source, LANGUAGES, defaults.source);
    fillSelect(dom.target, LANGUAGES.filter(item => item[0] !== 'auto'), defaults.target);
    try {
      const stored = await kk.storage.get('settings');
      if (stored && stored.translate && typeof stored.translate === 'object') {
        defaults = Object.assign(defaults, stored.translate);
      }
    } catch (error) { /* defaults */ }
    selectValue(dom.provider, defaults.provider, 'ai');
    selectValue(dom.source, defaults.source, 'auto');
    selectValue(dom.target, defaults.target, 'zh-CN');
    dom.translated.checked = defaults.translated !== false;
    dom.bilingual.checked = defaults.bilingual === true;
    if (!dom.translated.checked && !dom.bilingual.checked) dom.translated.checked = true;
    const rpm = parseRpm(defaults.aiRpm);
    dom.rpm.value = String(rpm == null ? 0 : rpm);
    dom.proxy.value = typeof defaults.proxy === 'string' ? defaults.proxy : '';
    syncProviderRows();

    try {
      const books = await kk.library.getBooks();
      book = books && books.length ? books[0] : null;
    } catch (error) {
      showResult(text.readBook.replace('{0}', errorText(error)), true);
      return;
    }
    if (!book) {
      showResult(text.noBook, true);
      return;
    }
    if (book.title) dom.title.textContent = book.title;
    kk.ui.setTitle((book.title ? book.title + ' · ' : '') + text.pageTitle).catch(() => {});
    const formats = Array.isArray(book.formats) ? book.formats.map(f => String(f).toUpperCase()) : null;
    if (formats && !formats.includes('EPUB')) {
      book = null;
      showResult(text.noEpub, true);
      return;
    }
    dom.start.disabled = running;
  }

  function validate() {
    if (!dom.translated.checked && !dom.bilingual.checked) return { error: text.noOutput };
    if (dom.source.value !== 'auto' && dom.source.value === dom.target.value) return { error: text.sameLanguage };
    let rpm = 0;
    if (dom.provider.value === 'ai') {
      rpm = parseRpm(dom.rpm.value);
      if (rpm == null || dom.rpm.validity.badInput) return { error: text.badRpm, focus: dom.rpm };
    }
    let proxy = '';
    if (dom.provider.value === 'google') {
      proxy = normalizeProxy(dom.proxy.value);
      if (proxy == null) return { error: text.badProxy, focus: dom.proxy };
    }
    return { rpm, proxy };
  }

  async function start() {
    if (running || !book) return;
    const checked = validate();
    if (checked.error) {
      clearResult();
      showStatus(checked.error, true);
      if (checked.focus) checked.focus.focus();
      return;
    }
    showStatus(text.running);
    setRunning(true);
    dom.stage.textContent = text.prepare;
    dom.counter.textContent = '';
    setBar(0, true);
    let finished = false;
    try {
      const result = await kk.library.translateBook(book.id, {
        provider: dom.provider.value,
        sourceLanguage: dom.source.value,
        targetLanguage: dom.target.value,
        translated: dom.translated.checked,
        bilingual: dom.bilingual.checked,
        aiRequestsPerMinute: checked.rpm,
        googleProxyAddress: checked.proxy,
        resume: true
      });
      if (!result || result.cancelled) {
        showStatus(text.cancelled);
        return;
      }
      showStatus('');
      const outputs = Array.isArray(result.outputs) ? result.outputs.map(String) : [];
      const outputCount = Number(result.outputCount) || 0;
      const failed = Number(result.failed) || 0;
      if (outputCount === 0) {
        showResult(text.noFiles, true);
      } else {
        showResult(text.done
          .replace('{0}', outputCount)
          .replace('{1}', Number(result.added) || 0)
          .replace('{2}', Number(result.skipped) || 0)
          .replace('{3}', failed), failed > 0, outputs);
      }
      finished = true;
    } catch (error) {
      showStatus('');
      showResult(errorText(error), true);
    } finally {
      setRunning(false);
      if (finished) {
        dom.progress.classList.add('visible');
        setBar(100, false);
        dom.stage.textContent = text.finished;
        dom.counter.textContent = '100%';
      }
    }
  }

  async function cancel() {
    if (!running || cancelling) return;
    cancelling = true;
    showStatus(text.cancelling);
    dom.cancel.disabled = true;
    dom.stage.textContent = text.cancelling;
    try { await kk.library.cancelTask(); } catch (error) { /* already finished */ }
  }

  dom.provider.addEventListener('change', syncProviderRows);
  [dom.translated, dom.bilingual, dom.source, dom.target, dom.rpm, dom.proxy, dom.provider].forEach(input =>
    input.addEventListener('change', () => { if (!running && dom.status.classList.contains('error')) showStatus(''); }));
  dom.start.addEventListener('click', () => void start());
  dom.cancel.addEventListener('click', () => void cancel());
  kk.events.on('task.progress', updateProgress);
  load().catch(error => showResult(errorText(error), true));
})();
