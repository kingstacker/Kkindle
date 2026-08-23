using System.Globalization;

namespace Kkindle;

// Builds the modern-Kindle e-ink refresh for in-document turns and captured
// page overlays for chapter transitions. Neither path transforms the live EPUB
// body, so animation cannot alter the multicolumn scroll extent: old and new
// page stay put geometrically and a horizontal refresh wave replaces content.
internal static class ReaderWaveScripts
{
    // 波前传播时长（普通文本翻页约 200~250ms）。
    public const int TotalDurationMs = 230;

    // 残影保持到波形结束后再完全消退所需的尾部时长。
    public const int GhostTailMs = 320;

    private const string WaveEase = "cubic-bezier(.3,.08,.35,.96)";

    public static string CreateWaveOverlayScript(
        string dataUrl,
        double width,
        double height,
        bool forward,
        int totalDurationMs = TotalDurationMs,
        bool startPaused = false)
    {
        var totalDuration = Math.Clamp(totalDurationMs, 160, 2000);
        var w = Format(width);
        var h = Format(height);
        var playState = startPaused ? "paused" : "running";
        // 残影强度 1%~3%，默认 2.2%；波形结束后短暂保持再消退。
        var ghostOpacity = "0.022";
        var ghostFadeMs = 260.ToString(CultureInfo.InvariantCulture);
        var colorSettleMs = 360.ToString(CultureInfo.InvariantCulture);
        // 前沿条带宽度取屏宽的 12.5%，落在真实设备 10%~15% 的范围内。
        var bandWidth = Math.Clamp(width * 0.125, 24, Math.Max(24, width * 0.15));
        var halfBand = bandWidth / 2;
        var blurRadius = Math.Max(3, bandWidth * 0.055);
        var trailWidth = bandWidth * 2.1;
        var trailBlur = Math.Max(6, bandWidth * 0.16);
        var trailLag = bandWidth * 0.42;
        // 前进翻页时新页从右缘向左缘刷新（墨水刷新与阅读流向相反），后退翻页相反。
        var bandStart = forward ? width + halfBand : -halfBand;
        var bandEnd = forward ? -halfBand : width + halfBand;
        // 拖尾带落后于前沿：行进向左时拖尾在右，反之亦然。
        var trailStart = bandStart + (forward ? trailLag : -trailLag);
        var trailEnd = bandEnd + (forward ? trailLag : -trailLag);

        // 软边缘遮罩：把 2.5 倍宽的黑白渐变蒙版用 mask-position 滑过页面，
        // 渐变里长度等于条带宽度的过渡区就是刷新前沿——旧页与新页之间没有
        // 硬边界，也不存在整体位移。蒙版滑动与条带位移使用同一时长与缓动，
        // 因此前沿全程锁步。墨水迁移感由滞后于前沿的拖尾带表现。
        const double maskScale = 2.5;
        var bandFraction = bandWidth / width;
        var ramp0 = 0.5 - bandFraction / (2 * maskScale);
        var ramp1 = 0.5 + bandFraction / (2 * maskScale);
        var posLeading = Format(ramp1 * maskScale / (maskScale - 1) * 100);
        var posTrailing = Format((ramp0 * maskScale - 1) / (maskScale - 1) * 100);
        var posFrom = forward ? posTrailing : posLeading;
        var posTo = forward ? posLeading : posTrailing;
        var maskAngle = forward ? "270deg" : "90deg";
        var maskStops =
            $"transparent 0%, transparent {Format(ramp0 * 100)}%, #000 {Format(ramp1 * 100)}%, #000 100%";
        // 拖尾带的暗侧朝向前沿行进方向。
        var trailGradient = forward
            ? "linear-gradient(270deg, rgba(0,0,0,0) 0%, rgba(72,72,72,.18) 52%, rgba(48,48,48,.28) 80%, rgba(30,30,30,.32) 100%)"
            : "linear-gradient(90deg, rgba(0,0,0,0) 0%, rgba(72,72,72,.18) 52%, rgba(48,48,48,.28) 80%, rgba(30,30,30,.32) 100%)";
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
                  #kk-wave canvas { position: absolute; inset: 0;
                                    width: ${W}px !important; height: ${H}px !important;
                                    max-width: none !important; max-height: none !important;
                                    margin: 0 !important; padding: 0 !important; }
                  #kk-wave-image { will-change: mask-position;
                                   -webkit-mask-image: linear-gradient({{maskAngle}}, {{maskStops}});
                                   -webkit-mask-size: 250% 100%;
                                   -webkit-mask-repeat: no-repeat;
                                   -webkit-mask-position: {{posFrom}}% 0;
                                   mask-image: linear-gradient({{maskAngle}}, {{maskStops}});
                                   mask-size: 250% 100%;
                                   mask-repeat: no-repeat;
                                   mask-position: {{posFrom}}% 0;
                                   animation: kk-eink-wave-old {{totalDuration}}ms {{WaveEase}} both;
                                   animation-play-state: {{playState}}; }
                  @keyframes kk-eink-wave-old {
                    0%   { -webkit-mask-position: {{posFrom}}% 0; mask-position: {{posFrom}}% 0; }
                    100% { -webkit-mask-position: {{posTo}}% 0; mask-position: {{posTo}}% 0; }
                  }
                  #kk-wave-ghost { opacity: 0;
                                   animation: kk-eink-ghost-hold {{totalDuration}}ms linear both,
                                              kk-eink-ghost-fade {{ghostFadeMs}}ms linear {{totalDuration}}ms forwards;
                                   animation-play-state: {{playState}}; }
                  @keyframes kk-eink-ghost-hold {
                    0%, 58% { opacity: 0; }
                    76%, 100% { opacity: {{ghostOpacity}}; }
                  }
                  @keyframes kk-eink-ghost-fade {
                    0% { opacity: {{ghostOpacity}}; }
                    100% { opacity: 0; }
                  }
                  #kk-wave-trail { position: absolute; top: 0; height: 100%;
                                   width: {{Format(trailWidth)}}px;
                                   opacity: .1; mix-blend-mode: multiply;
                                   filter: blur({{Format(trailBlur)}}px);
                                   will-change: transform, opacity;
                                   background: {{trailGradient}};
                                   transform: translate3d({{Format(trailStart)}}px,0,0);
                                   animation: kk-eink-trail-move {{totalDuration}}ms {{WaveEase}} both,
                                              kk-eink-jitter 130ms steps(2, jump-none) infinite;
                                   animation-play-state: {{playState}}; }
                  @keyframes kk-eink-trail-move {
                    0%   { transform: translate3d({{Format(trailStart)}}px,0,0); }
                    100% { transform: translate3d({{Format(trailEnd)}}px,0,0); }
                  }
                  #kk-wave-front { position: absolute; top: 0; height: 100%;
                                   width: {{Format(bandWidth)}}px;
                                   opacity: .95; mix-blend-mode: multiply;
                                   filter: blur({{Format(blurRadius)}}px);
                                   will-change: transform, opacity;
                                   background: linear-gradient(90deg,
                                     rgba(0,0,0,0) 0%, rgba(64,64,64,.17) 26%,
                                     rgba(44,44,44,.30) 46%, rgba(36,36,36,.34) 54%,
                                     rgba(70,70,70,.18) 74%, rgba(0,0,0,0) 100%);
                                   transform: translate3d({{Format(bandStart)}}px,0,0);
                                   animation: kk-eink-band-move {{totalDuration}}ms {{WaveEase}} both,
                                              kk-eink-jitter 110ms steps(2, jump-none) infinite;
                                   animation-play-state: {{playState}}; }
                  @keyframes kk-eink-band-move {
                    0%   { transform: translate3d({{Format(bandStart)}}px,0,0); }
                    100% { transform: translate3d({{Format(bandEnd)}}px,0,0); }
                  }
                  @keyframes kk-eink-jitter {
                    0%   { opacity: .86; }
                    33%  { opacity: 1; }
                    66%  { opacity: .82; }
                    100% { opacity: .95; }
                  }
                  #kk-wave-front::after { content: ''; position: absolute; inset: 0; opacity: .45;
                                          background: repeating-linear-gradient(90deg,
                                            rgba(255,255,255,.05) 0px, rgba(255,255,255,.05) 2px,
                                            rgba(0,0,0,.06) 2px, rgba(0,0,0,.06) 4px);
                                          background-size: 8px 100%;
                                          animation: kk-eink-noise 96ms steps(2, jump-none) infinite alternate; }
                  @keyframes kk-eink-noise {
                    0%   { background-position: 0 0; }
                    100% { background-position: -8px 0; }
                  }
                  #kk-wave-color { position: absolute; inset: 0;
                                   will-change: backdrop-filter, background-color;
                                   animation: kk-eink-color-settle {{colorSettleMs}}ms linear both,
                                              kk-eink-color-tint {{colorSettleMs}}ms linear both;
                                   animation-play-state: {{playState}}; }
                  @keyframes kk-eink-color-settle {
                    0%   { backdrop-filter: saturate(.62) hue-rotate(6deg) contrast(1.02); }
                    30%  { backdrop-filter: saturate(.74) hue-rotate(-4deg) contrast(1.01); }
                    64%  { backdrop-filter: saturate(.9) hue-rotate(2deg) contrast(1); }
                    100% { backdrop-filter: saturate(1) hue-rotate(0deg) contrast(1); }
                  }
                  @keyframes kk-eink-color-tint {
                    0%   { background-color: rgba(118,118,118,.12); }
                    100% { background-color: rgba(118,118,118,0); }
                  }
                `;
                document.head.appendChild(style);

                const container = document.createElement('div');
                container.id = 'kk-wave';
                const canvas = document.createElement('canvas');
                canvas.id = 'kk-wave-image';
                canvas.dataset.kkReady = 'false';
                const ghost = document.createElement('canvas');
                ghost.id = 'kk-wave-ghost';
                container.appendChild(canvas);
                container.appendChild(ghost);
                root.appendChild(container);
                window.__kkindleStartWaveOverlay = () => {
                  canvas.dataset.kkStartRequested = 'true';
                  if (canvas.dataset.kkReady !== 'true') return true;
                  for (const node of container.children) {
                    node.style.animationPlayState = 'running';
                  }
                  return true;
                };
                const encoded = DATA.slice(DATA.indexOf(',') + 1);
                const raw = atob(encoded);
                const bytes = new Uint8Array(raw.length);
                for (let i = 0; i < raw.length; i++) bytes[i] = raw.charCodeAt(i);
                createImageBitmap(new Blob([bytes], { type: 'image/png' })).then(bitmap => {
                  const context = canvas.getContext('2d', { alpha: false });
                  canvas.width = bitmap.width;
                  canvas.height = bitmap.height;
                  context.drawImage(bitmap, 0, 0);
                  const ghostContext = ghost.getContext('2d', { alpha: false });
                  ghost.width = bitmap.width;
                  ghost.height = bitmap.height;
                  ghostContext.drawImage(bitmap, 0, 0);
                  try {
                    // 彩色内容（Colorsoft 类）需要短暂低饱和度与颜色稳定过程；
                    // 对快照降采样估计色度，纯文本页面不会触发。
                    const sw = 48;
                    const sh = Math.max(8, Math.round(48 * bitmap.height / bitmap.width));
                    const sample = document.createElement('canvas');
                    sample.width = sw;
                    sample.height = sh;
                    const sampleContext = sample.getContext('2d', { willReadFrequently: true });
                    sampleContext.drawImage(bitmap, 0, 0, sw, sh);
                    const pixels = sampleContext.getImageData(0, 0, sw, sh).data;
                    let chromaSum = 0;
                    for (let i = 0; i < pixels.length; i += 4) {
                      chromaSum += Math.max(pixels[i], pixels[i + 1], pixels[i + 2])
                                 - Math.min(pixels[i], pixels[i + 1], pixels[i + 2]);
                    }
                    const chroma = chromaSum / (pixels.length / 4 * 255);
                    if (chroma > 0.045) {
                      const colorVeil = document.createElement('div');
                      colorVeil.id = 'kk-wave-color';
                      container.prepend(colorVeil);
                    }
                  } catch (_) {}
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
        var refresh4 = CreateRefreshClip(1, forward);
        var animationCss = wave
            ? $$"""
              ::view-transition-old(root) {
                z-index: 2; mix-blend-mode: normal;
                animation: kk-eink-vt-old {{duration}}ms cubic-bezier(.3,.08,.35,.96) both;
              }
              ::view-transition-new(root) {
                z-index: 1; mix-blend-mode: normal; animation: none;
              }
              @keyframes kk-eink-vt-old {
                0% { clip-path: {{refresh0}}; }
                100% { clip-path: {{refresh4}}; }
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
        // 前进翻页从右缘开始刷新：右内缩随进度增长；后退翻页镜像。
        return forward
            ? $"inset(0 {inset}% 0 0)"
            : $"inset(0 0 0 {inset}%)";
    }

    private static string Format(double value) =>
        value.ToString("0.##", CultureInfo.InvariantCulture);
}
