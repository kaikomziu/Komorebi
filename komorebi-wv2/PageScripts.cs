using System.Text.Json;

namespace Komorebi;

// ページに注入するスクリプトとCSS。
// DevTools の Runtime.evaluate で実行するのでページ側のCSPの影響を受けない。
// CSSは CSSStyleSheet.replaceSync + adoptedStyleSheets で当てる（インライン<style>と違いCSPで弾かれない）。
static class PageScripts
{
    // ---- CSS の挿入 / 削除 ----
    public static string InsertCss(string css, string key) => $$"""
        (() => {
          const s = new CSSStyleSheet();
          s.replaceSync({{JsonSerializer.Serialize(css)}});
          (window.__kmrSheets = window.__kmrSheets || {})[{{JsonSerializer.Serialize(key)}}] = s;
          document.adoptedStyleSheets = [...document.adoptedStyleSheets, s];
          return true;
        })()
        """;

    public static string RemoveCss(string key) => $$"""
        (() => {
          const k = {{JsonSerializer.Serialize(key)}};
          const s = (window.__kmrSheets || {})[k];
          if (s) {
            document.adoptedStyleSheets = document.adoptedStyleSheets.filter((x) => x !== s);
            delete window.__kmrSheets[k];
          }
          return true;
        })()
        """;

    // ---- 強制ダークモード用CSS（反転 + 画像/動画は再反転）----
    public const string DarkCss = """
        html{filter:invert(1) hue-rotate(180deg)!important;background:#fff!important}
        img,video,picture,canvas,svg image,iframe,[style*="background-image"]{filter:invert(1) hue-rotate(180deg)!important}
        """;

    // ---- リーダーモード: 本文を抽出して読みやすい表示に差し替える ----
    public const string ReaderJs = """
        (function () {
          const doc = document;
          if (doc.documentElement.hasAttribute('data-kmr-reader')) return 'already';

          const bad = [
            'script', 'style', 'noscript', 'nav', 'aside', 'footer', 'header', 'form', 'iframe',
            'button', 'input', 'select', 'textarea', 'svg', 'canvas', 'dialog',
            '[aria-hidden="true"]', '[role="navigation"]', '[role="banner"]', '[role="complementary"]',
            '.ad', '.ads', '.advert', '.advertisement', '.social', '.share', '.sidebar',
            '.related', '.comments', '#comments', '.newsletter', '.popup', '.modal', '.cookie',
          ].join(',');

          const score = (el) => {
            let s = 0;
            el.querySelectorAll('p').forEach((p) => {
              const t = p.textContent.trim();
              if (t.length > 40) s += t.length;
            });
            let links = 0;
            el.querySelectorAll('a').forEach((a) => { links += a.textContent.length; });
            return s - links * 0.5;
          };

          const cands = [...doc.querySelectorAll('article,main,[role=main],section,div')]
            .filter((e) => e.querySelectorAll('p').length >= 2);
          let best = null;
          let bs = 0;
          for (const e of cands) {
            const s = score(e);
            if (s > bs) { bs = s; best = e; }
          }
          if (!best || bs < 200) return 'fail';

          let descended = true;
          while (descended) {
            descended = false;
            for (const c of best.children) {
              const cs = score(c);
              if (cs >= bs * 0.8) { best = c; bs = cs; descended = true; break; }
            }
          }

          const clone = best.cloneNode(true);
          clone.querySelectorAll(bad).forEach((e) => e.remove());

          const keep = new Set(['src', 'href', 'alt', 'title', 'colspan', 'rowspan']);
          clone.querySelectorAll('img').forEach((img) => {
            const lazy = img.getAttribute('data-src') || img.getAttribute('data-original') ||
              img.getAttribute('data-lazy-src');
            if (lazy) img.setAttribute('src', new URL(lazy, location.href).href);
            else if (img.src) img.setAttribute('src', img.src);
          });
          clone.querySelectorAll('picture source').forEach((s) => s.remove());
          clone.querySelectorAll('a[href]').forEach((a) => a.setAttribute('href', a.href));
          [clone, ...clone.querySelectorAll('*')].forEach((e) => {
            for (const attr of [...e.attributes]) {
              if (!keep.has(attr.name)) e.removeAttribute(attr.name);
            }
          });

          const h1 = doc.querySelector('h1');
          const title = (h1 && h1.textContent.trim()) || doc.title || location.hostname;
          const firstH1 = clone.querySelector('h1');
          if (firstH1 && firstH1.textContent.trim() === title) firstH1.remove();

          const text = clone.textContent || '';
          const ascii = (text.match(/[\x00-\x7f]/g) || []).length / Math.max(1, text.length);
          const minutes = Math.max(1, Math.round(ascii > 0.7
            ? text.split(/\s+/).length / 220
            : text.length / 500));

          const root = doc.documentElement;
          let size = 19;
          const themes = ['light', 'sepia', 'dark'];
          let ti = 0;
          const applyTheme = () => root.setAttribute('data-kmr-theme', themes[ti]);
          const applySize = () => root.style.setProperty('--kmr-size', size + 'px');

          const wrap = doc.createElement('div');
          wrap.className = 'kmr-wrap';
          const bar = doc.createElement('div');
          bar.className = 'kmr-bar';
          const btn = (label, fn) => {
            const b = doc.createElement('button');
            b.className = 'kmr-btn';
            b.textContent = label;
            b.addEventListener('click', fn);
            bar.appendChild(b);
          };
          btn('A−', () => { size = Math.max(13, size - 1); applySize(); });
          btn('A＋', () => { size = Math.min(32, size + 1); applySize(); });
          btn('テーマ', () => { ti = (ti + 1) % themes.length; applyTheme(); });
          btn('元のページ', () => location.reload());

          const head = doc.createElement('h1');
          head.className = 'kmr-title';
          head.textContent = title;
          const meta = doc.createElement('div');
          meta.className = 'kmr-meta';
          meta.textContent = location.hostname + ' ・ 約' + minutes + '分で読めます';
          const body = doc.createElement('div');
          body.className = 'kmr-body';
          body.appendChild(clone);
          wrap.append(bar, head, meta, body);

          doc.querySelectorAll('link[rel~=stylesheet],style').forEach((e) => e.remove());
          doc.body.replaceChildren(wrap);
          doc.body.removeAttribute('class');
          doc.body.removeAttribute('style');
          root.setAttribute('data-kmr-reader', '1');
          applyTheme();
          applySize();
          window.scrollTo(0, 0);
          return 'ok';
        })()
        """;

    public const string ReaderCss = """
        html[data-kmr-reader]{--kmr-bg:#fbfbf9;--kmr-fg:#222;--kmr-sub:#777;--kmr-link:#1a5fb4;--kmr-bar:#ececE8;background:var(--kmr-bg)!important}
        html[data-kmr-reader][data-kmr-theme="sepia"]{--kmr-bg:#f4ecd8;--kmr-fg:#4a3b27;--kmr-sub:#8a7a63;--kmr-link:#8a4b08;--kmr-bar:#e6dcc3}
        html[data-kmr-reader][data-kmr-theme="dark"]{--kmr-bg:#1b1c1f;--kmr-fg:#d7d9dd;--kmr-sub:#8d9097;--kmr-link:#8ab4f8;--kmr-bar:#2b2c30}
        html[data-kmr-reader] body{margin:0!important;padding:0!important;background:var(--kmr-bg)!important;color:var(--kmr-fg)!important;
          font-family:"Yu Gothic UI","Hiragino Sans","Segoe UI",system-ui,sans-serif!important}
        html[data-kmr-reader] .kmr-wrap{max-width:720px;margin:0 auto;padding:28px 24px 140px}
        html[data-kmr-reader] .kmr-bar{display:flex;gap:8px;margin-bottom:48px}
        html[data-kmr-reader] .kmr-btn{all:unset;cursor:pointer;padding:6px 12px;border-radius:8px;background:var(--kmr-bar);color:var(--kmr-fg);font-size:13px}
        html[data-kmr-reader] .kmr-btn:hover{filter:brightness(.93)}
        html[data-kmr-reader] .kmr-title{font-size:calc(var(--kmr-size) * 1.7);line-height:1.35;margin:0 0 10px;font-weight:700;color:var(--kmr-fg)}
        html[data-kmr-reader] .kmr-meta{color:var(--kmr-sub);font-size:13px;margin-bottom:36px}
        html[data-kmr-reader] .kmr-body{font-size:var(--kmr-size);line-height:1.95;color:var(--kmr-fg)}
        html[data-kmr-reader] .kmr-body *{max-width:100%;color:inherit}
        html[data-kmr-reader] .kmr-body p{margin:0 0 1.3em}
        html[data-kmr-reader] .kmr-body h1,html[data-kmr-reader] .kmr-body h2,html[data-kmr-reader] .kmr-body h3{line-height:1.4;margin:1.8em 0 .7em}
        html[data-kmr-reader] .kmr-body a{color:var(--kmr-link)!important;text-decoration:underline}
        html[data-kmr-reader] .kmr-body img{height:auto;border-radius:8px;margin:1em 0;display:block}
        html[data-kmr-reader] .kmr-body blockquote{margin:1.2em 0;padding:.2em 1.2em;border-left:4px solid var(--kmr-bar)}
        html[data-kmr-reader] .kmr-body pre,html[data-kmr-reader] .kmr-body code{background:var(--kmr-bar);border-radius:6px;font-size:.9em}
        html[data-kmr-reader] .kmr-body pre{padding:12px;overflow:auto}
        html[data-kmr-reader] .kmr-body table{border-collapse:collapse}
        html[data-kmr-reader] .kmr-body td,html[data-kmr-reader] .kmr-body th{border:1px solid var(--kmr-bar);padding:6px 10px}
        """;

    // ---- 要素ブロッカー: クリックした要素のCSSセレクタを返す（Escでnull）----
    public const string PickerJs = """
        new Promise((resolve) => {
          const hl = document.createElement('div');
          Object.assign(hl.style, {
            position: 'fixed', zIndex: '2147483647', pointerEvents: 'none',
            outline: '2px solid #f28b82', background: 'rgba(242,139,130,.25)',
            borderRadius: '2px', left: '0', top: '0', width: '0', height: '0',
          });
          const tip = document.createElement('div');
          Object.assign(tip.style, {
            position: 'fixed', zIndex: '2147483647', left: '50%', top: '12px',
            transform: 'translateX(-50%)', background: '#202124', color: '#fff',
            font: '13px system-ui,sans-serif', padding: '8px 14px', borderRadius: '8px',
            boxShadow: '0 2px 12px rgba(0,0,0,.4)', pointerEvents: 'none',
          });
          tip.textContent = '消したい要素をクリック（Esc でキャンセル）';
          document.documentElement.append(hl, tip);

          const esc = (s) => CSS.escape(s);
          const selector = (el) => {
            const parts = [];
            let e = el;
            while (e && e.nodeType === 1 && e !== document.documentElement) {
              let p = e.tagName.toLowerCase();
              if (e.id && document.querySelectorAll('#' + esc(e.id)).length === 1) {
                parts.unshift('#' + esc(e.id));
                break;
              }
              const cls = [...e.classList].filter((c) => c.length < 30 && !/\d{3,}/.test(c)).slice(0, 2);
              if (cls.length) p += '.' + cls.map(esc).join('.');
              const sib = e.parentElement
                ? [...e.parentElement.children].filter((x) => x.tagName === e.tagName)
                : [];
              if (sib.length > 1) p += ':nth-of-type(' + (sib.indexOf(e) + 1) + ')';
              parts.unshift(p);
              const s = parts.join(' > ');
              try {
                if (document.querySelectorAll(s).length === 1) return s;
              } catch { /* 不正なセレクタは上位へ */ }
              e = e.parentElement;
            }
            return parts.join(' > ');
          };

          let cur = null;
          const move = (e) => {
            const el = document.elementFromPoint(e.clientX, e.clientY);
            if (!el || el === hl || el === tip) return;
            cur = el;
            const r = el.getBoundingClientRect();
            hl.style.left = r.left + 'px';
            hl.style.top = r.top + 'px';
            hl.style.width = r.width + 'px';
            hl.style.height = r.height + 'px';
          };
          const stop = (e) => { e.preventDefault(); e.stopPropagation(); e.stopImmediatePropagation(); };
          const done = (v) => {
            window.removeEventListener('mousemove', move, true);
            window.removeEventListener('click', click, true);
            window.removeEventListener('mousedown', stop, true);
            window.removeEventListener('mouseup', stop, true);
            window.removeEventListener('keydown', key, true);
            hl.remove();
            tip.remove();
            resolve(v);
          };
          const click = (e) => { stop(e); done(cur ? selector(cur) : null); };
          const key = (e) => { if (e.key === 'Escape') { stop(e); done(null); } };
          window.addEventListener('mousemove', move, true);
          window.addEventListener('click', click, true);
          window.addEventListener('mousedown', stop, true);
          window.addEventListener('mouseup', stop, true);
          window.addEventListener('keydown', key, true);
        })
        """;

    // ---- ピクチャーインピクチャー: 一番大きい動画をPiP化（再度実行で解除）----
    public const string PipJs = """
        (async function () {
          if (document.pictureInPictureElement) {
            await document.exitPictureInPicture();
            return 'exit';
          }
          const vs = [...document.querySelectorAll('video')]
            .filter((v) => v.readyState > 0 && !v.disablePictureInPicture);
          if (!vs.length) return 'none';
          vs.sort((a, b) => b.videoWidth * b.videoHeight - a.videoWidth * a.videoHeight);
          const v = vs.find((x) => !x.paused) || vs[0];
          await v.requestPictureInPicture();
          return 'ok';
        })()
        """;

    // ---- 各タブに最初に差し込むブリッジ ----
    // 内部ページ(https://komorebi.app/)でだけAPIを出す。外部サイトには何も公開しない。
    // ※ ホスト側でも送信元URLを必ず検証している。
    public const string TabBridgeJs = """
        (() => {
          if (location.origin !== 'https://komorebi.app') return;
          const wv = window.chrome && window.chrome.webview;
          if (!wv) return;
          const pending = new Map();
          let seq = 0;
          wv.addEventListener('message', (e) => {
            const m = e.data;
            if (m && m.id !== undefined && pending.has(m.id)) { pending.get(m.id)(m.r); pending.delete(m.id); }
          });
          const send = (c, p) => wv.postMessage({ c, p });
          const invoke = (c, ...a) => new Promise((res) => { const id = ++seq; pending.set(id, res); wv.postMessage({ c, a, id }); });
          if (/\/newtab\.html$/.test(location.pathname)) {
            window.komorebiNewtab = {
              data: () => invoke('k:newtab-data'),
              go: (text) => send('k:newtab-go', String(text)),
              cmd: (id) => send('k:newtab-cmd', String(id)),
            };
          }
          if (/\/focus\.html$/.test(location.pathname)) {
            window.komorebiFocus = { stop: () => send('k:focus-stop', null) };
          }
        })();
        """;
}
