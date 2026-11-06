/* Minimal example: read the books this window was opened for and show a few
   fields. Everything else a plugin can do is documented in docs/plugins.md. */
(async function () {
  'use strict';

  const list = document.getElementById('list');
  const FIELDS = [
    ['作者', book => book.authors],
    ['丛书', book => [book.series, book.seriesIndex].filter(Boolean).join(' · ')],
    ['出版社', book => book.publisher],
    ['出版日期', book => book.publishDate],
    ['ISBN', book => book.isbn],
    ['页数', book => book.pageCount],
    ['标签', book => Array.isArray(book.tags) ? book.tags.join('、') : ''],
    ['格式', book => Array.isArray(book.formats) ? book.formats.join('、') : '']
  ];

  function renderBook(book) {
    const card = document.createElement('section');
    card.className = 'book';
    const title = document.createElement('h2');
    title.textContent = book.title || '未命名书籍';
    card.appendChild(title);

    const facts = document.createElement('dl');
    FIELDS.forEach(([name, read]) => {
      let value;
      try { value = read(book); } catch { value = ''; }
      if (value === undefined || value === null || String(value).trim() === '') return;
      const term = document.createElement('dt');
      term.textContent = name;
      const detail = document.createElement('dd');
      detail.textContent = String(value);
      facts.appendChild(term);
      facts.appendChild(detail);
    });
    if (facts.children.length > 0) card.appendChild(facts);
    return card;
  }

  try {
    const books = await window.kkindle.library.getBooks();
    if (!books.length) {
      const empty = document.createElement('p');
      empty.className = 'empty';
      empty.textContent = '没有拿到已打开的书籍。';
      list.appendChild(empty);
      return;
    }
    window.kkindle.ui.setTitle(books.length === 1 ? books[0].title : `书籍信息（${books.length} 本）`);
    books.forEach(book => list.appendChild(renderBook(book)));
  } catch (error) {
    const failure = document.createElement('p');
    failure.className = 'error';
    failure.textContent = '读取失败：' + (error && error.message ? error.message : error);
    list.appendChild(failure);
  }
})();
