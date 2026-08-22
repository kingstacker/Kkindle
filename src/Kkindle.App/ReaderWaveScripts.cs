using System.Globalization;

namespace Kkindle;

// Builds native View Transition animations for in-document turns and captured
// page overlays for chapter transitions. Neither path transforms the live EPUB
// body, so animation cannot alter the multicolumn scroll extent.
internal static class ReaderWaveScripts
{
    public const int TotalDurationMs = 420;

    public static string CreateWaveOverlayScript(
        string dataUrl,
        double width,
        double height,
        bool forward,
        int totalDurationMs = TotalDurationMs,
        bool startPaused = false)
    {
        var totalDuration = Math.Clamp(totalDurationMs, 240, 2000);
        var w = Format(width);
        var h = Format(height);
        var old0 = CreateRefreshClip(0, forward);
        var old1 = CreateRefreshClip(.14, forward);
        var old2 = CreateRefreshClip(.56, forward);
        var old3 = CreateRefreshClip(.88, forward);
        var old4 = CreateRefreshClip(1, forward);
        var frontStart = forward ? width - 18 : -22;
        var frontEnd = forward ? -22 : width - 18;
        return $$"""
            (() => {
              try {
                const root = document.documentElement;
                const W = {{w}}, H = {{h}};
                if (W < 2 || H < 2) return false;
                const DATA = "{{dataUrl}}";
                document.getElementById('kk-wave')?.remove();
                document.getElementById('kk-wave-style')?.remove();

                const style = document.createElement('style');
                style.id = 'kk-wave-style';
                style.textContent = `
                  #kk-wave { position: fixed; inset: 0; width: 100%; height: 100%;
                              overflow: hidden; pointer-events: none; z-index: 2147483000;
                              background: transparent; }
                  #kk-wave-image { position: absolute; inset: 0;
                                   width: ${W}px !important; height: ${H}px !important;
                                   max-width: none !important; max-height: none !important;
                                   margin: 0 !important; padding: 0 !important; }
                  #kk-wave-image { will-change: clip-path, filter;
                                   animation: kk-kindle-refresh-old {{totalDuration}}ms cubic-bezier(.22,.62,.28,1) both;
                                   animation-play-state: {{(startPaused ? "paused" : "running")}}; }
                  @keyframes kk-kindle-refresh-old {
                    0%   { clip-path: {{old0}}; filter: none; }
                    18%  { clip-path: {{old1}}; filter: grayscale(.12) brightness(1.005) contrast(.99); }
                    54%  { clip-path: {{old2}}; filter: grayscale(.2) brightness(1.01) contrast(.975); }
                    82%  { clip-path: {{old3}}; filter: grayscale(.12) brightness(1.005) contrast(.99); }
                    100% { clip-path: {{old4}}; filter: none; }
                  }
                  #kk-wave-front { position: absolute; left: 0; top: 0;
                                   width: 40px; height: 100%; opacity: 0;
                                   will-change: transform, opacity;
                                   background: linear-gradient(90deg,
                                     transparent 0%, rgba(70,70,70,.025) 22%,
                                     rgba(255,255,255,.62) 48%, rgba(92,92,92,.035) 72%,
                                     transparent 100%);
                                   transform: translate3d({{Format(frontStart)}}px,0,0);
                                   animation: kk-kindle-refresh-front {{totalDuration}}ms cubic-bezier(.22,.62,.28,1) both;
                                   animation-play-state: {{(startPaused ? "paused" : "running")}}; }
                  @keyframes kk-kindle-refresh-front {
                    0%   { opacity: 0; transform: translate3d({{Format(frontStart)}}px,0,0); }
                    8%   { opacity: .76; }
                    88%  { opacity: .64; }
                    100% { opacity: 0; transform: translate3d({{Format(frontEnd)}}px,0,0); }
                  }
                `;
                document.head.appendChild(style);

                const container = document.createElement('div');
                container.id = 'kk-wave';
                const canvas = document.createElement('canvas');
                canvas.id = 'kk-wave-image';
                canvas.dataset.kkReady = 'false';
                container.appendChild(canvas);
                const front = document.createElement('div');
                front.id = 'kk-wave-front';
                container.appendChild(front);

                root.appendChild(container);
                window.__kkindleStartWaveOverlay = () => {
                  canvas.dataset.kkStartRequested = 'true';
                  if (canvas.dataset.kkReady !== 'true') return true;
                  container.querySelectorAll('#kk-wave-image, #kk-wave-front').forEach(node => {
                    node.style.animationPlayState = 'running';
                  });
                  return true;
                };
                const encoded = DATA.slice(DATA.indexOf(',') + 1);
                const raw = atob(encoded);
                const bytes = new Uint8Array(raw.length);
                for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                createImageBitmap(new Blob([bytes], { type: 'image/png' })).then(bitmap => {
                  canvas.width = bitmap.width;
                  canvas.height = bitmap.height;
                  canvas.getContext('2d', { alpha: false }).drawImage(bitmap, 0, 0);
                  bitmap.close();
                  canvas.dataset.kkReady = 'true';
                  if (canvas.dataset.kkStartRequested === 'true') {
                    window.__kkindleStartWaveOverlay?.();
                  }
                }).catch(() => {
                  canvas.dataset.kkReady = 'error';
                });
                return true;
              } catch (_) {
                const old = document.getElementById('kk-wave');
                if (old) old.remove();
                const st = document.getElementById('kk-wave-style');
                if (st) st.remove();
                return false;
              }
            })();
            """;
    }

    public static string CreateWaveStartScript() =>
        """
        (() => {
          const wave = document.getElementById('kk-wave');
          if (!wave) return false;
          return window.__kkindleStartWaveOverlay?.() === true;
        })();
        """;

    public static string WaveOverlayReadyScript =>
        "document.getElementById('kk-wave-image')?.dataset.kkReady === 'true'";

    public static string CreateSlideViewTransitionStartScript(
        bool forward,
        int durationMs) =>
        CreateViewTransitionStartScript(forward, durationMs, wave: false);

    public static string CreateWaveViewTransitionStartScript(
        bool forward,
        int durationMs) =>
        CreateViewTransitionStartScript(forward, durationMs, wave: true);

    public static string ViewTransitionReadyScript =>
        "Boolean(window.__kkindleViewTransitionReady)";

    public static string ViewTransitionReleaseScript =>
        "window.__kkindleViewTransitionRelease?.() === true";

    public static string ViewTransitionCleanupScript =>
        """
        (() => {
          try { window.__kkindleViewTransitionRelease?.(); } catch (_) {}
          try { window.__kkindleViewTransition?.skipTransition?.(); } catch (_) {}
          document.getElementById('kk-view-transition-style')?.remove();
          delete window.__kkindleViewTransition;
          delete window.__kkindleViewTransitionReady;
          delete window.__kkindleViewTransitionRelease;
          return true;
        })();
        """;

    public static string CreateWaveCleanupScript() =>
        """
        (() => {
          const el = document.getElementById('kk-wave');
          if (el) el.remove();
          const st = document.getElementById('kk-wave-style');
          if (st) st.remove();
          delete window.__kkindleStartWaveOverlay;
          return true;
        })();
        """;

    // The slide transition uses the same captured-page model as the wave: a
    // fixed snapshot moves while the live multicolumn document changes
    // underneath. Transforming body/documentElement changes Chromium's scroll
    // extent and can clamp a fractional last-page position before it is shown.
    public static string CreateSlideOverlayScript(
        string dataUrl,
        double width,
        double height,
        bool forward,
        int durationMs,
        bool startPaused = false)
    {
        var duration = Math.Clamp(durationMs, 120, 1200);
        var w = Format(width);
        var h = Format(height);
        var slideEnd = forward ? "-100%" : "100%";
        var edge = forward ? "-18px" : "18px";
        return $$"""
            (() => {
              try {
                const root = document.documentElement;
                const W = {{w}}, H = {{h}};
                if (!root || W < 2 || H < 2) return false;
                document.getElementById('kk-slide')?.remove();
                document.getElementById('kk-slide-style')?.remove();

                const style = document.createElement('style');
                style.id = 'kk-slide-style';
                style.textContent = `
                  #kk-slide { position: fixed; inset: 0; width: 100%; height: 100%;
                              overflow: hidden; pointer-events: none; z-index: 2147483000; }
                  #kk-slide-image { position: absolute; left: 0; top: 0;
                                    width: ${W}px !important; height: ${H}px !important;
                                    max-width: none !important; max-height: none !important;
                                    margin: 0 !important; padding: 0 !important;
                                    opacity: 1;
                                    will-change: transform;
                                    transform: translate3d(0,0,0);
                                    animation: kk-slide-page {{duration}}ms cubic-bezier(.38,0,.2,1) both;
                                    animation-play-state: {{(startPaused ? "paused" : "running")}}; }
                  #kk-slide-edge { position: absolute; left: 0; top: 0;
                                   width: 100%; height: 100%; opacity: 1;
                                   box-shadow: {{edge}} 0 28px rgba(0,0,0,.2);
                                   will-change: transform;
                                   animation: kk-slide-page {{duration}}ms cubic-bezier(.38,0,.2,1) both;
                                   animation-play-state: {{(startPaused ? "paused" : "running")}}; }
                  @keyframes kk-slide-page {
                    0% { transform: translate3d(0,0,0); }
                    100% { transform: translate3d({{slideEnd}},0,0); }
                  }
                `;
                document.head.appendChild(style);

                const container = document.createElement('div');
                container.id = 'kk-slide';
                const canvas = document.createElement('canvas');
                canvas.id = 'kk-slide-image';
                canvas.dataset.kkReady = 'false';
                container.appendChild(canvas);
                const edge = document.createElement('div');
                edge.id = 'kk-slide-edge';
                container.appendChild(edge);
                root.appendChild(container);
                window.__kkindleStartSlideOverlay = () => {
                  canvas.dataset.kkStartRequested = 'true';
                  if (canvas.dataset.kkReady !== 'true') return true;
                  container.querySelectorAll('#kk-slide-image, #kk-slide-edge').forEach(node => {
                    node.style.animationPlayState = 'running';
                  });
                  return true;
                };
                const DATA = "{{dataUrl}}";
                const encoded = DATA.slice(DATA.indexOf(',') + 1);
                const raw = atob(encoded);
                const bytes = new Uint8Array(raw.length);
                for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                createImageBitmap(new Blob([bytes], { type: 'image/png' })).then(bitmap => {
                  canvas.width = bitmap.width;
                  canvas.height = bitmap.height;
                  canvas.getContext('2d', { alpha: false }).drawImage(bitmap, 0, 0);
                  bitmap.close();
                  canvas.dataset.kkReady = 'true';
                  if (canvas.dataset.kkStartRequested === 'true') {
                    window.__kkindleStartSlideOverlay?.();
                  }
                }).catch(() => {
                  canvas.dataset.kkReady = 'error';
                });
                return true;
              } catch (_) {
                document.getElementById('kk-slide')?.remove();
                document.getElementById('kk-slide-style')?.remove();
                return false;
              }
            })();
            """;
    }

    public static string CreateSlideStartScript() =>
        """
        (() => {
          const slide = document.getElementById('kk-slide');
          if (!slide) return false;
          return window.__kkindleStartSlideOverlay?.() === true;
        })();
        """;

    public static string SlideOverlayReadyScript =>
        "document.getElementById('kk-slide-image')?.dataset.kkReady === 'true'";

    public static string CreateSlideCleanupScript() =>
        """
        (() => {
          document.getElementById('kk-slide')?.remove();
          document.getElementById('kk-slide-style')?.remove();
          delete window.__kkindleStartSlideOverlay;
          return true;
        })();
        """;

    private static string CreateViewTransitionStartScript(
        bool forward,
        int durationMs,
        bool wave)
    {
        var duration = Math.Clamp(durationMs, 180, 1600);
        var slideTarget = forward ? "-100%" : "100%";
        var slideShadow = forward ? "-22px" : "22px";
        var refresh0 = CreateRefreshClip(0, forward);
        var refresh1 = CreateRefreshClip(.14, forward);
        var refresh2 = CreateRefreshClip(.56, forward);
        var refresh3 = CreateRefreshClip(.88, forward);
        var refresh4 = CreateRefreshClip(1, forward);
        var animationCss = wave
            ? $$"""
              ::view-transition-old(root) {
                z-index: 2; mix-blend-mode: normal;
                animation: kk-kindle-refresh-old {{duration}}ms cubic-bezier(.22,.62,.28,1) both;
              }
              ::view-transition-new(root) {
                z-index: 1; mix-blend-mode: normal; animation: none;
              }
              @keyframes kk-kindle-refresh-old {
                0% { clip-path: {{refresh0}}; filter: none; }
                18% { clip-path: {{refresh1}}; filter: grayscale(.12) brightness(1.005) contrast(.99); }
                54% { clip-path: {{refresh2}}; filter: grayscale(.2) brightness(1.01) contrast(.975); }
                82% { clip-path: {{refresh3}}; filter: grayscale(.12) brightness(1.005) contrast(.99); }
                100% { clip-path: {{refresh4}}; filter: none; }
              }
              """
            : $$"""
              ::view-transition-old(root) {
                z-index: 2; mix-blend-mode: normal;
                animation: kk-slide-old {{duration}}ms cubic-bezier(.38,0,.2,1) both;
              }
              ::view-transition-new(root) {
                z-index: 1; mix-blend-mode: normal; animation: none;
              }
              @keyframes kk-slide-old {
                0% { opacity: 1; transform: translate3d(0,0,0); box-shadow: {{slideShadow}} 0 28px rgba(0,0,0,.2); }
                100% { opacity: 1; transform: translate3d({{slideTarget}},0,0); box-shadow: 0 0 0 rgba(0,0,0,0); }
              }
              """;

        return $$"""
            (() => {
              try {
                if (typeof document.startViewTransition !== 'function'
                    || window.__kkindleViewTransition) return false;
                document.getElementById('kk-view-transition-style')?.remove();
                const style = document.createElement('style');
                style.id = 'kk-view-transition-style';
                style.textContent = `
                  ::view-transition-group(root) { animation-duration: {{duration}}ms; }
                  ::view-transition-image-pair(root) { isolation: isolate; }
                  {{animationCss}}
                `;
                document.head.appendChild(style);

                let releaseUpdate;
                const updateGate = new Promise(resolve => { releaseUpdate = resolve; });
                window.__kkindleViewTransitionReady = false;
                window.__kkindleViewTransitionRelease = () => {
                  if (!releaseUpdate) return false;
                  const release = releaseUpdate;
                  releaseUpdate = null;
                  release();
                  return true;
                };
                const transition = document.startViewTransition(async () => {
                  window.__kkindleViewTransitionReady = true;
                  await updateGate;
                });
                window.__kkindleViewTransition = transition;
                transition.finished.catch(() => {});
                return true;
              } catch (_) {
                document.getElementById('kk-view-transition-style')?.remove();
                delete window.__kkindleViewTransition;
                delete window.__kkindleViewTransitionReady;
                delete window.__kkindleViewTransitionRelease;
                return false;
              }
            })();
            """;
    }

    private static string CreateRefreshClip(double progress, bool forward)
    {
        progress = Math.Clamp(progress, 0, 1);
        var inset = Format(progress * 100);
        return forward
            ? $"inset(0 {inset}% 0 0)"
            : $"inset(0 0 0 {inset}%)";
    }

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
