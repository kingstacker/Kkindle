/*
 * kkindle-mock.js — a stand-in for window.kkindle so plugin pages can be
 * developed in an ordinary browser (Live Server, Vite, file://).
 *
 *   <script src="kkindle-mock.js"></script>   <!-- before your own script -->
 *
 * Inside Kkindle the real bridge is already installed and this file does
 * nothing, so it is safe to leave in a package — but `kkplugin pack` users
 * usually load it only in development:
 *
 *   if (!window.kkindle) document.write('<script src="kkindle-mock.js"><\/script>');
 *
 * Customize before loading:
 *   window.KKINDLE_MOCK = {
 *     context: { action: 'translate', language: 'en-US', theme: 'dark', selection: {...} },
 *     permissions: { library: 'write', ai: true, ... },   // defaults: everything allowed
 *     books: [...],                                       // see DEFAULT_BOOKS
 *     aiReply: function (prompt, options) { return 'echo: ' + prompt; },
 *     fetch: true,                                        // use the browser's fetch (CORS applies)
 *     latency: 80                                         // ms per call
 *   };
 *
 * Mock-only helpers live on window.kkindleMock:
 *   kkindleMock.lookup(term)        runs a registered dictionary provider
 *   kkindleMock.translate(text)     runs a registered translator
 *   kkindleMock.runBackground()     runs the background handler
 *   kkindleMock.emit(name, payload) fires an events.on handler
 *   kkindleMock.calls               every bridge call made so far
 */
(function () {
  'use strict';
  if (window.kkindle) return;

  var options = window.KKINDLE_MOCK || {};
  var storagePrefix = 'kkindle-mock:';

  var DEFAULT_BOOKS = [
    {
      id: '11111111-1111-4111-8111-111111111111',
      title: '示例书籍',
      authors: '佚名',
      formats: ['EPUB'],
      chapters: [
        { title: '第一章', text: '天地玄黄，宇宙洪荒。日月盈昃，辰宿列张。' },
        { title: '第二章', text: 'The quick brown fox jumps over the lazy dog.' }
      ]
    },
    {
      id: '22222222-2222-4222-8222-222222222222',
      title: 'Sample Book',
      authors: 'Anonymous',
      formats: ['EPUB', 'PDF'],
      chapters: [{ title: 'Chapter 1', text: 'It was a bright cold day in April.' }]
    }
  ];

  var books = (options.books || DEFAULT_BOOKS).map(function (book, index) {
    var fileId = book.fileId || ('00000000-0000-4000-8000-' + String(index + 1).padStart(12, '0'));
    return Object.assign({
      series: null, seriesIndex: null, description: null, publisher: null, publishDate: null,
      isbn: null, pageCount: null, binding: null, rating: null, ratingCount: null,
      tags: '', category: '', hasCover: false, favorite: false, readingStatus: 'unread',
      collectionIds: [], updatedAt: new Date().toISOString(), formats: ['EPUB'], chapters: []
    }, book, { fileId: fileId });
  });
  var collections = [];
  var annotations = [];
  var bookmarks = [];
  var handlers = {};
  var listeners = {};
  var calls = [];

  var permissions = Object.assign({
    library: 'write', libraryAll: true, network: true, import: true, translation: true,
    pinyin: true, storage: true, secrets: true, content: true, ai: true,
    readingWrite: true, bookFiles: true
  }, options.permissions || {});

  var context = Object.freeze(Object.assign({
    pluginId: 'dev.example.plugin',
    pluginVersion: '0.0.0-dev',
    action: null,
    bookIds: books.slice(0, 1).map(function (book) { return book.id; }),
    language: navigator.language && navigator.language.indexOf('zh') === 0 ? 'zh-CN' : 'en-US',
    appVersion: '99.0.0',
    theme: window.matchMedia && window.matchMedia('(prefers-color-scheme: dark)').matches ? 'dark' : 'light',
    selection: null,
    developerMode: true,
    headless: false
  }, options.context || {}));

  var METHODS = [
    'library.getBooks', 'library.search', 'library.getBook', 'library.getCollections',
    'library.createCollection', 'library.addToCollection', 'library.removeFromCollection',
    'library.importFiles', 'library.updateBook', 'library.getCover', 'library.setCover',
    'library.importDownload', 'library.translateBook', 'library.annotatePinyin', 'library.cancelTask',
    'library.readFile', 'pinyin.status', 'pinyin.downloadModel', 'http.fetch', 'network.requestAccess',
    'secrets.get', 'secrets.set', 'secrets.remove', 'storage.get', 'storage.set', 'storage.remove',
    'ui.close', 'ui.notify', 'ui.setTitle', 'ui.openExternal', 'ui.log', 'ui.pickFiles', 'ui.saveFile',
    'ui.copyText', 'ui.readText', 'reading.getProgress', 'reading.getAnnotations', 'reading.getBookmarks',
    'reading.getStats', 'reading.addAnnotation', 'reading.addBookmark', 'book.getToc',
    'book.getChapterText', 'ai.status', 'ai.complete', 'reader.open'
  ];
  var capabilities = deepFreeze({
    apiVersion: 1, appVersion: context.appVersion, headless: false, methods: METHODS, permissions: permissions
  });

  function deepFreeze(value) {
    if (value && typeof value === 'object') {
      Object.keys(value).forEach(function (key) { deepFreeze(value[key]); });
      Object.freeze(value);
    }
    return value;
  }

  function clone(value) { return value === undefined ? null : JSON.parse(JSON.stringify(value)); }

  function uuid() {
    if (window.crypto && crypto.randomUUID) return crypto.randomUUID();
    return 'xxxxxxxx-xxxx-4xxx-8xxx-xxxxxxxxxxxx'.replace(/x/g, function () {
      return (Math.random() * 16 | 0).toString(16);
    });
  }

  function fail(message) { throw new Error(message); }

  function need(flag, message) { if (!permissions[flag]) fail(message); }

  function needLibrary(write) {
    if (!permissions.library) fail('插件没有读取书库的权限。');
    if (write && permissions.library !== 'write') fail('插件没有修改书库的权限。');
  }

  function findBook(id) {
    id = String(id);
    if (!permissions.libraryAll && context.bookIds.indexOf(id) < 0) fail('插件不能访问这本书。');
    var book = books.filter(function (item) { return item.id === id; })[0];
    return book || fail('书籍不存在或已被删除。');
  }

  function publicBook(book) {
    var copy = clone(book);
    delete copy.chapters;
    delete copy.fileId;
    delete copy.cover;
    copy.hasCover = !!book.cover;
    return copy;
  }

  function chapterPath(index) { return 'OEBPS/Text/ch' + (index + 1) + '.xhtml'; }

  function chapterOf(book, index) {
    var chapter = book.chapters[index];
    return chapter || fail('章节序号超出范围。');
  }

  function storageKey(kind, key) { return storagePrefix + context.pluginId + ':' + kind + ':' + key; }

  var methods = {
    'library.getBooks': function (p) {
      needLibrary(false);
      return (p.ids || context.bookIds).map(function (id) { return publicBook(findBook(id)); });
    },
    'library.search': function (p) {
      need('libraryAll', '插件没有搜索整个书库的权限。');
      var query = (p.query || '').toLowerCase();
      var matches = books.filter(function (book) {
        return (!query || (book.title + ' ' + book.authors).toLowerCase().indexOf(query) >= 0)
          && (!p.format || book.formats.indexOf(String(p.format).toUpperCase()) >= 0)
          && (p.favorite == null || book.favorite === p.favorite)
          && (!p.readingStatus || book.readingStatus === p.readingStatus)
          && (!p.collectionId || book.collectionIds.indexOf(p.collectionId) >= 0);
      });
      var start = p.pageIndex * p.pageSize;
      return {
        books: matches.slice(start, start + p.pageSize).map(publicBook),
        totalCount: matches.length, pageIndex: p.pageIndex, pageSize: p.pageSize
      };
    },
    'library.getBook': function (p) { needLibrary(false); return publicBook(findBook(p.id)); },
    'library.getCollections': function () {
      needLibrary(false);
      return collections.map(function (collection) {
        return {
          id: collection.id, name: collection.name,
          bookCount: books.filter(function (book) { return book.collectionIds.indexOf(collection.id) >= 0; }).length
        };
      });
    },
    'library.createCollection': function (p) {
      needLibrary(true);
      var collection = { id: uuid(), name: p.name };
      collections.push(collection);
      return { id: collection.id, name: collection.name, bookCount: 0 };
    },
    'library.addToCollection': function (p) {
      needLibrary(true);
      var book = findBook(p.bookId);
      if (book.collectionIds.indexOf(p.collectionId) < 0) book.collectionIds.push(p.collectionId);
      emit('library.changed', {});
      return { bookId: book.id, collectionId: p.collectionId, member: true };
    },
    'library.removeFromCollection': function (p) {
      needLibrary(true);
      var book = findBook(p.bookId);
      book.collectionIds = book.collectionIds.filter(function (id) { return id !== p.collectionId; });
      emit('library.changed', {});
      return { bookId: book.id, collectionId: p.collectionId, member: false };
    },
    'library.importFiles': function (p) {
      need('import', '插件没有导入书籍的权限。');
      var items = p.paths.map(function (path) {
        return { sourcePath: path, succeeded: true, message: '（模拟）已导入', bookId: uuid(), added: true };
      });
      return { items: items, successCount: items.length, addedCount: items.length };
    },
    'library.updateBook': function (p) {
      needLibrary(true);
      var book = findBook(p.id);
      Object.keys(p.patch || {}).forEach(function (key) { book[key] = p.patch[key]; });
      book.updatedAt = new Date().toISOString();
      emit('library.changed', {});
      return publicBook(book);
    },
    'library.getCover': function (p) { needLibrary(false); return findBook(p.id).cover || null; },
    'library.setCover': function (p) {
      needLibrary(true);
      var data = String(p.data);
      findBook(p.id).cover = data.indexOf('data:') === 0 ? data : 'data:' + data;
      return null;
    },
    'library.readFile': function (p) {
      need('bookFiles', '插件没有读取书籍文件的权限。');
      var book = findBook(p.bookId);
      var bytes = new TextEncoder().encode(book.chapters.map(function (c) { return c.text; }).join('\n'));
      var chunk = bytes.slice(p.offset, p.offset + p.length);
      var binary = '';
      chunk.forEach(function (byte) { binary += String.fromCharCode(byte); });
      return {
        fileId: book.fileId, name: book.title + '.txt', format: book.formats[0], size: bytes.length,
        offset: p.offset, data: btoa(binary), hasMore: p.offset + chunk.length < bytes.length
      };
    },
    'library.importDownload': function (p) {
      need('import', '插件没有导入书籍的权限。');
      var name = p.fileName || p.url.split('/').pop() || 'download';
      emit('import.progress', { fileName: name, stage: 'download', downloadedBytes: 1, totalBytes: 1, percent: 100 });
      return { fileName: name, imported: true, added: true, message: '（模拟）已导入', bookId: uuid() };
    },
    'library.translateBook': function (p) { need('translation', '插件没有翻译书籍的权限。'); return fakeTask('translation', p.bookId); },
    'library.annotatePinyin': function (p) { need('pinyin', '插件没有注音的权限。'); return fakeTask('pinyin', p.bookId); },
    'library.cancelTask': function () { return { cancelling: false }; },
    'pinyin.status': function () { need('pinyin', '插件没有注音的权限。'); return { g2pwInstalled: false }; },
    'pinyin.downloadModel': function () { need('pinyin', '插件没有注音的权限。'); return { installed: true }; },
    'book.getToc': function (p) {
      need('content', '插件没有读取书籍正文的权限。');
      var book = findBook(p.bookId);
      return {
        bookId: book.id, fileId: book.fileId, format: book.formats[0],
        chapters: book.chapters.map(function (chapter, index) {
          return { index: index, title: chapter.title, path: chapterPath(index), length: chapter.text.length };
        }),
        toc: book.chapters.map(function (chapter, index) {
          return { title: chapter.title, chapterIndex: index, level: 1, fragment: null };
        })
      };
    },
    'book.getChapterText': function (p) {
      need('content', '插件没有读取书籍正文的权限。');
      var chapter = chapterOf(findBook(p.bookId), p.chapterIndex);
      var text = chapter.text.substr(p.offset, p.maxLength);
      return {
        chapterIndex: p.chapterIndex, title: chapter.title, path: chapterPath(p.chapterIndex), text: text,
        offset: p.offset, length: chapter.text.length, hasMore: p.offset + text.length < chapter.text.length
      };
    },
    'reading.getProgress': function (p) {
      needLibrary(false);
      var book = findBook(p.bookId);
      return { bookId: book.id, title: book.title, files: [] };
    },
    'reading.getAnnotations': function (p) {
      needLibrary(false);
      return { bookId: p.bookId, annotations: annotations.filter(function (a) { return a.bookId === p.bookId; }).slice(0, p.limit) };
    },
    'reading.getBookmarks': function (p) {
      needLibrary(false);
      return { bookId: p.bookId, bookmarks: bookmarks.filter(function (b) { return b.bookId === p.bookId; }).slice(0, p.limit) };
    },
    'reading.getStats': function (p) { needLibrary(false); return { bookId: p.bookId, files: [] }; },
    'reading.addAnnotation': function (p) {
      need('readingWrite', '插件没有写入阅读数据的权限。');
      var book = findBook(p.bookId);
      var chapter = chapterOf(book, p.chapterIndex);
      if (!(p.startOffset >= 0 && p.endOffset > p.startOffset)) fail('startOffset 和 endOffset 无效，结束位置必须大于开始位置。');
      if (p.endOffset > chapter.text.length) fail('划线位置超出章节长度。');
      var color = p.color || '#000000';
      if (!/^#[0-9A-Fa-f]{6}$/.test(color)) fail('color 必须是 #RRGGBB 形式。');
      var now = new Date().toISOString();
      var annotation = {
        id: uuid(), bookId: book.id, fileId: book.fileId, chapterPath: chapterPath(p.chapterIndex),
        startOffset: p.startOffset, endOffset: p.endOffset,
        selectedText: chapter.text.slice(p.startOffset, p.endOffset), note: p.note || '',
        color: color.toUpperCase(), underlineStyle: p.style || 'solid', createdAt: now, updatedAt: now
      };
      annotations.push(annotation);
      emit('reading.changed', { kind: 'annotation' });
      return clone(annotation);
    },
    'reading.addBookmark': function (p) {
      need('readingWrite', '插件没有写入阅读数据的权限。');
      var book = findBook(p.bookId);
      var chapter = chapterOf(book, p.chapterIndex);
      var bookmark = {
        id: uuid(), bookId: book.id, fileId: book.fileId, chapterPath: chapterPath(p.chapterIndex),
        chapterIndex: p.chapterIndex, title: p.title || chapter.title,
        quote: p.quote || chapter.text.substr(p.offset, 120).trim(), createdAt: new Date().toISOString()
      };
      bookmarks.push(bookmark);
      emit('reading.changed', { kind: 'bookmark' });
      return clone(bookmark);
    },
    'ai.status': function () {
      need('ai', '插件没有使用 AI 的权限。');
      return { available: true, provider: 'mock', model: 'mock-model', reason: null };
    },
    'ai.complete': function (p) {
      need('ai', '插件没有使用 AI 的权限。');
      var reply = options.aiReply ? options.aiReply(p.prompt, p) : '（模拟 AI 回复）' + p.prompt.slice(0, 200);
      return Promise.resolve(reply).then(function (text) { return { text: String(text) }; });
    },
    'reader.open': function (p) {
      findBook(p.bookId);
      console.info('[kkindle-mock] reader.open', p);
      return null;
    },
    'http.fetch': function (p) {
      need('network', '插件没有网络权限。');
      if (options.fetch === false) fail('kkindle-mock：http.fetch 已关闭（KKINDLE_MOCK.fetch = false）。');
      if (p.url.indexOf('https://') !== 0) fail('只能请求 https:// 地址。');
      return fetch(p.url, { method: p.method, headers: p.headers, body: p.body }).then(function (response) {
        var headers = {};
        response.headers.forEach(function (value, key) { headers[key] = value; });
        var read = p.responseType === 'base64'
          ? response.arrayBuffer().then(function (buffer) {
              var binary = '';
              new Uint8Array(buffer).forEach(function (byte) { binary += String.fromCharCode(byte); });
              return btoa(binary);
            })
          : response.text();
        return read.then(function (body) {
          return {
            status: response.status, ok: response.ok, url: response.url,
            contentType: (response.headers.get('content-type') || '').split(';')[0] || null,
            headers: headers, body: body
          };
        });
      });
    },
    'network.requestAccess': function (p) {
      need('network', '插件没有网络权限。');
      var host = String(p.origin).replace(/^https?:\/\//, '').split('/')[0];
      return { granted: window.confirm('允许插件访问 ' + host + '？'), host: host };
    },
    'secrets.get': function (p) { need('secrets', '插件没有声明密钥权限。'); return localStorage.getItem(storageKey('secret', p.key)); },
    'secrets.set': function (p) {
      need('secrets', '插件没有声明密钥权限。');
      if (p.value == null) localStorage.removeItem(storageKey('secret', p.key));
      else localStorage.setItem(storageKey('secret', p.key), p.value);
      return null;
    },
    'secrets.remove': function (p) { need('secrets', '插件没有声明密钥权限。'); localStorage.removeItem(storageKey('secret', p.key)); return null; },
    'storage.get': function (p) {
      need('storage', '插件没有声明存储权限。');
      var raw = localStorage.getItem(storageKey('data', p.key));
      return raw == null ? null : JSON.parse(raw);
    },
    'storage.set': function (p) {
      need('storage', '插件没有声明存储权限。');
      localStorage.setItem(storageKey('data', p.key), JSON.stringify(p.value));
      return null;
    },
    'storage.remove': function (p) { need('storage', '插件没有声明存储权限。'); localStorage.removeItem(storageKey('data', p.key)); return null; },
    'ui.close': function () { console.info('[kkindle-mock] ui.close'); return null; },
    'ui.notify': function (p) { console.info('[kkindle-mock] notify:', p.message); return null; },
    'ui.setTitle': function (p) { document.title = p.title; return null; },
    'ui.openExternal': function (p) { window.open(p.url, '_blank', 'noopener'); return null; },
    'ui.log': function () { return null; },
    'ui.pickFiles': function (p) {
      return new Promise(function (resolve) {
        var input = document.createElement('input');
        input.type = 'file';
        input.multiple = !!p.multiple;
        if (p.extensions && p.extensions.length) input.accept = p.extensions.join(',');
        input.onchange = function () {
          resolve({ files: Array.prototype.map.call(input.files, function (file) {
            return { name: file.name, path: '/mock/' + file.name };
          }) });
        };
        input.click();
      });
    },
    'ui.saveFile': function (p) {
      var blob = p.base64 != null
        ? new Blob([Uint8Array.from(atob(p.base64), function (c) { return c.charCodeAt(0); })])
        : new Blob([p.text || ''], { type: 'text/plain;charset=utf-8' });
      var link = document.createElement('a');
      link.href = URL.createObjectURL(blob);
      link.download = p.fileName;
      link.click();
      setTimeout(function () { URL.revokeObjectURL(link.href); }, 1000);
      return { name: p.fileName, path: '/mock/' + p.fileName };
    },
    'ui.copyText': function (p) { return navigator.clipboard.writeText(p.text).then(function () { return null; }); },
    'ui.readText': function () {
      return navigator.clipboard.readText().then(function (text) { return { text: text }; }, function () { return null; });
    }
  };

  function fakeTask(task, bookId) {
    [25, 50, 75, 100].forEach(function (percent, index) {
      setTimeout(function () {
        emit('task.progress', {
          task: task, bookId: bookId, stage: 'mock', message: '（模拟）' + percent + '%', percent: percent,
          processed: percent, total: 100, indeterminate: false
        });
      }, (index + 1) * 300);
    });
    return { task: task, bookId: bookId, completed: true };
  }

  function call(method, params) {
    calls.push({ method: method, params: clone(params || {}) });
    return new Promise(function (resolve, reject) {
      setTimeout(function () {
        try {
          var handler = methods[method];
          if (!handler) throw new Error('kkindle-mock 不支持 ' + method);
          Promise.resolve(handler(clone(params || {}))).then(function (value) { resolve(clone(value)); }, reject);
        } catch (error) {
          reject(error instanceof Error ? error : new Error(String(error)));
        }
      }, options.latency == null ? 80 : options.latency);
    });
  }

  function emit(name, payload) {
    (listeners[name] || []).slice().forEach(function (handler) {
      try { handler(clone(payload)); } catch (error) { console.error(error); }
    });
  }

  function on(name, handler) {
    (listeners[name] = listeners[name] || []).push(handler);
    return function () {
      listeners[name] = (listeners[name] || []).filter(function (item) { return item !== handler; });
    };
  }

  function register(kind, id, handler) {
    if (typeof handler !== 'function') throw new TypeError('handler 必须是函数。');
    handlers[kind + ':' + id] = handler;
  }

  function runHandler(key, args) {
    var handler = handlers[key];
    if (!handler) return Promise.reject(new Error('没有注册 ' + key));
    return Promise.resolve().then(function () { return handler.apply(null, args); });
  }

  // Same argument defaults and shapes as the real bridge.
  function opt(value, fallback) { return value == null ? fallback : value; }
  function str(value) { return value == null ? null : String(value); }

  var api = {
    context: context,
    capabilities: capabilities,
    has: function (method) { return METHODS.indexOf(String(method)) >= 0; },
    register: Object.freeze({
      dictionary: function (id, handler) { register('dictionary', id, handler); },
      translator: function (id, handler) { register('translator', id, handler); },
      background: function (handler) { register('background', '', handler); }
    }),
    book: Object.freeze({
      getToc: function (bookId, o) { o = o || {}; return call('book.getToc', { bookId: String(bookId), fileId: str(o.fileId) }); },
      getChapterText: function (bookId, chapterIndex, o) {
        o = o || {};
        return call('book.getChapterText', {
          bookId: String(bookId), chapterIndex: opt(chapterIndex, 0), fileId: str(o.fileId),
          offset: opt(o.offset, 0), maxLength: opt(o.maxLength, 200000)
        });
      }
    }),
    ai: Object.freeze({
      status: function () { return call('ai.status'); },
      complete: function (prompt, o) {
        o = o || {};
        return call('ai.complete', { prompt: String(prompt), system: str(o.system), history: o.history || [] })
          .then(function (result) { return result.text; });
      }
    }),
    reader: Object.freeze({
      open: function (bookId, o) {
        o = o || {};
        return call('reader.open', {
          bookId: String(bookId), fileId: str(o.fileId), chapterIndex: opt(o.chapterIndex, null),
          chapterPath: str(o.chapterPath), offset: opt(o.offset, null)
        });
      }
    }),
    library: Object.freeze({
      getBooks: function (ids) { return call('library.getBooks', { ids: ids || context.bookIds }); },
      search: function (o) {
        o = o || {};
        return call('library.search', {
          query: str(o.query), author: str(o.author), tag: str(o.tag), format: str(o.format),
          category: str(o.category), collectionId: str(o.collectionId), readingStatus: str(o.readingStatus),
          favorite: typeof o.favorite === 'boolean' ? o.favorite : null, sort: str(o.sort),
          pageIndex: opt(o.pageIndex, 0), pageSize: opt(o.pageSize, 50)
        });
      },
      getBook: function (id) { return call('library.getBook', { id: String(id) }); },
      getCollections: function () { return call('library.getCollections'); },
      createCollection: function (name) { return call('library.createCollection', { name: String(name) }); },
      addToCollection: function (bookId, collectionId) {
        return call('library.addToCollection', { bookId: String(bookId), collectionId: String(collectionId) });
      },
      removeFromCollection: function (bookId, collectionId) {
        return call('library.removeFromCollection', { bookId: String(bookId), collectionId: String(collectionId) });
      },
      importFiles: function (paths) { return call('library.importFiles', { paths: paths || [] }); },
      updateBook: function (id, patch) { return call('library.updateBook', { id: id, patch: patch }); },
      getCover: function (id) { return call('library.getCover', { id: id }); },
      setCover: function (id, data) { return call('library.setCover', { id: id, data: data }); },
      readFile: function (bookId, o) {
        o = o || {};
        return call('library.readFile', {
          bookId: String(bookId), fileId: str(o.fileId), offset: opt(o.offset, 0), length: opt(o.length, 4194304)
        });
      },
      importDownload: function (url, o) {
        o = o || {};
        return call('library.importDownload', { url: String(url), fileName: str(o.fileName), headers: o.headers || {} });
      },
      translateBook: function (bookId, o) { return call('library.translateBook', Object.assign({ bookId: String(bookId) }, o || {})); },
      annotatePinyin: function (bookId, o) { return call('library.annotatePinyin', Object.assign({ bookId: String(bookId) }, o || {})); },
      cancelTask: function () { return call('library.cancelTask'); }
    }),
    pinyin: Object.freeze({
      status: function () { return call('pinyin.status'); },
      downloadModel: function () { return call('pinyin.downloadModel'); }
    }),
    reading: Object.freeze({
      getProgress: function (bookId) { return call('reading.getProgress', { bookId: String(bookId) }); },
      getAnnotations: function (bookId, o) {
        return call('reading.getAnnotations', { bookId: String(bookId), limit: opt((o || {}).limit, 500) });
      },
      getBookmarks: function (bookId, o) {
        return call('reading.getBookmarks', { bookId: String(bookId), limit: opt((o || {}).limit, 500) });
      },
      getStats: function (bookId) { return call('reading.getStats', { bookId: String(bookId) }); },
      addAnnotation: function (bookId, a) {
        a = a || {};
        return call('reading.addAnnotation', {
          bookId: String(bookId), fileId: str(a.fileId), chapterIndex: a.chapterIndex,
          startOffset: a.startOffset, endOffset: a.endOffset, note: str(a.note), color: str(a.color), style: str(a.style)
        });
      },
      addBookmark: function (bookId, b) {
        b = b || {};
        return call('reading.addBookmark', {
          bookId: String(bookId), fileId: str(b.fileId), chapterIndex: b.chapterIndex,
          offset: opt(b.offset, 0), title: str(b.title), quote: str(b.quote)
        });
      }
    }),
    http: Object.freeze({
      fetch: function (url, o) {
        o = o || {};
        return call('http.fetch', {
          url: String(url), method: o.method || 'GET', headers: o.headers || {},
          body: str(o.body), responseType: o.responseType || 'text'
        });
      }
    }),
    network: Object.freeze({
      requestAccess: function (origin) { return call('network.requestAccess', { origin: String(origin) }); }
    }),
    secrets: Object.freeze({
      get: function (key) { return call('secrets.get', { key: String(key) }); },
      set: function (key, value) { return call('secrets.set', { key: String(key), value: str(value) }); },
      remove: function (key) { return call('secrets.remove', { key: String(key) }); }
    }),
    events: Object.freeze({ on: on }),
    storage: Object.freeze({
      get: function (key) { return call('storage.get', { key: key }); },
      set: function (key, value) { return call('storage.set', { key: key, value: value === undefined ? null : value }); },
      remove: function (key) { return call('storage.remove', { key: key }); }
    }),
    ui: Object.freeze({
      close: function () { return call('ui.close'); },
      notify: function (message) { return call('ui.notify', { message: String(message) }); },
      setTitle: function (title) { return call('ui.setTitle', { title: String(title) }); },
      openExternal: function (url) { return call('ui.openExternal', { url: String(url) }); },
      pickFiles: function (o) {
        o = o || {};
        return call('ui.pickFiles', { multiple: o.multiple === true, extensions: o.extensions || [] });
      },
      saveFile: function (fileName, content) {
        content = content || {};
        return call('ui.saveFile', { fileName: String(fileName), text: str(content.text), base64: str(content.base64) });
      },
      copyText: function (text) { return call('ui.copyText', { text: String(text) }); },
      readText: function () { return call('ui.readText'); }
    })
  };

  Object.defineProperty(window, 'kkindle', { value: Object.freeze(api), writable: false, configurable: false });

  // Theme variables the real host injects, so pages look roughly right.
  var dark = context.theme === 'dark';
  var style = document.createElement('style');
  style.id = 'kkindle-theme';
  var ink = dark ? '#E8E6E1' : '#1F1F1F';
  var paper = dark ? '#1E1F22' : '#FBFAF7';
  style.textContent = ':root{color-scheme:' + context.theme
    + ';--kk-paper:' + paper + ';--kk-ink:' + ink
    + ';--kk-muted:' + (dark ? '#9A9893' : '#6B6965')
    + ';--kk-hairline:' + (dark ? '#3A3B3F' : '#E2DFD8')
    + ';--kk-pressed:' + (dark ? '#2C2D31' : '#ECE9E2')
    + ';--kk-hover:' + (dark ? '#26272A' : '#F3F1EC')
    + ';--kk-strong-border:' + (dark ? '#9A9893' : '#6B6965')
    + ';--kk-accent:' + ink + ';--kk-accent-hover:' + ink + ';--kk-accent-pressed:' + ink
    + ';--kk-on-accent:' + paper + ';--kk-danger:' + (dark ? '#EAA59B' : '#A7342F')
    + ';--kk-radius:0px;--kk-font:\"Songti SC\",\"Noto Serif CJK SC\",serif;--kk-ui-font:var(--kk-font)}'
    + 'html,body{margin:0;padding:0;background:var(--kk-paper);color:var(--kk-ink);font:14px/1.55 var(--kk-font)}';
  (document.head || document.documentElement).appendChild(style);

  window.kkindleMock = Object.freeze({
    books: books,
    calls: calls,
    emit: emit,
    lookup: function (term, id) {
      return runHandler('dictionary:' + (id || firstId('dictionary')), [String(term), { language: context.language }]);
    },
    translate: function (text, id, target) {
      return runHandler('translator:' + (id || firstId('translator')),
        [String(text), { target: target || context.language, language: context.language }]);
    },
    runBackground: function (reason) {
      return runHandler('background:', [{ reason: reason || 'startup', lastRun: null, lastSucceeded: null }]);
    }
  });

  function firstId(kind) {
    var key = Object.keys(handlers).filter(function (name) { return name.indexOf(kind + ':') === 0; })[0];
    return key ? key.slice(kind.length + 1) : '';
  }

  console.info('[kkindle-mock] window.kkindle 已模拟，插件 ID ' + context.pluginId);
})();
