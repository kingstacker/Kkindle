/* Page-drawn dropdowns: every select.kk-input gets a menu drawn by the page,
   so the popup looks the same on macOS, Windows and Linux instead of following
   the operating system. The select stays in the DOM as the value store, so page
   code keeps using .value, options and change events unchanged. */
(function () {
  'use strict';

  const CHEVRON = '<svg class="kk-dd-chevron" viewBox="0 0 12 12" fill="none" '
    + 'stroke="currentColor" stroke-width="1.4" stroke-linecap="round" '
    + 'stroke-linejoin="round" aria-hidden="true"><path d="M2.5 4.5 L6 8 L9.5 4.5"/></svg>';

  function enhance(select) {
    if (select.kkDropdownRoot) return;

    const root = document.createElement('div');
    root.className = 'kk-dd';
    select.parentNode.insertBefore(root, select);
    root.appendChild(select);

    const button = document.createElement('button');
    button.type = 'button';
    button.className = 'kk-dd-button';
    button.setAttribute('aria-haspopup', 'listbox');
    button.setAttribute('aria-expanded', 'false');
    const label = document.createElement('span');
    label.className = 'kk-dd-label';
    button.insertAdjacentHTML('beforeend', CHEVRON);
    button.insertBefore(label, button.firstChild);

    const menu = document.createElement('div');
    menu.className = 'kk-dd-menu';
    menu.setAttribute('role', 'listbox');
    menu.hidden = true;

    root.appendChild(button);
    root.appendChild(menu);
    select.kkDropdownRoot = root;
    select.setAttribute('aria-hidden', 'true');
    select.tabIndex = -1;

    function build() {
      menu.textContent = '';
      Array.from(select.options).forEach(option => {
        const item = document.createElement('button');
        item.type = 'button';
        item.className = 'kk-dd-option' + (option.selected ? ' selected' : '');
        item.setAttribute('role', 'option');
        item.setAttribute('aria-selected', String(option.selected));
        item.textContent = option.text;
        item.addEventListener('click', () => {
          close(true);
          select.value = option.value;
          select.dispatchEvent(new Event('change', { bubbles: true }));
          sync();
        });
        menu.appendChild(item);
      });
    }

    function sync() {
      const option = select.options[select.selectedIndex] || null;
      label.textContent = option ? option.text : '';
      button.disabled = select.disabled;
      root.classList.toggle('kk-dd-disabled', select.disabled);
      if (!menu.hidden) build();
    }

    function outside(event) {
      if (root.contains(event.target)) return;
      close();
    }

    function open() {
      if (select.disabled || !menu.hidden) return;
      build();
      menu.hidden = false;
      button.setAttribute('aria-expanded', 'true');
      const selected = menu.querySelector('.kk-dd-option.selected') || menu.firstElementChild;
      if (selected) selected.focus();
      document.addEventListener('mousedown', outside, true);
    }

    function close(focusButton) {
      if (menu.hidden) return;
      menu.hidden = true;
      button.setAttribute('aria-expanded', 'false');
      document.removeEventListener('mousedown', outside, true);
      if (focusButton) button.focus();
    }

    button.addEventListener('click', () => {
      if (menu.hidden) open();
      else close(true);
    });
    button.addEventListener('keydown', event => {
      if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
      event.preventDefault();
      open();
    });
    menu.addEventListener('keydown', event => {
      if (event.key === 'Escape') {
        event.preventDefault();
        close(true);
        return;
      }
      if (event.key === 'Tab') {
        event.preventDefault();
        close(true);
        return;
      }
      if (event.key !== 'ArrowDown' && event.key !== 'ArrowUp') return;
      event.preventDefault();
      const items = Array.from(menu.children);
      const index = items.indexOf(document.activeElement);
      const delta = event.key === 'ArrowDown' ? 1 : -1;
      const next = items[(index + delta + items.length) % items.length];
      if (next) next.focus();
    });
    // Labels use for="id", and page code may change values programmatically.
    select.addEventListener('change', sync);
    select.addEventListener('focus', () => button.focus());
    new MutationObserver(sync).observe(select, {
      childList: true,
      subtree: true,
      characterData: true,
      attributes: true,
      attributeFilter: ['disabled']
    });

    sync();
  }

  function start() {
    document.querySelectorAll('select.kk-input').forEach(enhance);
  }

  if (document.readyState === 'loading') document.addEventListener('DOMContentLoaded', start);
  else start();
})();
