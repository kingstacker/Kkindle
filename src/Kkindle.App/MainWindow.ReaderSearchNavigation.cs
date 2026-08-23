using System.Text.Json;
using Kkindle.Core;

namespace Kkindle;

public partial class MainWindow
{
    private async Task ScrollToPendingReaderChunkAsync(
        IReaderHost host,
        CancellationToken cancellationToken)
    {
        if (_readerPendingChunkOffset is not int offset) return;
        _readerPendingChunkOffset = null;
        var searchQuery = _readerPendingSearchQuery;
        _readerPendingSearchQuery = null;
        var searchContext = _readerPendingSearchContext;
        _readerPendingSearchContext = null;
        cancellationToken.ThrowIfCancellationRequested();

        var serializedQuery = JsonSerializer.Serialize(searchQuery ?? string.Empty);
        var serializedContext = JsonSerializer.Serialize(searchContext ?? string.Empty);
        var pagination = _readerLayout.FlowMode == 1 ? "true" : "false";
        var script = $$"""
            (() => {
              try {
              const root = document.body;
              if (!root) return 0;
              // Clear the previous whole-book hit without changing the chapter
              // text. The result list can be clicked repeatedly in one chapter.
              const unwrap = mark => {
                const parent = mark.parentNode;
                if (!parent) return;
                while (mark.firstChild) parent.insertBefore(mark.firstChild, mark);
                parent.removeChild(mark);
                if (typeof parent.normalize === 'function') parent.normalize();
              };
              document.querySelectorAll('mark.kkindle-search-hit').forEach(unwrap);

              const query = ({{serializedQuery}} || '').trim();
              const foldedQuery = query.toLocaleLowerCase();
              const normalizeContext = value => (value || '').replace(/\s+/g, ' ').trim().toLocaleLowerCase();
              const targetContext = normalizeContext({{serializedContext}});
              const terms = [...new Set(query
                .split(/\s+/)
                .map(term => term.trim())
                .filter(Boolean))];
              // Do not pass a NodeFilter callback. Reader pages intentionally
              // have script execution disabled; this injected script must be
              // self-contained in the current WebView call.
              const walker = document.createTreeWalker(root, NodeFilter.SHOW_TEXT);
              const foldedTerms = terms
                .filter(term => term.toLocaleLowerCase() !== foldedQuery)
                .map(term => ({ original: term, folded: term.toLocaleLowerCase() }));
              let cursor = 0;
              let fallback = null;
              let bestExact = null;
              let bestExactDistance = Number.POSITIVE_INFINITY;
              let bestExactContextScore = -1;
              const exactMatches = [];
              const termMatches = [];
              let bestTerm = null;
              let bestTermDistance = Number.POSITIVE_INFINITY;
              let bestTermContextScore = -1;

              const contextScores = new WeakMap();
              const contextScore = block => {
                if (!block || !targetContext) return 0;
                if (contextScores.has(block)) return contextScores.get(block);
                const blockText = normalizeContext(block.textContent || '');
                let score = 0;
                if (blockText === targetContext) score = 100000 + blockText.length;
                else if (blockText.includes(targetContext)) score = 90000 + targetContext.length;
                else if (targetContext.includes(blockText)) score = 80000 + blockText.length;
                else if (foldedQuery) {
                  const targetIndex = targetContext.indexOf(foldedQuery);
                  let blockIndex = blockText.indexOf(foldedQuery);
                  while (targetIndex >= 0 && blockIndex >= 0) {
                    let before = 0;
                    while (before < 160
                        && targetIndex - before - 1 >= 0
                        && blockIndex - before - 1 >= 0
                        && targetContext[targetIndex - before - 1] === blockText[blockIndex - before - 1]) before++;
                    let after = 0;
                    const targetAfter = targetIndex + foldedQuery.length;
                    const blockAfter = blockIndex + foldedQuery.length;
                    while (after < 220
                        && targetAfter + after < targetContext.length
                        && blockAfter + after < blockText.length
                        && targetContext[targetAfter + after] === blockText[blockAfter + after]) after++;
                    score = Math.max(score, before + after);
                    blockIndex = blockText.indexOf(
                      foldedQuery,
                      blockIndex + Math.max(1, foldedQuery.length));
                  }
                }
                contextScores.set(block, score);
                return score;
              };

              const consider = (current, distance, kind) => {
                const score = contextScore(current.block || current.node.parentElement);
                if (kind === 'exact') {
                  if (score > bestExactContextScore
                      || (score === bestExactContextScore && distance < bestExactDistance)) {
                    bestExactContextScore = score;
                    bestExactDistance = distance;
                    bestExact = current;
                  }
                } else if (score > bestTermContextScore
                    || (score === bestTermContextScore && distance < bestTermDistance)) {
                  bestTermContextScore = score;
                  bestTermDistance = distance;
                  bestTerm = current;
                }
              };

              while (walker.nextNode()) {
                const node = walker.currentNode;
                const parent = node.parentElement;
                if (!parent
                    || ['SCRIPT', 'STYLE', 'NOSCRIPT'].includes(parent.tagName)
                    || parent.closest?.('#kkindle-selection-bar, .kkindle-wave-sweep')
                    || (typeof parent.closest === 'function'
                        && parent.closest('mark.kkindle-search-hit'))) continue;
                const text = node.data || '';
                if (!fallback && cursor + text.length >= {{Math.Max(0, offset)}})
                  fallback = node.parentElement || root;
                const foldedText = text.toLocaleLowerCase();
                if (foldedQuery) {
                  let localIndex = foldedText.indexOf(foldedQuery);
                  while (localIndex >= 0) {
                    const current = {
                      node,
                      start: localIndex,
                      length: query.length,
                      block: parent.closest?.('p,li,blockquote,dd,dt,h1,h2,h3,h4,h5,h6,div') || parent
                    };
                    exactMatches.push(current);
                    consider(current, Math.abs(cursor + localIndex - {{Math.Max(0, offset)}}), 'exact');
                    localIndex = foldedText.indexOf(
                      foldedQuery,
                      localIndex + Math.max(1, foldedQuery.length));
                  }
                }
                for (const term of foldedTerms) {
                  let localIndex = foldedText.indexOf(term.folded);
                  while (localIndex >= 0) {
                    const current = {
                      node,
                      start: localIndex,
                      length: term.original.length,
                      block: parent.closest?.('p,li,blockquote,dd,dt,h1,h2,h3,h4,h5,h6,div') || parent
                    };
                    termMatches.push(current);
                    consider(current, Math.abs(cursor + localIndex - {{Math.Max(0, offset)}}), 'term');
                    localIndex = foldedText.indexOf(
                      term.folded,
                      localIndex + Math.max(1, term.folded.length));
                  }
                }
                cursor += text.length;
              }

              const target = bestExact || bestTerm;
              if (!target) {
                if (fallback) fallback.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
                return 0;
              }

              const createMark = current => {
                const range = document.createRange();
                range.setStart(current.node, current.start);
                range.setEnd(current.node, current.start + current.length);
                const hit = document.createElement('mark');
                hit.className = 'kkindle-search-hit';
                hit.style.setProperty('background', '#000000', 'important');
                hit.style.setProperty('background-color', '#000000', 'important');
                hit.style.setProperty('color', '#ffffff', 'important');
                hit.style.setProperty('text-decoration', 'none', 'important');
                range.surroundContents(hit);
                return hit;
              };

              let mark = null;
              const matches = bestExact ? exactMatches : termMatches;
              const scroller = document.scrollingElement || document.documentElement;
              const step = {{ReaderPaginationScripts.PageStepExpression}};
              const matchPage = current => {
                if (!{{pagination}} || step <= 0) return -1;
                const range = document.createRange();
                range.setStart(current.node, current.start);
                range.setEnd(current.node, current.start + current.length);
                const rects = range.getClientRects ? Array.from(range.getClientRects()) : [];
                const rect = rects.find(item => item.width > 0 || item.height > 0)
                  || range.getBoundingClientRect();
                if (!rect || !Number.isFinite(rect.left)) return -1;
                const absoluteLeft = rect.left + scroller.scrollLeft + Math.max(0, rect.width) / 2;
                return Math.floor(Math.max(0, absoluteLeft) / step);
              };
              const targetPage = matchPage(target);
              const visibleMatches = matches.filter(current => {{pagination}} && targetPage >= 0
                ? matchPage(current) === targetPage
                : current.block === target.block);
              if (visibleMatches.length > 0) {
                // Highlight every occurrence in the rendered page/paragraph,
                // while retaining the selected match as the scroll target.
                for (let index = visibleMatches.length - 1; index >= 0; index--) {
                  const current = visibleMatches[index];
                  const hit = createMark(current);
                  if (current === target) mark = hit;
                }
              }
              if (!mark) mark = createMark(target);

              if ({{pagination}}) {
                // scrollIntoView handles both the positive horizontal range
                // and vertical-rl's negative one natively; the host snaps the
                // result to the owning page boundary right after this script.
                mark.scrollIntoView({ block: 'nearest', inline: 'center', behavior: 'instant' });
              } else {
                mark.scrollIntoView({ block: 'center', inline: 'nearest', behavior: 'instant' });
              }
              return 1;
              } catch (_) {
                return 0;
              }
            })();
            """;
        try
        {
            await host.InvokeScriptAsync(script);
            if (_readerLayout.FlowMode == 1)
                await host.InvokeScriptAsync(ReaderPaginationScripts.Snap(_readerLayout.VerticalWriting));
        }
        catch
        {
            // Location decoration is best-effort; a stale host must not turn
            // an otherwise successful chapter navigation into an error.
        }
    }
}
