using System.Text.Json;
using Kkindle.Core;

namespace Kkindle;

/// <summary>
/// Official Send to Kindle DOM adapter. Selectors verified against Amazon's
/// KindleDocsWebServiceBuzzAssets-home on 2026-09-14. No private HTTP endpoints,
/// cookies, credential fields, or document contents cross this bridge.
/// </summary>
internal static class KindleWebPageScripts
{
    private const string Helpers = """
        const visible = el => !!el && el.getClientRects().length > 0
            && getComputedStyle(el).visibility !== 'hidden';
        const signedOut = () => visible(document.querySelector('#s2k-dnd-sign-in-button'))
            || visible(document.querySelector('#s2k-dnd-sign-in-modal-content'));
        const uploadPage = location.protocol === 'https:' && location.host === 'www.amazon.com'
            && /^\/sendtokindle\/?$/i.test(location.pathname);
        const pageKind = () => /\/ap\//i.test(location.pathname) || signedOut() ? 'signin'
            : uploadPage && document.querySelector('.s2k-dnd-box')
                && document.querySelector('#s2k-r2s-send-button') ? 'ready' : 'unknown';
        const readyNames = () => Array.from(document.querySelectorAll(
            '#s2k-r2s-file-list .s2k-r2s-file-item .s2k-r2s-book-name'), el => el.getAttribute('title') || el.textContent);
        const hasError = () => Array.from(document.querySelectorAll(
            '#s2k-dnd-upload-error-modal-content, #s2k-dnd-invalid-files-zone, #s2k-r2s-file-list .s2k-dnd-file-invalidated-item'))
            .some(el => visible(el) && el.textContent.trim().length > 0);
        const clean = value => (value || '').replace(/\s+/g, ' ').trim();
        const accountName = () => {
            const selectors = '#nav-link-accountList, #nav-link-accountList-nav-line-1, '
                + '[data-testid*="account" i], [data-account-email], [data-account], '
                + '[aria-label*="account" i][data-email], [aria-label*="account" i]';
            const candidates = Array.from(document.querySelectorAll(selectors)).filter(visible);
            const explicit = candidates.flatMap(el => [el.getAttribute('data-email'),
                el.getAttribute('data-account-email'), el.getAttribute('data-account')].map(clean).filter(Boolean));
            const values = candidates.flatMap(el => [el.getAttribute('aria-label'), el.textContent]
                .map(clean).filter(Boolean));
            const emailValues = explicit.concat(values);
            const email = emailValues.map(value => value.match(/[A-Z0-9._%+-]+@[A-Z0-9.-]+\.[A-Z]{2,}/i)?.[0])
                .find(Boolean);
            if (email) return email;
            for (const value of values) {
                const greeting = value.match(/(?:hello|hi|您好|你好)\s*[,，]?\s*(.+?)(?:\s+(?:account\s*&\s*lists|账户与列表))?$/i);
                if (greeting?.[1]) {
                    const name = clean(greeting[1]).replace(/\s+(?:account\s*&\s*lists|账户与列表).*$/i, '');
                    if (name) return name;
                }
            }
            const explicitName = explicit.find(value => value.length <= 120
                && !/^(account|accounts|账号|amazon)$/i.test(value));
            if (explicitName) return explicitName;
            return '';
        };
        const recentHeading = () => {
            const amazonHeading = document.querySelector('#s2k-dnd-rsf-heading');
            if (amazonHeading) return amazonHeading;
            const pattern = /recently sent(?: files)?|最近发送的文件|最近发送/i;
            const candidates = Array.from(document.querySelectorAll(
                'h1, h2, h3, h4, h5, [role="heading"], [data-testid], section, table'));
            return candidates.find(el => visible(el) && clean(el.textContent).length < 120
                && pattern.test(clean(el.textContent)))
                || Array.from(document.querySelectorAll('div')).find(el => visible(el)
                    && clean(el.textContent).length < 100 && pattern.test(clean(el.textContent)));
        };
        const recentRoot = () => {
            // This is Amazon's actual recent-send component. It is collapsed
            // by default, so neither the root nor its rows are necessarily
            // visible even though the component has already fetched data.
            const amazonRoot = document.querySelector('#s2k-dnd-rsf-section-container');
            if (amazonRoot) return amazonRoot;
            const selectors = [
                '#s2k-r2s-recent-files', '#s2k-r2s-recent-file-list',
                '#s2k-recent-files', '#s2k-recent-file-list',
                '[data-testid="recently-sent-files"]', '[data-testid="recent-files"]',
                '[aria-label*="Recently sent files" i]', '[aria-label*="最近发送" i]'
            ];
            const hasGridRows = node => Array.from(node.querySelectorAll('div, li')).some(row => {
                const children = Array.from(row.children || []);
                return children.length >= 3 && children.length <= 6
                    && children.every(child => clean(child.textContent).length <= 300);
            });
            const known = selectors.map(selector => document.querySelector(selector))
                .find(el => el && (el.querySelector('table, [role="table"], [role="row"], ul, ol, li')
                    || hasGridRows(el)));
            if (known) return known;
            const heading = recentHeading();
            let node = heading;
            for (let depth = 0; node && depth < 7; depth++, node = node.parentElement) {
                if (node.querySelector('table, [role="table"], [role="row"], ul, ol, li')) return node;
                if (hasGridRows(node)) return node;
                if (depth > 1 && clean(node.textContent).length > 1800) break;
            }
            return heading?.parentElement || null;
        };
        const recentCells = row => {
            const direct = Array.from(row.children || []).filter(el =>
                /^(TH|TD)$/i.test(el.tagName)
                || /^(cell|gridcell)$/i.test(el.getAttribute('role') || '')
                || el.classList.contains('s2k-rsf-cell'));
            if (direct.length > 0) return direct;
            const nested = Array.from(row.querySelectorAll(
                'th, td, [role="cell"], [role="gridcell"]')).filter(el =>
                el === row || el.closest('[role="row"], tr') === row);
            if (nested.length > 0) return nested;
            return Array.from(row.children || []).filter(el => clean(el.textContent).length > 0);
        };
        const recentCellText = cell => {
            const rendered = cell.querySelector('.s2k-dnd-rsf-text')?.textContent;
            const named = cell.getAttribute('data-title') || cell.getAttribute('title');
            const link = cell.querySelector('a');
            const linkText = link?.getAttribute('title') || link?.textContent;
            const labelled = cell.getAttribute('aria-label')
                || cell.querySelector('[aria-label]')?.getAttribute('aria-label');
            // Prefer the visible cell text. Amazon's file cell often contains a
            // labelled document icon next to the filename; choosing that label
            // first would return only "document" and lose the actual title.
            return clean(rendered || cell.innerText || cell.textContent)
                || clean(named || linkText || labelled);
        };
        const recentRows = root => {
            const amazonRows = root.matches?.('.s2k-rsf-data')
                ? [root] : Array.from(root.querySelectorAll('.s2k-rsf-data'));
            if (amazonRows.length > 0) return amazonRows;
            const table = root.matches?.('table, [role="table"]')
                ? root : root.querySelector('table, [role="table"]');
            if (table) {
                const tableRows = Array.from(table.querySelectorAll('tr'));
                if (tableRows.length > 0) return tableRows.filter(visible);
                return Array.from(table.querySelectorAll('[role="row"]')).filter(visible);
            }
            const list = root.matches?.('ul, ol, [role="list"]')
                ? root : root.querySelector('ul, ol, [role="list"]');
            if (list) {
                const listRows = Array.from(list.querySelectorAll(':scope > li'));
                if (listRows.length > 0) return listRows.filter(visible);
                return Array.from(list.querySelectorAll('li, [role="listitem"]')).filter(visible);
            }
            const markedRows = Array.from(root.querySelectorAll(
                '[role="row"], [data-testid*="recent" i], [class*="recent" i][class*="row" i]'))
                .filter(visible);
            if (markedRows.length > 0) return markedRows;
            // Some Amazon builds render this table as a CSS grid without
            // semantic row/cell attributes. In that case, a row is the small
            // three- or four-column element whose children are all leaf-ish cells.
            return Array.from(root.querySelectorAll('div, li')).filter(row => {
                if (!visible(row)) return false;
                const children = Array.from(row.children || []);
                if (children.length < 3 || children.length > 6
                    || children.some(child => child.children.length >= 4)) return false;
                return children.every(child => clean(child.textContent).length <= 300);
            });
        };
        const recentFiles = () => {
            const root = recentRoot();
            if (!root) return [];
            const amazonData = root.matches?.('#s2k-dnd-rsf-section-container')
                ? root.querySelector('.s2k-rsf-data')
                : null;
            if (amazonData) {
                // Amazon renders the desktop table as one flat .s2k-rsf-data
                // container: four consecutive cells make one file row
                // (send time, title, source, status). Some responsive builds
                // omit the source cell and use three cells per row.
                const cells = Array.from(amazonData.children || [])
                    .filter(cell => cell.classList.contains('s2k-rsf-cell'));
                const rowWidth = cells.some(cell => cell.classList.contains('s2k-rsf-from')) ? 4 : 3;
                return Array.from({ length: Math.floor(cells.length / rowWidth) }, (_, index) => {
                    const values = cells.slice(index * rowWidth, (index + 1) * rowWidth)
                        .map(recentCellText);
                    const sent = values[0] || '';
                    const title = values[1] || '';
                    const from = rowWidth === 4 ? values[2] || '' : '';
                    const status = values[rowWidth - 1] || '';
                    if (!title || !status || /18 most recently sent files|最近发送的文件/.test(title)) return null;
                    return { sent, title, from, status };
                }).filter(Boolean).slice(0, 20);
            }
            const rows = recentRows(root);
            if (!rows.length) return [];
            const rowValues = row => recentCells(row).map(recentCellText);
            const headerIndex = rows.findIndex(row => {
                const values = rowValues(row).join(' ').toLowerCase();
                return /sent|发送/.test(values) && /title|标题/.test(values)
                    && /status|状态/.test(values);
            });
            const header = headerIndex >= 0 ? rowValues(rows[headerIndex]) : [];
            const findColumn = pattern => header.findIndex(value => pattern.test(value));
            const sentIndex = findColumn(/sent|发送/i);
            const titleIndex = findColumn(/title|标题/i);
            const fromIndex = findColumn(/from|来源/i);
            const statusIndex = findColumn(/status|状态/i);
            return rows.slice(headerIndex + 1).map(rowValues).map(values => {
                if (values.length < 3) return null;
                const sent = values[sentIndex >= 0 ? sentIndex : 0] || '';
                const title = values[titleIndex >= 0 ? titleIndex : 1] || '';
                const from = fromIndex >= 0 ? values[fromIndex] || '' : '';
                const statusFallback = values.length >= 4 ? 3 : 2;
                const status = values[statusIndex >= 0 ? statusIndex : statusFallback] || '';
                if (!title || !status || /18 most recently sent files|最近发送的文件/.test(title)) return null;
                return { sent, title, from, status };
            }).filter(Boolean).slice(0, 20);
        };
        const state = window.__kkindleTransfer;
        """;

    private static string Wrap(string body) => "(() => {\n" + Helpers + "\n" + body + "\n})()";

    public static string Read { get; } = Wrap("""
        const kind = pageKind();
        if (!uploadPage || kind === 'signin') return { page: kind, account: '' };
        return {
            page: kind,
            account: accountName(),
            readyNames: readyNames(),
            files: state ? state.files : [],
            recentFiles: recentFiles(),
            percentage: Number(document.querySelector('#s2k-sip-progress-bar')
                ?.getAttribute('data-progress-percentage')) || 0,
        hasError: hasError()
        };
        """);

    public static string Prepare(string batchId, IReadOnlyList<KindleWebFile> files) => Wrap($$"""
        if (!uploadPage || pageKind() !== 'ready' || readyNames().length || hasError()) return false;
        state?.observer?.disconnect();
        document.getElementById('kkindle-send-files')?.remove();
        const input = document.createElement('input');
        input.type = 'file'; input.multiple = true; input.id = 'kkindle-send-files';
        input.style.display = 'none';
        document.body.appendChild(input);
        window.__kkindleTransfer = {
            id: {{JsonSerializer.Serialize(batchId)}}, staged: false, submitted: false,
            expected: {{JsonSerializer.Serialize(files.Select(file => new { name = file.Name, size = file.Length }))}},
            files: []
        };
        return true;
        """);

    public static string Drop(string batchId) => Wrap($$"""
        if (!uploadPage || pageKind() !== 'ready' || !state
            || state.id !== {{JsonSerializer.Serialize(batchId)}} || state.staged || state.submitted) return false;
        const input = document.getElementById('kkindle-send-files');
        const files = Array.from(input?.files || []);
        if (files.length !== state.expected.length || files.some((file, i) =>
            file.name !== state.expected[i].name || file.size !== state.expected[i].size)) return false;
        const target = Array.from(document.querySelectorAll('.s2k-dnd-box')).find(visible);
        if (!target || readyNames().length || hasError()) return false;
        const transfer = new DataTransfer();
        for (const file of files) transfer.items.add(file);
        state.staged = true;
        target.dispatchEvent(new DragEvent('drop', { bubbles: true, cancelable: true, dataTransfer: transfer }));
        input.remove();
        return true;
        """);

    public static string Submit(string batchId) => Wrap($$"""
        if (!uploadPage || pageKind() !== 'ready' || !state
            || state.id !== {{JsonSerializer.Serialize(batchId)}} || !state.staged || state.submitted) return false;
        const names = readyNames();
        if (names.length !== state.expected.length || names.some(name =>
            !state.expected.some(file => file.name === name)) || new Set(names).size !== names.length || hasError()) return false;
        const button = document.querySelector('#s2k-r2s-send-button');
        const list = document.querySelector('#s2k-dnd-sip-file-list');
        const library = document.querySelector('#s2k-r2s-add2lib input[type=checkbox]');
        if (!list || !library || !visible(button)) return false;
        if (!library.checked) {
            library.checked = true;
            library.dispatchEvent(new Event('change', { bubbles: true }));
        }
        if (button.disabled || button.getAttribute('aria-disabled') === 'true') return false;
        state.files = names.map(name => ({ name, status: 'sending' }));
        const inspectRow = row => {
            const title = row.querySelector('.s2k-dnd-file-title');
            const name = title?.getAttribute('title') || title?.textContent;
            const file = state.files.find(file => file.name === name);
            const icon = row.querySelector('.s2k-dnd-file-progress-icon');
            if (!file || !icon) return;
            if (icon.classList.contains('s2k-dnd-icons-done')) file.status = 'submitted';
            else if (icon.classList.contains('s2k-dnd-icons-failed')) file.status = 'failed';
        };
        const inspectNode = node => {
            if (node.nodeType !== Node.ELEMENT_NODE) return;
            const row = node.closest('.s2k-dnd-file-item');
            if (row) inspectRow(row);
            node.querySelectorAll('.s2k-dnd-file-item').forEach(inspectRow);
        };
        // Amazon removes completed rows immediately. Inspect removed nodes as
        // well, preserving terminal results between desktop polling ticks.
        state.observer = new MutationObserver(records => {
            for (const record of records) {
                inspectNode(record.target);
                record.addedNodes.forEach(inspectNode);
                record.removedNodes.forEach(inspectNode);
            }
        });
        state.observer.observe(list, { subtree: true, childList: true, attributes: true, attributeFilter: ['class'] });
        state.submitted = true;
        button.click();
        return true;
        """);
}
