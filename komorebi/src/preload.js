'use strict';

// ブラウザ外枠(index.html)とオーバーレイ(overlay.html)専用のブリッジ。
// どちらもローカルのファイルしか読み込まないが、念のため "k:" で始まるチャンネルだけ通す。
const { contextBridge, ipcRenderer } = require('electron');

const ok = (ch) => typeof ch === 'string' && ch.startsWith('k:');

contextBridge.exposeInMainWorld('komorebi', {
  send(channel, payload) {
    if (ok(channel)) ipcRenderer.send(channel, payload);
  },
  invoke(channel, ...args) {
    return ok(channel) ? ipcRenderer.invoke(channel, ...args) : Promise.resolve(null);
  },
  on(channel, cb) {
    if (ok(channel)) ipcRenderer.on(channel, (_e, payload) => cb(payload));
  },
});
