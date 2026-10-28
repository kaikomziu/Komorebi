'use strict';

// WebView2 のメッセージ通信を、Electron版と同じ window.komorebi.{send, invoke, on} として見せる薄い橋渡し。
// これにより renderer.js / overlay.js は Electron版からほぼそのまま動く。
//   ページ → ホスト : { c: チャンネル, p: データ }            （send）
//                    { c: チャンネル, a: [引数...], id }       （invoke。結果は { id, r } で返る）
//   ホスト → ページ : { c: チャンネル, p: データ }
(() => {
  const wv = window.chrome && window.chrome.webview;
  const listeners = {};
  const pending = new Map();
  let seq = 0;

  if (wv) {
    wv.addEventListener('message', (e) => {
      const m = e.data;
      if (!m) return;
      if (m.id !== undefined && pending.has(m.id)) {
        pending.get(m.id)(m.r);
        pending.delete(m.id);
        return;
      }
      if (m.c && listeners[m.c]) for (const f of listeners[m.c]) f(m.p);
    });
  }

  window.komorebi = {
    send(c, p) {
      if (wv) wv.postMessage({ c, p: p === undefined ? null : p });
    },
    invoke(c, ...a) {
      return new Promise((resolve) => {
        if (!wv) return resolve(null);
        const id = ++seq;
        pending.set(id, resolve);
        wv.postMessage({ c, a, id });
      });
    },
    on(c, cb) {
      (listeners[c] = listeners[c] || []).push(cb);
    },
  };
})();
