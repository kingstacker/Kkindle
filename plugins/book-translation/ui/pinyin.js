(function () {
  'use strict';
  const kk = window.kkindle;
  const zh = !(kk.context.language || 'zh-CN').toLowerCase().startsWith('en');
  const text = zh ? {
    lead: '为整本书的中文正文生成拼音注音，生成的文件会自动加入书库；重新生成会替换已有的注音版。注音进度保存在本机，中断后可继续。',
    engine: '注音引擎',
    engineHint: '内置引擎无需下载；g2pW 对多音字更准确，需要先下载模型。',
    engineLocal: '内置拼音引擎',
    engineG2pw: 'g2pW v2（高精度）',
    model: 'g2pW 模型',
    modelInstalled: '已安装，可以直接使用。',
    modelMissing: '尚未安装，约 590 MB。',
    modelChecking: '正在检查模型状态…',
    modelDownload: '下载模型',
    modelRedownload: '重新下载',
    modelDownloading: '正在下载模型，关闭窗口前会先确认，确认后停止下载。',
    modelReady: '模型已安装。',
    modelCancel: '取消下载',
    modelCancelled: '已取消模型下载。',
    modelCheckFailed: '无法检查模型状态：{0}',
    modelFailed: '模型下载失败：{0}',
    style: '注音样式',
    styleHint: '声调符号更接近纸质注音读物。',
    styleMarked: '带声调符号（zhōng）',
    styleNumber: '数字声调（zhong1）',
    styleNone: '不加声调（zhong）',
    ai: '使用 AI 复核多音字',
    aiHint: '只把本机标记为多音字候选的位置发给应用设置里的 AI，数量很少；关闭后完全本地生成。',
    start: '生成注音书',
    cancel: '取消',
    cancelling: '正在取消…',
    running: '正在生成注音书，关闭窗口前会先确认，确认后停止任务。',
    done: '注音书已生成并加入书库：处理 {0} 页，标注 {1} 字。',
    aiReviewed: 'AI 复核了 {0} / {1} 处多音字。',
    aiSkipped: '应用里的 AI 未配置或已关闭（或网络已关闭），本次只使用本地引擎注音。',
    cancelled: '已取消注音，已完成的部分已保存，下次可以继续。',
    noBook: '没有选中的书籍。',
    pageTitle: '生成注音书',
    prepare: '正在准备…',
    import: '正在加入书库…',
    finished: '完成',
    readBook: '无法读取书籍信息：{0}'
  } : {
    lead: 'Adds pinyin ruby text to the Chinese body text of the whole book. The generated book is added to the library; regenerating replaces the existing pinyin edition. Progress is cached so an interrupted run can resume.',
    engine: 'Pinyin engine',
    engineHint: 'The built-in engine needs no download; g2pW is more accurate for polyphonic characters but needs its model.',
    engineLocal: 'Built-in engine',
    engineG2pw: 'g2pW v2 (accurate)',
    model: 'g2pW model',
    modelInstalled: 'Installed and ready.',
    modelMissing: 'Not installed, about 590 MB.',
    modelChecking: 'Checking the model…',
    modelDownload: 'Download model',
    modelRedownload: 'Download again',
    modelDownloading: 'Downloading the model. Closing this window asks before stopping the download.',
    modelReady: 'The model is installed.',
    modelCancel: 'Cancel download',
    modelCancelled: 'Model download cancelled.',
    modelCheckFailed: 'Unable to check the model: {0}',
    modelFailed: 'Model download failed: {0}',
    style: 'Annotation style',
    styleHint: 'Tone marks look closest to printed pinyin books.',
    styleMarked: 'Tone marks (zhōng)',
    styleNumber: 'Tone numbers (zhong1)',
    styleNone: 'No tones (zhong)',
    ai: 'Review polyphones with AI',
    aiHint: 'Only locally flagged polyphone candidates are sent to the AI configured in the app; turn off for fully local generation.',
    start: 'Generate pinyin book',
    cancel: 'Cancel',
    cancelling: 'Cancelling…',
    running: 'Generating the pinyin book. Closing this window asks before stopping the task.',
    done: 'Pinyin book generated and added to the library: {0} page(s), {1} character(s) annotated.',
    aiReviewed: 'AI reviewed {0} of {1} polyphone candidate(s).',
    aiSkipped: 'AI is not configured or is turned off (or network access is off) in the app, so only the local engine was used.',
    cancelled: 'Annotation cancelled. Finished parts are saved and can be resumed next time.',
    noBook: 'No book is selected.',
    pageTitle: 'Pinyin book',
    prepare: 'Preparing…',
    import: 'Adding to library…',
    finished: 'Finished',
    readBook: 'Unable to read the book: {0}'
  };

  const dom = {
    title: document.getElementById('title'),
    lead: document.getElementById('lead'),
    engineLabel: document.getElementById('engineLabel'),
    engineHint: document.getElementById('engineHint'),
    engine: document.getElementById('engine'),
    engineLocal: document.getElementById('engineLocal'),
    engineG2pw: document.getElementById('engineG2pw'),
    modelRow: document.getElementById('modelRow'),
    modelLabel: document.getElementById('modelLabel'),
    modelStatus: document.getElementById('modelStatus'),
    download: document.getElementById('download'),
    styleLabel: document.getElementById('styleLabel'),
    styleHint: document.getElementById('styleHint'),
    styleMarked: document.getElementById('styleMarked'),
    styleNumber: document.getElementById('styleNumber'),
    styleNone: document.getElementById('styleNone'),
    style: document.getElementById('style'),
    aiLabel: document.getElementById('aiLabel'),
    aiHint: document.getElementById('aiHint'),
    aiReview: document.getElementById('aiReview'),
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
  let downloading = false;
  let g2pwInstalled = false;
  let modelError = '';
  let modelChecked = false;
  let defaults = { engine: 'dotnet-g2p', style: 'tone-marked', aiReview: true };

  function selectValue(select, value, fallback) {
    const values = Array.from(select.options).map(option => option.value);
    select.value = values.includes(value) ? value : fallback;
  }

  function errorText(error) {
    return (error && error.message) || String(error || '');
  }

  function showStatus(message, isError) {
    dom.status.textContent = message || '';
    dom.status.classList.toggle('error', Boolean(isError));
  }

  function showResult(message, isError) {
    dom.result.textContent = message;
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
    dom.engineLabel.textContent = text.engine;
    dom.engineHint.textContent = text.engineHint;
    dom.engineLocal.textContent = text.engineLocal;
    dom.engineG2pw.textContent = text.engineG2pw;
    dom.modelLabel.textContent = text.model;
    dom.styleLabel.textContent = text.style;
    dom.styleHint.textContent = text.styleHint;
    dom.styleMarked.textContent = text.styleMarked;
    dom.styleNumber.textContent = text.styleNumber;
    dom.styleNone.textContent = text.styleNone;
    dom.aiLabel.textContent = text.ai;
    dom.aiHint.textContent = text.aiHint;
    dom.start.textContent = text.start;
    dom.cancel.textContent = text.cancel;
    dom.stage.textContent = text.prepare;
    dom.download.textContent = text.modelDownload;
    dom.title.textContent = text.pageTitle;
    document.title = text.pageTitle;
  }

  // Single place that derives every control's enabled state, so switching
  // engines, finishing a download or finishing a run cannot leave the start
  // button stuck disabled (or enabled without a book / model).
  function syncControls() {
    const selectedG2pw = dom.engine.value === 'g2pw';
    const busy = running || downloading;
    dom.modelRow.style.display = selectedG2pw || downloading ? '' : 'none';
    if (downloading) {
      dom.modelStatus.textContent = text.modelDownloading;
      dom.download.textContent = text.modelCancel;
      dom.download.disabled = cancelling;
    } else {
      dom.modelStatus.textContent = !modelChecked ? text.modelChecking
        : modelError || (g2pwInstalled ? text.modelInstalled : text.modelMissing);
      dom.download.textContent = g2pwInstalled ? text.modelRedownload : text.modelDownload;
      dom.download.disabled = running;
    }
    dom.engine.disabled = busy;
    dom.style.disabled = busy;
    dom.aiReview.disabled = busy;
    dom.start.disabled = busy || !book || (selectedG2pw && !g2pwInstalled);
    dom.cancel.style.display = running ? '' : 'none';
    dom.cancel.disabled = running && cancelling;
  }

  function stageText(p) {
    if (p.stage === 'prepare') return text.prepare;
    if (p.stage === 'import') return text.import;
    if (p.stage === 'done') return text.finished;
    const stage = p.stage === 'download' ? '' : (p.stage || '');
    const message = p.message && p.message !== stage ? p.message : '';
    return stage && message ? stage + ' · ' + message : (stage || message || text.prepare);
  }

  function updateProgress(p) {
    if (!p || cancelling) return;
    // Late events (posted after the call already resolved) are ignored.
    if (p.task === 'pinyin' ? !running : p.task === 'pinyin-model' ? !downloading : true) return;
    const percent = Math.max(0, Math.min(100, Number(p.percent) || 0));
    setBar(percent, p.indeterminate);
    dom.stage.textContent = stageText(p);
    const total = Number(p.total) || 0;
    const processed = Number(p.processed) || 0;
    dom.counter.textContent = total > 0
      ? processed + ' / ' + total + ' (' + Math.round(percent) + '%)'
      : !p.indeterminate && percent > 0 ? Math.round(percent) + '%' : '';
  }

  async function refreshModelStatus() {
    modelError = '';
    try {
      const status = await kk.pinyin.status();
      g2pwInstalled = Boolean(status && status.g2pwInstalled);
    } catch (error) {
      g2pwInstalled = false;
      modelError = text.modelCheckFailed.replace('{0}', errorText(error));
    }
    modelChecked = true;
    syncControls();
  }

  async function load() {
    applyText();
    try {
      const stored = await kk.storage.get('settings');
      if (stored && stored.pinyin && typeof stored.pinyin === 'object') {
        defaults = Object.assign(defaults, stored.pinyin);
      }
    } catch (error) { /* defaults */ }
    selectValue(dom.engine, defaults.engine, 'dotnet-g2p');
    selectValue(dom.style, defaults.style, 'tone-marked');
    dom.aiReview.checked = defaults.aiReview !== false;
    syncControls();

    try {
      const books = await kk.library.getBooks();
      book = books && books.length ? books[0] : null;
    } catch (error) {
      showResult(text.readBook.replace('{0}', errorText(error)), true);
    }
    if (!book && !dom.result.classList.contains('visible')) showResult(text.noBook, true);
    if (book) {
      if (book.title) dom.title.textContent = book.title;
      kk.ui.setTitle((book.title ? book.title + ' · ' : '') + text.pageTitle).catch(() => {});
    }
    await refreshModelStatus();
  }

  async function downloadModel() {
    if (running) return;
    if (downloading) {
      if (cancelling) return;
      cancelling = true;
      syncControls();
      dom.stage.textContent = text.cancelling;
      try { await kk.library.cancelTask(); } catch (error) { /* finished */ }
      return;
    }
    downloading = true;
    cancelling = false;
    showStatus('');
    clearResult();
    syncControls();
    dom.progress.classList.add('visible');
    setBar(0, true);
    dom.stage.textContent = text.modelDownloading;
    dom.counter.textContent = '';
    let installed = false;
    try {
      const result = await kk.pinyin.downloadModel();
      if (!result || result.cancelled) {
        showStatus(text.modelCancelled);
      } else {
        installed = true;
      }
    } catch (error) {
      showResult(text.modelFailed.replace('{0}', errorText(error)), true);
    } finally {
      downloading = false;
      cancelling = false;
      if (installed) {
        setBar(100, false);
        dom.stage.textContent = text.modelReady;
        dom.counter.textContent = '100%';
        showStatus(text.modelReady);
      } else {
        dom.progress.classList.remove('visible');
      }
      await refreshModelStatus();
    }
  }

  async function start() {
    if (running || downloading || !book) return;
    if (dom.engine.value === 'g2pw' && !g2pwInstalled) return;
    showStatus(text.running);
    clearResult();
    running = true;
    cancelling = false;
    syncControls();
    dom.progress.classList.add('visible');
    dom.stage.textContent = text.prepare;
    dom.counter.textContent = '';
    setBar(0, true);
    const aiRequested = dom.aiReview.checked;
    let finished = false;
    try {
      const result = await kk.library.annotatePinyin(book.id, {
        engine: dom.engine.value,
        outputStyle: dom.style.value,
        aiReview: aiRequested,
        resume: true
      });
      if (!result || result.cancelled) {
        showStatus(text.cancelled);
        return;
      }
      showStatus('');
      const lines = [text.done
        .replace('{0}', Number(result.scannedPages) || 0)
        .replace('{1}', Number(result.characters) || 0)];
      if (aiRequested && result.aiReview === false) {
        lines.push(text.aiSkipped);
      } else if (result.aiReview && Number(result.reviewCandidates) > 0) {
        lines.push(text.aiReviewed
          .replace('{0}', Number(result.aiReviewed) || 0)
          .replace('{1}', Number(result.reviewCandidates) || 0));
      }
      showResult(lines.join('\n'), false);
      finished = true;
    } catch (error) {
      showStatus('');
      showResult(errorText(error), true);
    } finally {
      running = false;
      cancelling = false;
      syncControls();
      if (finished) {
        setBar(100, false);
        dom.stage.textContent = text.finished;
        dom.counter.textContent = '100%';
      } else {
        dom.progress.classList.remove('visible');
      }
    }
  }

  async function cancel() {
    if (!running || cancelling) return;
    cancelling = true;
    syncControls();
    showStatus(text.cancelling);
    dom.stage.textContent = text.cancelling;
    try { await kk.library.cancelTask(); } catch (error) { /* finished */ }
  }

  dom.engine.addEventListener('change', syncControls);
  dom.download.addEventListener('click', () => void downloadModel());
  dom.start.addEventListener('click', () => void start());
  dom.cancel.addEventListener('click', () => void cancel());
  kk.events.on('task.progress', updateProgress);
  load().catch(error => showResult(errorText(error), true));
})();
