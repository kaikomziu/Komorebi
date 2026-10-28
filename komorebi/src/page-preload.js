'use strict';

// 各タブ(Webページ)に読み込まれるプリロード。
// 外部サイトには何も公開しない。Komorebi 自身の内部ページ(新しいタブ・集中モード画面)でだけAPIを出す。
// ※ main 側でも送信元URLを必ず検証している。
const { contextBridge, ipcRenderer } = require('electron');

const href = location.href;
const isNewtab = href.startsWith('file:') && /\/newtab\.html(\?|#|$)/.test(href);
const isFocus = href.startsWith('file:') && /\/focus\.html(\?|#|$)/.test(href);

if (isNewtab) {
  contextBridge.exposeInMainWorld('komorebiNewtab', {
    data: () => ipcRenderer.invoke('k:newtab-data'),
    go: (text) => ipcRenderer.send('k:newtab-go', String(text)),
    cmd: (id) => ipcRenderer.send('k:newtab-cmd', String(id)),
  });
}

if (isFocus) {
  contextBridge.exposeInMainWorld('komorebiFocus', {
    stop: () => ipcRenderer.send('k:focus-stop'),
  });
}
