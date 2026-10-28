'use strict';

const {
  app, BrowserWindow, WebContentsView, ipcMain, Menu, shell, session,
  clipboard, dialog, Notification, nativeImage,
} = require('electron');
const path = require('path');
const fs = require('fs');
const os = require('os');
const { pathToFileURL } = require('url');
const { Store, flushAll } = require('./lib/store');
const { isAdHost, cleanTrackingUrl } = require('./lib/blocklist');
const PS = require('./lib/pagescripts');

// ---------------------------------------------------------------------------
// 起動前の準備: 旧名称(deluxe-browser)のデータ引き継ぎ・単一インスタンス
// ---------------------------------------------------------------------------
const SELFTEST = process.argv.includes('--selftest');
if (SELFTEST) {
  // セルフテストは使い捨ての一時プロファイルで動かし、普段のデータには触れない
  const tmp = path.join(os.tmpdir(), 'komorebi-selftest');
  if (!process.env.KMR_KEEP) fs.rmSync(tmp, { recursive: true, force: true }); // 復元テストでは残す
  app.setPath('userData', tmp);
} else {
  app.setPath('userData', path.join(app.getPath('appData'), 'komorebi'));
  try {
    const oldDir = path.join(app.getPath('appData'), 'deluxe-browser');
    const newDir = app.getPath('userData');
    if (fs.existsSync(oldDir) && !fs.existsSync(newDir)) {
      fs.cpSync(oldDir, newDir, {
        recursive: true,
        filter: (src) => !/^(Singleton|lockfile)/i.test(path.basename(src)),
      });
    }
  } catch (e) {
    console.error('旧データの引き継ぎに失敗', e);
  }
  if (!app.requestSingleInstanceLock()) app.quit();
}

// ---------------------------------------------------------------------------
// 定数
// ---------------------------------------------------------------------------
const UI_DIR = path.join(__dirname, 'ui');
const NEWTAB_URL = pathToFileURL(path.join(UI_DIR, 'newtab.html')).href;
const FOCUS_URL = pathToFileURL(path.join(UI_DIR, 'focus.html')).href;
const UI_PRELOAD = path.join(__dirname, 'preload.js');
const PAGE_PRELOAD = path.join(__dirname, 'page-preload.js');
const DEV = process.argv.includes('--dev');
const BG = '#202124';

const WS_COLORS = ['#8ab4f8', '#f28b82', '#81c995', '#fdd663', '#c58af9', '#ff8bcb', '#78d9ec', '#fcad70'];

const ENGINES = {
  google: { name: 'Google', url: 'https://www.google.com/search?q=%s' },
  duckduckgo: { name: 'DuckDuckGo', url: 'https://duckduckgo.com/?q=%s' },
  bing: { name: 'Bing', url: 'https://www.bing.com/search?q=%s' },
  yahoo: { name: 'Yahoo! JAPAN', url: 'https://search.yahoo.co.jp/search?p=%s' },
  brave: { name: 'Brave Search', url: 'https://search.brave.com/search?q=%s' },
};

// アドレスバーで「yt ねこ」のように打つと、そのサイトで検索できる
const BANGS = {
  g: 'https://www.google.com/search?q=%s',
  ddg: 'https://duckduckgo.com/?q=%s',
  bing: 'https://www.bing.com/search?q=%s',
  yt: 'https://www.youtube.com/results?search_query=%s',
  gh: 'https://github.com/search?q=%s',
  w: 'https://ja.wikipedia.org/w/index.php?search=%s',
  wen: 'https://en.wikipedia.org/w/index.php?search=%s',
  map: 'https://www.google.com/maps/search/%s',
  img: 'https://www.google.com/search?tbm=isch&q=%s',
  amz: 'https://www.amazon.co.jp/s?k=%s',
  nico: 'https://www.nicovideo.jp/search/%s',
  x: 'https://x.com/search?q=%s',
  tr: 'https://translate.google.com/?sl=auto&tl=ja&text=%s&op=translate',
  so: 'https://stackoverflow.com/search?q=%s',
  mdn: 'https://developer.mozilla.org/ja/search?q=%s',
  npm: 'https://www.npmjs.com/search?q=%s',
  r: 'https://www.reddit.com/search/?q=%s',
};

const DEFAULT_SETTINGS = {
  adblock: true,
  httpsUpgrade: true,
  stripTracking: true,
  restoreSession: true,
  sleepMinutes: 15,
  tabLayout: 'top', // 'top' | 'side'
  searchEngine: 'google',
  forceDark: false,
  showBookmarksBar: true,
  opacity: 100,
  alwaysOnTop: false,
  sidebarCollapsed: false,
  focusSites: [
    'youtube.com', 'x.com', 'twitter.com', 'reddit.com', 'twitch.tv',
    'nicovideo.jp', 'tiktok.com', 'instagram.com', 'facebook.com',
  ],
  workspaces: [{ id: 'w1', name: 'メイン', color: WS_COLORS[0] }],
  currentWs: 'w1',
  bounds: null,
};

// ---------------------------------------------------------------------------
// 状態
// ---------------------------------------------------------------------------
let win = null;
let overlay = null;
let overlayReady = false;
let overlayQueue = [];
let overlayMode = null; // null | 'palette' | 'panel' | 'suggest' | 'prompt'
let panelName = 'history';
let suggestRect = { x: 0, y: 0, width: 0, height: 0 };
let pendingPrompt = null;
let promptSeq = 0;

let ui = { top: 84, left: 0, titleH: 40 }; // ブラウザ外枠が占める領域(px)
let uiReady = false;

const tabs = [];
let activeId = null;
let tabSeq = 0;
let split = null; // { a, b } 分割表示中のタブID
let currentWs = 'w1';
const closedStack = [];

let focusUntil = 0;
let focusTimer = null;

const downloads = [];
const dlItems = new Map();
let dlSeq = 0;

const upgraded = new Set(); // HTTPSへ自動で上げたホスト
const noUpgrade = new Set(); // HTTPSで失敗したのでHTTPに戻すホスト

let settings; let sites; let bookmarks; let history; let notes; let sessionStore;
let REAL_UA = '';
let MOBILE_UA = '';

// ---------------------------------------------------------------------------
// URLユーティリティ
// ---------------------------------------------------------------------------
const hostOf = (url) => {
  try {
    const u = new URL(url);
    return /^https?:$/.test(u.protocol) ? u.hostname : '';
  } catch {
    return '';
  }
};

function isLocalHost(host) {
  return !host.includes('.') || /^\d{1,3}(\.\d{1,3}){3}$/.test(host) ||
    /\.(local|localhost|test|internal|lan)$/i.test(host);
}

function looksLikeUrl(s) {
  if (/^(https?|file|about|chrome|data|view-source):/i.test(s)) return true;
  if (/^localhost(:\d+)?(\/.*)?$/i.test(s)) return true;
  if (/^\d{1,3}(\.\d{1,3}){3}(:\d+)?(\/.*)?$/.test(s)) return true;
  return /^[^\s./]+\.[^\s]{2,}(\/.*)?$/.test(s) && !s.includes(' ');
}

function searchUrl(q) {
  const eng = ENGINES[settings.data.searchEngine] || ENGINES.google;
  return eng.url.replace('%s', encodeURIComponent(q));
}

function toURL(input) {
  const s = String(input || '').trim();
  if (!s) return NEWTAB_URL;
  if (/^(https?|file|about|chrome|data|view-source):/i.test(s)) return s;
  const m = s.match(/^!?(\S+)\s+(.+)$/);
  if (m && BANGS[m[1].toLowerCase()]) {
    return BANGS[m[1].toLowerCase()].replace('%s', encodeURIComponent(m[2]));
  }
  if (looksLikeUrl(s)) {
    return (/^(localhost|\d{1,3}(\.\d{1,3}){3})/i.test(s) ? 'http://' : 'https://') + s;
  }
  return searchUrl(s);
}

function errorPage(url, desc) {
  const esc = (v) => String(v || '').replace(/[&<>"]/g, (c) => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;' }[c]));
  const html = `<!doctype html><meta charset="utf-8"><title>読み込みエラー</title>
  <style>
    body{font-family:system-ui,'Segoe UI','Yu Gothic UI',sans-serif;background:#202124;color:#e8eaed;
      display:flex;min-height:100vh;margin:0;align-items:center;justify-content:center}
    .box{max-width:480px;padding:32px}
    h1{font-size:20px;font-weight:600;margin:0 0 10px}
    p{color:#9aa0a6;line-height:1.7;font-size:14px}
    code{background:#303134;padding:2px 6px;border-radius:4px;word-break:break-all}
    button{margin-top:18px;background:#8ab4f8;border:0;color:#202124;font-weight:600;
      padding:8px 18px;border-radius:6px;cursor:pointer}
  </style>
  <div class="box"><h1>ページを表示できません</h1>
  <p><code>${esc(url)}</code> の読み込みに失敗しました。<br>${esc(desc)}</p>
  <button onclick="history.back()">戻る</button></div>`;
  return 'data:text/html;charset=utf-8,' + encodeURIComponent(html);
}

// ---------------------------------------------------------------------------
// あいまい検索（コマンドパレット・アドレスバー候補用）
// ---------------------------------------------------------------------------
function fuzzy(q, text) {
  q = q.toLowerCase();
  text = text.toLowerCase();
  if (!q) return 1;
  const i = text.indexOf(q);
  if (i >= 0) return 1000 - i - (text.length - q.length) * 0.1;
  let qi = 0; let score = 0; let last = -2;
  for (let ti = 0; ti < text.length && qi < q.length; ti++) {
    if (text[ti] === q[qi]) {
      score += ti === last + 1 ? 5 : 1;
      last = ti;
      qi++;
    }
  }
  return qi === q.length ? score : 0;
}

// 空白区切りの単語がすべて一致したときだけスコアを返す
function fuzzyAll(query, text) {
  const words = query.split(/\s+/).filter(Boolean);
  if (!words.length) return 1;
  let total = 0;
  for (const w of words) {
    const s = fuzzy(w, text);
    if (!s) return 0;
    total += s;
  }
  return total;
}

// ---------------------------------------------------------------------------
// タブ基本操作
// ---------------------------------------------------------------------------
const getTab = (id) => tabs.find((t) => t.id === id) || null;
const activeTab = () => getTab(activeId);
const tabByWcId = (wcId) => tabs.find((t) => t.view && t.view.webContents.id === wcId) || null;
const activeWC = () => {
  const t = activeTab();
  return t && t.view && !t.view.webContents.isDestroyed() ? t.view.webContents : null;
};

// 現在のワークスペースのタブ（ピン留めを先頭に並べた表示順）
function wsOrdered(ws = currentWs) {
  const l = tabs.filter((t) => t.ws === ws);
  return [...l.filter((t) => t.pinned), ...l.filter((t) => !t.pinned)];
}

function validWs(id) {
  return settings.data.workspaces.some((w) => w.id === id) ? id : settings.data.workspaces[0].id;
}

function createTab(url, opts = {}) {
  if (!win) return null;
  const tab = {
    id: ++tabSeq,
    ws: validWs(opts.ws || currentWs),
    url: url || NEWTAB_URL,
    title: opts.title || '新しいタブ',
    favicon: opts.favicon || null,
    view: null,
    pinned: !!opts.pinned,
    muted: false,
    audible: false,
    loading: false,
    canGoBack: false,
    canGoForward: false,
    blocked: 0,
    mobile: false,
    reader: false,
    darkKey: null,
    lastActive: Date.now(),
  };
  const afterIdx = opts.after ? tabs.findIndex((t) => t.id === opts.after) : -1;
  if (afterIdx >= 0) tabs.splice(afterIdx + 1, 0, tab);
  else tabs.push(tab);

  if (!opts.sleeping) wake(tab);
  if (opts.activate !== false) activateTab(tab.id);
  scheduleState();
  saveSessionSoon();
  return tab.id;
}

// 休止中のタブにWebContentsViewを与えて読み込む
function wake(tab) {
  if (tab.view || !win) return;
  const view = new WebContentsView({
    webPreferences: {
      contextIsolation: true,
      nodeIntegration: false,
      sandbox: true,
      preload: PAGE_PRELOAD,
    },
  });
  tab.view = view;
  wire(tab, view.webContents);
  win.contentView.addChildView(view);
  view.setVisible(false);
  bringOverlayToTop();
  if (tab.mobile) view.webContents.setUserAgent(MOBILE_UA);
  view.webContents.loadURL(tab.url);
}

function destroyView(tab) {
  if (!tab.view) return;
  try {
    win.contentView.removeChildView(tab.view);
    if (!tab.view.webContents.isDestroyed()) tab.view.webContents.close();
  } catch (e) {
    console.error('ビュー破棄に失敗', e);
  }
  tab.view = null;
  tab.loading = false;
  tab.audible = false;
}

function activateTab(id) {
  const t = getTab(id);
  if (!t) return;
  if (t.ws !== currentWs) {
    currentWs = t.ws;
    settings.set('currentWs', currentWs);
  }
  if (split && split.a !== id && split.b !== id) split = null;
  activeId = id;
  t.lastActive = Date.now();
  wake(t);
  if (split) {
    const o = getTab(split.a === id ? split.b : split.a);
    if (o) wake(o);
  }
  applyVisibility();
  layout();
  if (!overlayMode) focusActive();
  scheduleState();
  saveSessionSoon();
}

function closeTab(id, noRestore = false) {
  const t = getTab(id);
  if (!t) return;
  const ordered = wsOrdered(t.ws);
  const oi = ordered.indexOf(t);
  if (!noRestore && /^https?:/i.test(t.url)) {
    closedStack.push({ url: t.url, title: t.title, ws: t.ws, pinned: t.pinned });
    if (closedStack.length > 25) closedStack.shift();
  }
  destroyView(t);
  tabs.splice(tabs.indexOf(t), 1);
  if (split && (split.a === id || split.b === id)) split = null;

  if (activeId === id) {
    const rest = wsOrdered(t.ws);
    if (rest.length) activateTab(rest[Math.min(oi, rest.length - 1)].id);
    else {
      activeId = null;
      createTab(NEWTAB_URL, { ws: t.ws });
    }
  }
  scheduleState();
  saveSessionSoon();
}

function reopenClosed() {
  const c = closedStack.pop();
  if (!c) return toast('閉じたタブはありません');
  createTab(c.url, { ws: c.ws, pinned: c.pinned, title: c.title, after: activeId });
}

function sleepTab(t, force = false) {
  if (!t || !t.view) return false;
  const inSplit = split && (split.a === t.id || split.b === t.id);
  if (!force && (t.id === activeId || inSplit)) return false;
  if (t.id === activeId) {
    const o = wsOrdered().find((x) => x.id !== t.id);
    if (!o) return false;
    activateTab(o.id);
  }
  destroyView(t);
  scheduleState();
  return true;
}

function cycleTab(d) {
  const l = wsOrdered();
  if (l.length < 2) return;
  const i = l.findIndex((t) => t.id === activeId);
  activateTab(l[(i + d + l.length) % l.length].id);
}

function jumpTab(n) {
  const l = wsOrdered();
  if (!l.length) return;
  activateTab((n >= 9 ? l[l.length - 1] : l[n - 1] || l[l.length - 1]).id);
}

function navigateTab(tab, url) {
  tab.url = url;
  tab.reader = false;
  if (!tab.view) wake(tab);
  else tab.view.webContents.loadURL(url);
  scheduleState();
}

function navigate(url) {
  const t = activeTab();
  if (t) navigateTab(t, url);
  else createTab(url);
}

function toggleSplit() {
  if (split) {
    split = null;
    applyVisibility();
    layout();
    scheduleState();
    return toast('分割表示を終了しました');
  }
  const a = activeTab();
  if (!a) return;
  let other = wsOrdered().filter((t) => t.id !== a.id).sort((x, y) => y.lastActive - x.lastActive)[0];
  if (!other) other = getTab(createTab(NEWTAB_URL, { after: a.id, activate: false }));
  split = { a: a.id, b: other.id };
  wake(other);
  activateTab(a.id);
  toast('分割表示（クリックした側が操作対象になります）');
}

function openInSplit(url) {
  const a = activeTab();
  if (!a) return;
  const id = createTab(url, { after: a.id, activate: false });
  split = { a: a.id, b: id };
  activateTab(a.id);
}

// ---------------------------------------------------------------------------
// レイアウト
// ---------------------------------------------------------------------------
function applyVisibility() {
  for (const t of tabs) {
    if (!t.view) continue;
    const shown = t.ws === currentWs &&
      (t.id === activeId || (split && (t.id === split.a || t.id === split.b)));
    t.view.setVisible(!!shown);
  }
}

function layout() {
  if (!win) return;
  const { width: W, height: H } = win.getContentBounds();
  // 最小化・復元の途中などでサイズが 0 になることがある。そのときはビューを潰さず、復元後の再計算に任せる
  if (W < 100 || H < 100) return;
  const x0 = ui.left;
  const y0 = ui.top;
  const w = Math.max(0, W - x0);
  const h = Math.max(0, H - y0);
  const a = activeTab();
  if (!a) return;
  if (split) {
    const gap = 3;
    const half = Math.floor((w - gap) / 2);
    const A = getTab(split.a);
    const B = getTab(split.b);
    if (A && A.view) A.view.setBounds({ x: x0, y: y0, width: half, height: h });
    if (B && B.view) B.view.setBounds({ x: x0 + half + gap, y: y0, width: w - half - gap, height: h });
  } else if (a.view) {
    a.view.setBounds({ x: x0, y: y0, width: w, height: h });
  }
  layoutOverlay();
}

function focusActive() {
  const wc = activeWC();
  if (wc && !overlayMode) wc.focus();
}

function focusUI() {
  if (win) win.webContents.focus();
}

// ---------------------------------------------------------------------------
// サイトごとの見た目（ズーム・ダーク・非表示要素）
// ---------------------------------------------------------------------------
function isDark(tab) {
  const h = hostOf(tab.url);
  if (!h) return false;
  const d = sites.data.dark[h];
  return d === undefined ? settings.data.forceDark : d;
}

async function applyPageStyles(tab) {
  const wc = tab.view && tab.view.webContents;
  if (!wc || wc.isDestroyed()) return;
  const host = hostOf(tab.url);
  if (!host) return;
  try {
    wc.setZoomFactor(sites.data.zoom[host] || 1);
    tab.darkKey = null; // ページ遷移でCSSは消えているので再適用する
    if (!tab.reader && isDark(tab)) tab.darkKey = await wc.insertCSS(PS.DARK_CSS);
    const rules = sites.data.hidden[host];
    if (rules && rules.length) {
      await wc.insertCSS(rules.map((s) => `${s}{display:none!important}`).join('\n'));
    }
  } catch { /* ページ遷移中などは無視 */ }
}

async function toggleDark() {
  const a = activeTab();
  const wc = activeWC();
  const h = a && hostOf(a.url);
  if (!wc || !h) return toast('このページでは使えません');
  const cur = isDark(a);
  sites.data.dark[h] = !cur;
  sites.save();
  if (cur) {
    if (a.darkKey) wc.removeInsertedCSS(a.darkKey).catch(() => {});
    a.darkKey = null;
  } else {
    a.darkKey = await wc.insertCSS(PS.DARK_CSS);
  }
  toast(`${h}: ダークモード ${cur ? 'OFF' : 'ON'}`);
  scheduleState();
}

function adjustZoom(delta) {
  const a = activeTab();
  const wc = activeWC();
  if (!wc) return;
  const z = delta === 0 ? 1 : Math.round(Math.min(5, Math.max(0.25, wc.getZoomFactor() + delta)) * 100) / 100;
  wc.setZoomFactor(z);
  const h = hostOf(a.url);
  if (h) {
    if (z === 1) delete sites.data.zoom[h];
    else sites.data.zoom[h] = z;
    sites.save();
  }
  toast(`ズーム ${Math.round(z * 100)}%${h ? '（サイトごとに記憶）' : ''}`);
}

// ---------------------------------------------------------------------------
// WebContents のイベント配線
// ---------------------------------------------------------------------------
function wire(tab, wc) {
  const refresh = () => {
    if (wc.isDestroyed()) return;
    const u = wc.getURL();
    // エラーページ(data:)のURLは保存しない（元URLを保つ）
    if (u && !u.startsWith('data:text/html')) tab.url = u;
    tab.title = wc.getTitle() || tab.title;
    const nh = wc.navigationHistory;
    tab.canGoBack = nh.canGoBack();
    tab.canGoForward = nh.canGoForward();
    scheduleState();
    saveSessionSoon();
  };

  // ポップアップ(OAuth等)は本物のウィンドウ、それ以外は新しいタブで開く
  wc.setWindowOpenHandler(({ url, disposition, features }) => {
    if (['foreground-tab', 'background-tab'].includes(disposition) || (disposition === 'default' && !features)) {
      createTab(url, { after: tab.id, activate: disposition !== 'background-tab' });
      return { action: 'deny' };
    }
    return {
      action: 'allow',
      overrideBrowserWindowOptions: { autoHideMenuBar: true, backgroundColor: '#ffffff' },
    };
  });

  wc.on('page-title-updated', (_e, title) => {
    tab.title = title;
    const last = history.data[history.data.length - 1];
    if (last && last.url === tab.url) { last.title = title; history.save(); }
    scheduleState();
  });
  wc.on('page-favicon-updated', (_e, favicons) => {
    tab.favicon = (favicons && favicons[0]) || null;
    scheduleState();
  });
  wc.on('did-start-loading', () => { tab.loading = true; scheduleState(); });
  wc.on('did-stop-loading', () => { tab.loading = false; refresh(); });
  wc.on('did-start-navigation', (d) => {
    if (d.isMainFrame && !d.isSameDocument) {
      tab.blocked = 0;
      tab.reader = false;
    }
  });
  wc.on('did-navigate', () => { refresh(); recordHistory(tab); });
  wc.on('did-navigate-in-page', (_e, _u, isMainFrame) => { if (isMainFrame) refresh(); });
  wc.on('dom-ready', () => applyPageStyles(tab));
  wc.on('audio-state-changed', () => {
    tab.audible = wc.isCurrentlyAudible();
    scheduleState();
  });
  wc.on('found-in-page', (_e, r) => {
    if (tab.id === activeId && win) {
      win.webContents.send('k:find-result', { active: r.activeMatchOrdinal, total: r.matches });
    }
  });
  wc.on('did-fail-load', (_e, code, desc, validatedURL, isMainFrame) => {
    if (!isMainFrame || code === -3) return;
    const host = hostOf(validatedURL);
    // HTTPSへ自動で上げたサイトが開けなければHTTPに戻して再試行
    if (validatedURL.startsWith('https://') && upgraded.has(host) && !noUpgrade.has(host)) {
      noUpgrade.add(host);
      wc.loadURL('http://' + validatedURL.slice(8));
      return;
    }
    wc.loadURL(errorPage(validatedURL, desc));
  });
  wc.on('render-process-gone', () => { tab.title = '（クラッシュしました）'; tab.loading = false; scheduleState(); });
  wc.on('context-menu', (_e, params) => showPageMenu(tab, wc, params));
  wc.on('zoom-changed', (_e, dir) => { if (tab.id === activeId) adjustZoom(dir === 'in' ? 0.1 : -0.1); });
  // 分割表示中、クリックした側をアクティブにする
  wc.on('focus', () => {
    if (split && activeId !== tab.id && (split.a === tab.id || split.b === tab.id)) {
      activeId = tab.id;
      tab.lastActive = Date.now();
      scheduleState();
    }
  });
  attachShortcuts(wc);
}

// ---------------------------------------------------------------------------
// 履歴・ブックマーク
// ---------------------------------------------------------------------------
function recordHistory(tab) {
  if (!/^https?:/i.test(tab.url)) return;
  const last = history.data[history.data.length - 1];
  if (last && last.url === tab.url) return;
  history.data.push({ url: tab.url, title: tab.title, ts: Date.now() });
  if (history.data.length > 5000) history.data.splice(0, history.data.length - 5000);
  history.save();
  refreshPanel('history');
}

const isBookmarked = (url) => bookmarks.data.some((b) => b.url === url);

function toggleBookmark() {
  const a = activeTab();
  if (!a || !/^https?:/i.test(a.url)) return toast('このページはブックマークできません');
  const i = bookmarks.data.findIndex((b) => b.url === a.url);
  if (i >= 0) {
    bookmarks.data.splice(i, 1);
    toast('ブックマークを解除しました');
  } else {
    bookmarks.data.unshift({ url: a.url, title: a.title || a.url, favicon: a.favicon || null });
    toast('ブックマークに追加しました');
  }
  bookmarks.save();
  refreshPanel('bookmarks');
  scheduleState();
}

// ---------------------------------------------------------------------------
// ワークスペース
// ---------------------------------------------------------------------------
function switchWs(id) {
  if (id === currentWs || !settings.data.workspaces.some((w) => w.id === id)) return;
  currentWs = id;
  settings.set('currentWs', id);
  split = null;
  const cand = tabs.filter((t) => t.ws === id).sort((a, b) => b.lastActive - a.lastActive)[0];
  if (cand) activateTab(cand.id);
  else createTab(NEWTAB_URL);
  const w = settings.data.workspaces.find((x) => x.id === id);
  toast(`ワークスペース: ${w.name}`);
}

async function newWs() {
  const name = await askPrompt({ title: '新しいワークスペース', label: '名前（例: 仕事 / 勉強 / 趣味）', value: '', confirmLabel: '作成' });
  if (!name || !name.trim()) return;
  const list = settings.data.workspaces;
  const ws = { id: 'w' + Date.now(), name: name.trim().slice(0, 12), color: WS_COLORS[list.length % WS_COLORS.length] };
  list.push(ws);
  settings.save();
  switchWs(ws.id);
}

async function renameWs(id = currentWs) {
  const w = settings.data.workspaces.find((x) => x.id === id);
  if (!w) return;
  const name = await askPrompt({ title: 'ワークスペース名を変更', label: '新しい名前', value: w.name, confirmLabel: '変更' });
  if (!name || !name.trim()) return;
  w.name = name.trim().slice(0, 12);
  settings.save();
  scheduleState();
}

async function deleteWs(id = currentWs) {
  const list = settings.data.workspaces;
  if (list.length <= 1) return toast('最後のワークスペースは削除できません');
  const w = list.find((x) => x.id === id);
  if (!w) return;
  if (!(await askConfirm(`「${w.name}」を削除します。\nタブは別のワークスペースに移動します。`))) return;
  const dest = list.find((x) => x.id !== id).id;
  for (const t of tabs) if (t.ws === id) t.ws = dest;
  list.splice(list.indexOf(w), 1);
  settings.save();
  if (currentWs === id) switchWs(dest);
  scheduleState();
}

// ---------------------------------------------------------------------------
// 集中モード
// ---------------------------------------------------------------------------
function isFocusBlocked(url) {
  if (!focusUntil || Date.now() > focusUntil) return false;
  const h = hostOf(url);
  return !!h && settings.data.focusSites.some((s) => h === s || h.endsWith('.' + s));
}

const focusPageUrl = (url) => `${FOCUS_URL}?host=${encodeURIComponent(hostOf(url))}&until=${focusUntil}`;

function startFocus(min) {
  focusUntil = Date.now() + min * 60000;
  clearTimeout(focusTimer);
  focusTimer = setTimeout(endFocus, min * 60000);
  toast(`集中モード開始（${min}分）: 気が散るサイトをブロックします`);
  scheduleState();
}

function stopFocus() {
  if (!focusUntil) return;
  focusUntil = 0;
  clearTimeout(focusTimer);
  toast('集中モードを終了しました');
  scheduleState();
}

function endFocus() {
  focusUntil = 0;
  try {
    new Notification({ title: 'Komorebi', body: '集中モードが終了しました。おつかれさまでした！' }).show();
  } catch { /* 通知が使えない環境は無視 */ }
  toast('集中モードが終わりました');
  scheduleState();
}

async function startFocusCustom() {
  const v = await askPrompt({ title: '集中モード', label: '何分間？（1〜480）', value: '25', confirmLabel: '開始' });
  const n = parseInt(v, 10);
  if (n >= 1 && n <= 480) startFocus(n);
}

// ---------------------------------------------------------------------------
// ツール類（リーダー・要素ブロッカー・PiP・スクショ…）
// ---------------------------------------------------------------------------
async function toggleReader() {
  const a = activeTab();
  const wc = activeWC();
  if (!wc || !hostOf(a.url)) return toast('このページではリーダーモードを使えません');
  if (a.reader) {
    a.reader = false;
    wc.reload();
    return scheduleState();
  }
  try {
    if (a.darkKey) { wc.removeInsertedCSS(a.darkKey).catch(() => {}); a.darkKey = null; }
    await wc.insertCSS(PS.READER_CSS);
    const r = await wc.executeJavaScript(PS.READER_JS);
    if (r === 'fail') return toast('本文を検出できませんでした');
    a.reader = true;
    toast('リーダーモード（もう一度 F9 で元に戻ります）');
  } catch {
    toast('リーダーモードに失敗しました');
  }
  scheduleState();
}

async function runPicker() {
  const a = activeTab();
  const wc = activeWC();
  const h = a && hostOf(a.url);
  if (!wc || !h) return toast('このページでは使えません');
  toast('消したい要素をクリックしてください（Escでキャンセル）');
  wc.focus();
  let sel;
  try {
    sel = await wc.executeJavaScript(PS.PICKER_JS, true);
  } catch {
    return toast('要素の選択に失敗しました');
  }
  if (!sel) return toast('キャンセルしました');
  (sites.data.hidden[h] = sites.data.hidden[h] || []).push(sel);
  sites.save();
  try { await wc.insertCSS(`${sel}{display:none!important}`); } catch { /* 無視 */ }
  toast('要素を非表示にしました（次回以降もこのサイトで有効）');
}

function unhideAll() {
  const a = activeTab();
  const h = a && hostOf(a.url);
  if (!h || !(sites.data.hidden[h] || []).length) return toast('このサイトで非表示にした要素はありません');
  delete sites.data.hidden[h];
  sites.save();
  const wc = activeWC();
  if (wc) wc.reload();
  toast('非表示にした要素を元に戻しました');
}

async function togglePip() {
  const wc = activeWC();
  if (!wc) return;
  try {
    const r = await wc.executeJavaScript(PS.PIP_JS, true);
    if (r === 'none') toast('このページに再生できる動画がありません');
    else toast(r === 'exit' ? 'ピクチャーインピクチャーを終了しました' : 'ピクチャーインピクチャー');
  } catch {
    toast('この動画はピクチャーインピクチャーに対応していません');
  }
}

const stamp = () => {
  const d = new Date();
  const p = (n) => String(n).padStart(2, '0');
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`;
};

function saveShot(img, label) {
  const dir = path.join(app.getPath('pictures'), 'Komorebi');
  fs.mkdirSync(dir, { recursive: true });
  const file = path.join(dir, `komorebi-${stamp()}.png`);
  fs.writeFileSync(file, img.toPNG());
  clipboard.writeImage(img);
  toast(`${label}: 保存してクリップボードにコピーしました`);
  return file;
}

async function shotVisible() {
  const wc = activeWC();
  if (!wc) return;
  try {
    saveShot(await wc.capturePage(), 'スクリーンショット');
  } catch {
    // ウィンドウが最小化・非表示のときは撮影できない
    toast('スクリーンショットに失敗しました（ウィンドウが表示されていません）');
  }
}

// ページ全体（縦に長いページも1枚）をデバッガAPIで撮影する
async function shotFull() {
  const wc = activeWC();
  if (!wc) return;
  const dbg = wc.debugger;
  let attached = false;
  try {
    if (!dbg.isAttached()) { dbg.attach('1.3'); attached = true; }
    const m = await dbg.sendCommand('Page.getLayoutMetrics');
    const cs = m.cssContentSize || m.contentSize;
    const w = Math.ceil(cs.width);
    const h = Math.min(Math.ceil(cs.height), 16000);
    const r = await dbg.sendCommand('Page.captureScreenshot', {
      format: 'png', captureBeyondViewport: true, clip: { x: 0, y: 0, width: w, height: h, scale: 1 },
    });
    saveShot(nativeImage.createFromBuffer(Buffer.from(r.data, 'base64')), 'ページ全体');
  } catch {
    toast('ページ全体の撮影に失敗しました');
  } finally {
    if (attached) { try { dbg.detach(); } catch { /* 無視 */ } }
  }
}

function copyCleanUrl() {
  const a = activeTab();
  if (!a) return;
  const clean = cleanTrackingUrl(a.url);
  clipboard.writeText(clean);
  toast(clean === a.url ? 'URLをコピーしました' : 'トラッキング情報を除いたURLをコピーしました');
}

function toggleMobile() {
  const a = activeTab();
  const wc = activeWC();
  if (!wc) return;
  a.mobile = !a.mobile;
  wc.setUserAgent(a.mobile ? MOBILE_UA : REAL_UA);
  wc.reload();
  toast(a.mobile ? 'モバイル表示に切り替えました' : 'デスクトップ表示に戻しました');
  scheduleState();
}

function toggleMute(t) {
  if (!t || !t.view) return;
  t.muted = !t.muted;
  t.view.webContents.setAudioMuted(t.muted);
  scheduleState();
}

function toggleSiteAdblock() {
  const a = activeTab();
  const h = a && hostOf(a.url);
  if (!h) return toast('このページでは使えません');
  const list = sites.data.adblockOff;
  const i = list.indexOf(h);
  if (i >= 0) list.splice(i, 1);
  else list.push(h);
  sites.save();
  toast(`${h}: 広告ブロック ${i >= 0 ? 'ON' : 'OFF'}（再読み込みで反映）`);
  scheduleState();
}

async function clearBrowsingData() {
  if (!(await askConfirm('Cookie・キャッシュなどの閲覧データを削除します。\n（ログイン状態も解除されます）'))) return;
  await session.defaultSession.clearStorageData();
  await session.defaultSession.clearCache();
  toast('閲覧データを削除しました');
  const wc = activeWC();
  if (wc) wc.reload();
}

function addSelectionToNote(text, tab) {
  const n = {
    id: Date.now(),
    text: `${text}\n\n— ${tab.title}\n${tab.url}`,
    ts: Date.now(),
  };
  notes.data.unshift(n);
  notes.save();
  refreshPanel('notes');
  toast('選択範囲をメモに追加しました');
}

// ---------------------------------------------------------------------------
// トースト（外枠に一時メッセージ）
// ---------------------------------------------------------------------------
function toast(msg) {
  if (win && !win.isDestroyed()) win.webContents.send('k:toast', msg);
}

// ---------------------------------------------------------------------------
// 状態をUIへ送る
// ---------------------------------------------------------------------------
let stateTimer = null;
function scheduleState() {
  if (stateTimer) return;
  stateTimer = setTimeout(() => { stateTimer = null; sendState(); }, 16);
}

const pubTab = (t) => ({
  id: t.id,
  title: t.title,
  url: t.url,
  favicon: t.favicon,
  loading: t.loading,
  sleeping: !t.view,
  pinned: t.pinned,
  muted: t.muted,
  audible: t.audible,
  inSplit: !!(split && (split.a === t.id || split.b === t.id)),
});

function sendState() {
  if (!win || win.isDestroyed() || !uiReady) return;
  const a = activeTab();
  const host = a ? hostOf(a.url) : '';
  const s = settings.data;
  win.webContents.send('k:state', {
    tabs: wsOrdered().map(pubTab),
    activeId,
    workspaces: s.workspaces,
    currentWs,
    split: !!split,
    bookmarks: s.showBookmarksBar ? bookmarks.data : [],
    settings: {
      tabLayout: s.tabLayout,
      showBookmarksBar: s.showBookmarksBar,
      sidebarCollapsed: s.sidebarCollapsed,
      adblock: s.adblock,
      alwaysOnTop: s.alwaysOnTop,
    },
    info: a ? {
      url: a.url,
      host,
      blocked: a.blocked,
      bookmarked: isBookmarked(a.url),
      dark: isDark(a),
      mobile: a.mobile,
      reader: a.reader,
      loading: a.loading,
      canGoBack: a.canGoBack,
      canGoForward: a.canGoForward,
      adblockOff: !!host && sites.data.adblockOff.includes(host),
      hiddenCount: host ? (sites.data.hidden[host] || []).length : 0,
    } : null,
    downloadsActive: downloads.filter((d) => d.state === 'progressing').length,
    focusUntil,
  });
}

// ---------------------------------------------------------------------------
// セッション保存・復元
// ---------------------------------------------------------------------------
let sessTimer = null;
function saveSessionSoon() {
  clearTimeout(sessTimer);
  sessTimer = setTimeout(saveSession, 1500);
}
function saveSession() {
  if (!sessionStore) return;
  sessionStore.data.tabs = tabs.map((t) => ({
    url: t.url, title: t.title, favicon: t.favicon, ws: t.ws, pinned: t.pinned,
  }));
  sessionStore.data.active = tabs.findIndex((t) => t.id === activeId);
  sessionStore.save();
}

function restoreOrOpenFirst() {
  const s = sessionStore.data;
  if (settings.data.restoreSession && Array.isArray(s.tabs) && s.tabs.length) {
    for (const t of s.tabs) {
      createTab(t.url, { ws: t.ws, title: t.title, favicon: t.favicon, pinned: t.pinned, sleeping: true, activate: false });
    }
    const idx = s.active >= 0 && s.active < tabs.length ? s.active : 0;
    const want = tabs[idx];
    // 前回のワークスペースを優先（なければ復元したタブのワークスペース）
    const inCur = tabs.find((t) => t.ws === currentWs);
    activateTab((inCur && want.ws !== currentWs ? inCur : want).id);
  } else {
    createTab(NEWTAB_URL);
  }
}

// ---------------------------------------------------------------------------
// オーバーレイ（コマンドパレット・パネル・候補・入力ダイアログ）
// ---------------------------------------------------------------------------
function createOverlay() {
  overlay = new WebContentsView({
    webPreferences: { preload: UI_PRELOAD, contextIsolation: true, nodeIntegration: false, sandbox: true },
  });
  overlay.setBackgroundColor('#00000000');
  win.contentView.addChildView(overlay);
  overlay.setVisible(false);
  overlay.setBounds({ x: 0, y: 0, width: 0, height: 0 });
  overlay.webContents.loadFile(path.join(UI_DIR, 'overlay.html'));
  overlay.webContents.on('will-navigate', (e) => e.preventDefault());
  overlay.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  attachShortcuts(overlay.webContents);
}

function bringOverlayToTop() {
  if (win && overlay) win.contentView.addChildView(overlay);
}

function sendOverlay(channel, payload) {
  if (!overlay) return;
  if (!overlayReady) { overlayQueue.push([channel, payload]); return; }
  overlay.webContents.send(channel, payload);
}

function layoutOverlay() {
  if (!win || !overlay || !overlayMode) return;
  const { width: W, height: H } = win.getContentBounds();
  let b;
  if (overlayMode === 'panel') {
    const w = Math.min(420, W);
    b = { x: W - w, y: ui.top, width: w, height: Math.max(0, H - ui.top) };
  } else if (overlayMode === 'suggest') {
    b = suggestRect;
  } else {
    b = { x: 0, y: 0, width: W, height: H };
  }
  overlay.setBounds(b);
}

function showOverlay(mode, payload = {}, focus = true) {
  if (!overlay) return;
  overlayMode = mode;
  layoutOverlay();
  overlay.setVisible(true);
  bringOverlayToTop();
  sendOverlay('k:ov-show', { mode, ...payload });
  if (focus) overlay.webContents.focus();
}

function hideOverlay(refocus = true) {
  if (!overlayMode) return;
  overlayMode = null;
  overlay.setVisible(false);
  overlay.setBounds({ x: 0, y: 0, width: 0, height: 0 });
  sendOverlay('k:ov-hide', null);
  if (pendingPrompt) {
    const p = pendingPrompt;
    pendingPrompt = null;
    p.resolve(null);
  }
  if (refocus) focusActive();
}

function hideSuggest() {
  if (overlayMode === 'suggest') hideOverlay(false);
  omni.items = [];
  omni.sel = -1;
}

function refreshPanel(name) {
  if (overlayMode === 'panel' && panelName === name) sendOverlay('k:panel-refresh', name);
}

function openPanel(name) {
  if (overlayMode === 'panel' && panelName === name) return hideOverlay();
  panelName = name;
  showOverlay('panel', { panel: name });
}

function togglePalette() {
  if (overlayMode === 'palette') return hideOverlay();
  showOverlay('palette', {});
}

function askPrompt({ title, label = '', value = '', confirmLabel = 'OK', input = true }) {
  return new Promise((resolve) => {
    if (pendingPrompt) pendingPrompt.resolve(null);
    const id = ++promptSeq;
    pendingPrompt = { id, resolve };
    showOverlay('prompt', { id, title, label, value, confirmLabel, input });
  });
}

async function askConfirm(message) {
  const v = await askPrompt({ title: '確認', label: message, confirmLabel: '実行', input: false });
  return v !== null;
}

// ---------------------------------------------------------------------------
// アドレスバーの候補
// ---------------------------------------------------------------------------
const omni = { items: [], sel: -1, text: '' };

function buildSuggestions(text) {
  const q = text.trim();
  const out = [];
  const seen = new Set();
  const push = (it) => {
    const key = it.kind === 'tab' ? 'tab:' + it.tabId : it.url;
    if (seen.has(key)) return;
    seen.add(key);
    out.push(it);
  };
  const eng = ENGINES[settings.data.searchEngine] || ENGINES.google;
  const bang = q.match(/^!?(\S+)\s+(.+)$/);
  if (looksLikeUrl(q)) push({ kind: 'url', title: q, sub: 'このURLを開く', url: toURL(q) });
  else push({
    kind: 'search',
    title: q,
    sub: bang && BANGS[bang[1].toLowerCase()] ? `「${bang[1]}」ショートカットで検索` : `${eng.name} で検索`,
    url: toURL(q),
  });

  const scored = [];
  for (const t of tabs) {
    const s = fuzzyAll(q, `${t.title} ${t.url}`);
    if (s) scored.push({ s: s + 50, it: { kind: 'tab', title: t.title, sub: 'タブに切り替え', url: t.url, tabId: t.id } });
  }
  for (const b of bookmarks.data) {
    const s = fuzzyAll(q, `${b.title} ${b.url}`);
    if (s) scored.push({ s: s + 20, it: { kind: 'bookmark', title: b.title, sub: b.url, url: b.url } });
  }
  for (let i = history.data.length - 1; i >= 0 && i > history.data.length - 1500; i--) {
    const h = history.data[i];
    const s = fuzzyAll(q, `${h.title} ${h.url}`);
    if (s) scored.push({ s, it: { kind: 'history', title: h.title || h.url, sub: h.url, url: h.url } });
  }
  scored.sort((a, b) => b.s - a.s);
  for (const { it } of scored) {
    if (out.length >= 7) break;
    push(it);
  }
  return out;
}

function updateSuggest(text, rect) {
  omni.text = text;
  if (!text.trim() || (overlayMode && overlayMode !== 'suggest')) {
    hideSuggest();
    return;
  }
  omni.items = buildSuggestions(text);
  omni.sel = -1;
  suggestRect = {
    x: Math.round(rect.left),
    y: Math.round(rect.bottom + 4),
    width: Math.round(rect.width),
    height: Math.min(omni.items.length * 46 + 12, 420),
  };
  showOverlay('suggest', { items: omni.items, sel: -1 }, false);
}

function pickSuggest(it) {
  hideSuggest();
  if (it.kind === 'tab') activateTab(it.tabId);
  else navigate(it.url);
  if (win) win.webContents.send('k:omni-done');
}

// ---------------------------------------------------------------------------
// コマンドパレットの検索
// ---------------------------------------------------------------------------
function evalMath(q) {
  if (!/^[\d\s+\-*/().%^]+$/.test(q) || !/[+\-*/%^]/.test(q) || !/\d/.test(q)) return null;
  try {
    // 許可した文字だけなので Function で評価しても安全
    const v = Function(`"use strict"; return (${q.replace(/\^/g, '**')})`)();
    return Number.isFinite(v) ? v : null;
  } catch {
    return null;
  }
}

function paletteQuery(raw) {
  let q = String(raw || '').trim();
  let mode = 'all';
  const prefix = { '>': 'cmd', '@': 'tab', '*': 'bm', '?': 'hist' }[q[0]];
  if (prefix) { mode = prefix; q = q.slice(1).trim(); }

  const items = [];
  const want = (m) => mode === 'all' || mode === m;

  if (mode === 'all') {
    const v = evalMath(q);
    if (v !== null) {
      items.push({ s: 5000, kind: 'calc', title: `= ${v}`, sub: 'Enter で結果をコピー', icon: '🧮', value: String(v) });
    }
  }
  if (want('tab')) {
    for (const t of tabs) {
      const ws = settings.data.workspaces.find((w) => w.id === t.ws);
      const s = q ? fuzzyAll(q, `${t.title} ${t.url}`) : 1;
      if (s) items.push({ s: s + 60, kind: 'tab', title: t.title, sub: `タブ · ${ws ? ws.name : ''} · ${t.url}`, icon: '🗂', tabId: t.id });
    }
  }
  if (want('cmd')) {
    for (const c of COMMANDS) {
      const s = q ? fuzzyAll(q, `${c.label} ${c.id}`) : 1;
      if (s) items.push({ s: q ? s + 40 : 0.5, kind: 'cmd', title: c.label, sub: c.keys || 'コマンド', icon: '⌘', id: c.id });
    }
    for (const w of settings.data.workspaces) {
      const label = `ワークスペースを切り替え: ${w.name}`;
      const s = q ? fuzzyAll(q, label) : 0;
      if (s) items.push({ s: s + 40, kind: 'cmd', title: label, sub: 'ワークスペース', icon: '🎨', id: 'ws.switch:' + w.id });
    }
  }
  if (q && want('bm')) {
    for (const b of bookmarks.data) {
      const s = fuzzyAll(q, `${b.title} ${b.url}`);
      if (s) items.push({ s: s + 30, kind: 'url', title: b.title, sub: b.url, icon: '★', url: b.url });
    }
  }
  if (q && want('hist')) {
    for (let i = history.data.length - 1; i >= 0 && i > history.data.length - 2000; i--) {
      const h = history.data[i];
      const s = fuzzyAll(q, `${h.title} ${h.url}`);
      if (s) items.push({ s, kind: 'url', title: h.title || h.url, sub: h.url, icon: '🕘', url: h.url });
    }
  }
  if (q && mode === 'all') {
    const eng = ENGINES[settings.data.searchEngine] || ENGINES.google;
    items.push({
      s: 1, kind: 'url', icon: looksLikeUrl(q) ? '🌐' : '🔎',
      title: looksLikeUrl(q) ? q : `${eng.name} で「${q}」を検索`,
      sub: looksLikeUrl(q) ? 'このURLを開く' : 'Web検索', url: toURL(q),
    });
  }
  items.sort((a, b) => b.s - a.s);
  const seen = new Set();
  const out = [];
  for (const it of items) {
    const key = it.kind === 'tab' ? 'tab' + it.tabId : it.kind === 'cmd' ? it.id : it.kind + (it.url || it.title);
    if (seen.has(key)) continue;
    seen.add(key);
    out.push(it);
    if (out.length >= 40) break;
  }
  return out;
}

function runPaletteItem(it, mod = {}) {
  hideOverlay(false);
  switch (it.kind) {
    case 'cmd': runCommand(it.id); break;
    case 'tab': activateTab(it.tabId); break;
    case 'calc': clipboard.writeText(it.value); toast('計算結果をコピーしました'); break;
    case 'url':
      if (mod.ctrl) createTab(it.url, { after: activeId });
      else navigate(it.url);
      break;
    default: break;
  }
  // パレットを閉じたらページへフォーカスを戻す（UIにフォーカスを渡すコマンドは除く）
  const keepUI = it.kind === 'cmd' && ['focus.address', 'view.find'].includes(it.id);
  if (!overlayMode && !keepUI) focusActive();
}

// ---------------------------------------------------------------------------
// コマンド一覧（パレット・メニュー・ショートカットの共通定義）
// ---------------------------------------------------------------------------
const COMMANDS = [];
const cmd = (id, label, keys, run) => COMMANDS.push({ id, label, keys, run });
const act = (fn) => { const wc = activeWC(); if (wc) fn(wc); };
const toggleSetting = (key, label) => () => {
  settings.data[key] = !settings.data[key];
  settings.save();
  applySetting(key);
  toast(`${label}: ${settings.data[key] ? 'ON' : 'OFF'}`);
  scheduleState();
};

// タブ
cmd('tab.new', '新しいタブ', 'Ctrl+T', () => createTab(NEWTAB_URL, { after: activeId }));
cmd('tab.close', 'タブを閉じる', 'Ctrl+W', () => closeTab(activeId));
cmd('tab.restore', '閉じたタブを開く', 'Ctrl+Shift+T', reopenClosed);
cmd('tab.duplicate', 'タブを複製', '', () => { const a = activeTab(); if (a) createTab(a.url, { after: a.id }); });
cmd('tab.pin', 'タブをピン留め / 解除', '', () => { const a = activeTab(); if (a) { a.pinned = !a.pinned; scheduleState(); saveSessionSoon(); } });
cmd('tab.mute', 'タブをミュート / 解除', 'Ctrl+Shift+M', () => toggleMute(activeTab()));
cmd('tab.sleep', 'このタブを休止（メモリ解放）', '', () => sleepTab(activeTab(), true));
cmd('tab.sleepOthers', '他のタブをすべて休止', '', () => { let n = 0; for (const t of tabs) if (sleepTab(t)) n++; toast(`${n}個のタブを休止しました`); });
cmd('tab.closeOthers', '他のタブを閉じる', '', () => { for (const t of [...tabs]) if (t.id !== activeId && t.ws === currentWs && !t.pinned) closeTab(t.id); });
cmd('tab.next', '次のタブ', 'Ctrl+Tab', () => cycleTab(1));
cmd('tab.prev', '前のタブ', 'Ctrl+Shift+Tab', () => cycleTab(-1));
// ナビゲーション
cmd('nav.back', '戻る', 'Alt+←', () => act((wc) => wc.navigationHistory.goBack()));
cmd('nav.forward', '進む', 'Alt+→', () => act((wc) => wc.navigationHistory.goForward()));
cmd('nav.reload', '再読み込み', 'Ctrl+R', () => act((wc) => wc.reload()));
cmd('nav.hardReload', 'キャッシュを無視して再読み込み', 'Ctrl+Shift+R', () => act((wc) => wc.reloadIgnoringCache()));
cmd('nav.stop', '読み込みを停止', '', () => act((wc) => wc.stop()));
cmd('nav.home', 'ホーム（新しいタブページ）へ', 'Alt+Home', () => navigate(NEWTAB_URL));
cmd('focus.address', 'アドレスバーに移動', 'Ctrl+L', () => { focusUI(); win.webContents.send('k:focus-address'); });
cmd('bookmark.toggle', 'このページをブックマーク / 解除', 'Ctrl+D', toggleBookmark);
// 表示
cmd('view.palette', 'コマンドパレットを開く', 'Ctrl+K', togglePalette);
cmd('view.find', 'ページ内検索', 'Ctrl+F', () => { focusUI(); win.webContents.send('k:find-open'); });
cmd('view.split', '分割表示 ON / OFF', 'Alt+S', toggleSplit);
cmd('view.layout', 'タブの位置を切り替え（上 / 縦）', 'Ctrl+Shift+L', () => {
  settings.data.tabLayout = settings.data.tabLayout === 'top' ? 'side' : 'top';
  settings.save();
  scheduleState();
});
cmd('view.bookmarksBar', 'ブックマークバーの表示切替', 'Ctrl+Shift+B', toggleSetting('showBookmarksBar', 'ブックマークバー'));
cmd('view.fullscreen', '全画面表示', 'F11', () => win.setFullScreen(!win.isFullScreen()));
cmd('view.zoomIn', '拡大', 'Ctrl+Plus', () => adjustZoom(0.1));
cmd('view.zoomOut', '縮小', 'Ctrl+Minus', () => adjustZoom(-0.1));
cmd('view.zoomReset', 'ズームをリセット', 'Ctrl+0', () => adjustZoom(0));
// ツール
cmd('tools.reader', 'リーダーモード', 'F9', toggleReader);
cmd('tools.dark', 'このサイトを強制ダークモード', 'Ctrl+Shift+D', toggleDark);
cmd('tools.picker', '要素ブロッカー（クリックした要素を消す）', 'Ctrl+Shift+X', runPicker);
cmd('tools.unhide', 'このサイトの非表示要素をすべて元に戻す', '', unhideAll);
cmd('tools.pip', 'ピクチャーインピクチャー', 'Ctrl+Shift+P', togglePip);
cmd('tools.shot', 'スクリーンショット（表示範囲）', 'Ctrl+Shift+S', shotVisible);
cmd('tools.shotFull', 'スクリーンショット（ページ全体）', '', shotFull);
cmd('tools.copyClean', 'トラッキングを除いたURLをコピー', 'Ctrl+Shift+C', copyCleanUrl);
cmd('tools.translate', 'このページを日本語に翻訳', '', () => { const a = activeTab(); if (a && hostOf(a.url)) createTab(`https://translate.google.com/translate?sl=auto&tl=ja&u=${encodeURIComponent(a.url)}`, { after: a.id }); });
cmd('tools.mobile', 'モバイル表示 / デスクトップ表示', '', toggleMobile);
cmd('tools.adblockSite', 'このサイトの広告ブロックを ON / OFF', '', toggleSiteAdblock);
cmd('tools.devtools', 'デベロッパーツール', 'F12', () => act((wc) => wc.toggleDevTools()));
cmd('tools.external', 'このページを既定のブラウザで開く', '', () => { const a = activeTab(); if (a && hostOf(a.url)) shell.openExternal(a.url); });
// パネル
cmd('panel.history', '履歴', 'Ctrl+H', () => openPanel('history'));
cmd('panel.downloads', 'ダウンロード', 'Ctrl+J', () => openPanel('downloads'));
cmd('panel.bookmarks', 'ブックマーク', 'Ctrl+Shift+O', () => openPanel('bookmarks'));
cmd('panel.notes', 'クイックメモ', 'Ctrl+Shift+N', () => openPanel('notes'));
cmd('panel.settings', '設定', 'Ctrl+,', () => openPanel('settings'));
cmd('panel.help', 'コマンド・ショートカット一覧', 'F1', () => openPanel('help'));
// 集中モード
cmd('focus.start25', '集中モード 25分', '', () => startFocus(25));
cmd('focus.start50', '集中モード 50分', '', () => startFocus(50));
cmd('focus.startCustom', '集中モード（時間を指定）', 'Ctrl+Shift+F', startFocusCustom);
cmd('focus.stop', '集中モードを終了', '', stopFocus);
// ワークスペース
cmd('ws.new', 'ワークスペースを作成', '', newWs);
cmd('ws.rename', 'ワークスペース名を変更', '', () => renameWs());
cmd('ws.delete', 'ワークスペースを削除', '', () => deleteWs());
// ウィンドウ
cmd('win.top', 'ウィンドウを常に最前面にする ON / OFF', '', toggleSetting('alwaysOnTop', '常に最前面'));
for (const o of [100, 80, 60, 40]) {
  cmd(`win.opacity${o}`, `ウィンドウの透明度 ${o}%`, '', () => { settings.data.opacity = o; settings.save(); applySetting('opacity'); });
}
// 機能のON/OFF
cmd('set.adblock', '広告ブロック（全体）ON / OFF', '', toggleSetting('adblock', '広告ブロック'));
cmd('set.httpsUpgrade', 'HTTPS自動アップグレード ON / OFF', '', toggleSetting('httpsUpgrade', 'HTTPS自動アップグレード'));
cmd('set.forceDark', '全サイト強制ダークモード ON / OFF', '', toggleSetting('forceDark', '全サイト強制ダーク'));
// データ・アプリ
cmd('data.clear', '閲覧データ（Cookie・キャッシュ）を削除', '', clearBrowsingData);
cmd('data.clearHistory', '履歴をすべて削除', '', async () => { if (await askConfirm('閲覧履歴をすべて削除します。')) { history.data.length = 0; history.save(); refreshPanel('history'); toast('履歴を削除しました'); } });
cmd('app.about', 'Komorebi について', '', () => {
  dialog.showMessageBox(win, {
    type: 'info', title: 'Komorebi', message: `Komorebi ${app.getVersion()}`,
    detail: `Chromium ${process.versions.chrome}\nElectron ${process.versions.electron}\nNode ${process.versions.node}`,
  });
});
cmd('app.quit', 'Komorebi を終了', 'Ctrl+Shift+Q', () => app.quit());

// ショートカット表を作る（"Ctrl+Shift+T" → "ctrl+shift+t"）
const MOD_ORDER = ['ctrl', 'alt', 'shift', 'meta'];
function normKeys(str) {
  const parts = str.toLowerCase().split('+');
  const key = parts.pop();
  const mods = MOD_ORDER.filter((m) => parts.includes(m));
  return [...mods, { '←': 'left', '→': 'right' }[key] || key].join('+');
}
const SHORTCUTS = {};
for (const c of COMMANDS) if (c.keys) SHORTCUTS[normKeys(c.keys)] = c.id;
Object.assign(SHORTCUTS, { 'ctrl+\\': 'view.split', 'ctrl+¥': 'view.split', 'ctrl+shift+f': 'focus.startCustom' });

function runCommand(id) {
  if (id.startsWith('ws.switch:')) return switchWs(id.slice(10));
  const c = COMMANDS.find((x) => x.id === id);
  if (c) c.run();
}

function eventKey(i) {
  const map = { ' ': 'space', arrowleft: 'left', arrowright: 'right', arrowup: 'up', arrowdown: 'down', '+': 'plus', '=': 'plus', '-': 'minus', '＝': 'plus' };
  let k = (i.key || '').toLowerCase();
  if (!k) return '';
  k = map[k] || k;
  const m = [];
  if (i.control) m.push('ctrl');
  if (i.alt) m.push('alt');
  if (i.shift && k !== 'plus') m.push('shift');
  if (i.meta) m.push('meta');
  m.push(k);
  return m.join('+');
}

// どのWebContentsにフォーカスがあってもショートカットが効くようにする
function attachShortcuts(wc) {
  wc.on('before-input-event', (event, input) => {
    if (input.type !== 'keyDown') return;
    const k = eventKey(input);
    const num = k.match(/^(ctrl|alt)\+([1-9])$/);
    if (num) {
      event.preventDefault();
      if (num[1] === 'ctrl') jumpTab(+num[2]);
      else {
        const w = settings.data.workspaces[+num[2] - 1];
        if (w) switchWs(w.id);
      }
      return;
    }
    const id = SHORTCUTS[k];
    if (id) {
      event.preventDefault();
      runCommand(id);
    }
  });
}

// ---------------------------------------------------------------------------
// 設定の反映
// ---------------------------------------------------------------------------
function applySetting(key) {
  if (!win) return;
  if (key === 'opacity') win.setOpacity(Math.max(0.3, settings.data.opacity / 100));
  if (key === 'alwaysOnTop') win.setAlwaysOnTop(!!settings.data.alwaysOnTop);
  if (key === 'forceDark') for (const t of tabs) if (t.view) applyPageStyles(t).then(() => {});
  scheduleState();
}

// ---------------------------------------------------------------------------
// ネイティブメニュー
// ---------------------------------------------------------------------------
function cmdItem(id, extra = {}) {
  const c = COMMANDS.find((x) => x.id === id);
  return { label: c.keys ? `${c.label}\t${c.keys}` : c.label, click: () => c.run(), ...extra };
}

function showPageMenu(tab, wc, p) {
  const t = [];
  const nh = wc.navigationHistory;
  if (p.linkURL) {
    t.push({ label: 'リンクを新しいタブで開く', click: () => createTab(p.linkURL, { after: tab.id }) });
    t.push({ label: 'リンクを横に並べて開く（分割表示）', click: () => openInSplit(p.linkURL) });
    t.push({ label: 'リンクのURLをコピー', click: () => clipboard.writeText(cleanTrackingUrl(p.linkURL)) });
    t.push({ type: 'separator' });
  }
  if (p.mediaType === 'image' && p.srcURL) {
    t.push({ label: '画像を新しいタブで開く', click: () => createTab(p.srcURL, { after: tab.id }) });
    t.push({ label: '画像をコピー', click: () => wc.copyImageAt(p.x, p.y) });
    t.push({ label: '画像を保存', click: () => wc.downloadURL(p.srcURL) });
    t.push({ type: 'separator' });
  }
  if (p.isEditable) {
    t.push({ role: 'undo', label: '元に戻す' }, { role: 'redo', label: 'やり直し' }, { type: 'separator' });
    t.push({ role: 'cut', label: '切り取り' }, { role: 'copy', label: 'コピー' }, { role: 'paste', label: '貼り付け' }, { role: 'selectAll', label: 'すべて選択' });
  } else if (p.selectionText) {
    const sel = p.selectionText.trim();
    t.push({ role: 'copy', label: 'コピー' });
    t.push({ label: `「${sel.slice(0, 16)}」を検索`, click: () => createTab(searchUrl(sel), { after: tab.id }) });
    t.push({ label: '選択範囲をメモに追加', click: () => addSelectionToNote(sel, tab) });
    if (evalMath(sel) !== null) t.push({ label: `計算結果をコピー（= ${evalMath(sel)}）`, click: () => clipboard.writeText(String(evalMath(sel))) });
  } else {
    t.push({ label: '戻る', enabled: nh.canGoBack(), click: () => nh.goBack() });
    t.push({ label: '進む', enabled: nh.canGoForward(), click: () => nh.goForward() });
    t.push({ label: '再読み込み', click: () => wc.reload() });
    t.push({ type: 'separator' });
    t.push(cmdItem('tools.reader'), cmdItem('tools.picker'), cmdItem('tools.shot'), cmdItem('view.split'));
  }
  t.push({ type: 'separator' });
  t.push({ label: '検証', click: () => wc.inspectElement(p.x, p.y) });
  Menu.buildFromTemplate(t).popup({ window: win });
}

function showTabMenu(id) {
  const t = getTab(id);
  if (!t) return;
  const others = settings.data.workspaces.filter((w) => w.id !== t.ws);
  const tpl = [
    { label: '新しいタブ', click: () => createTab(NEWTAB_URL, { after: t.id }) },
    { label: 'タブを複製', click: () => createTab(t.url, { after: t.id }) },
    { type: 'separator' },
    { label: t.pinned ? 'ピン留めを解除' : 'ピン留め', click: () => { t.pinned = !t.pinned; scheduleState(); saveSessionSoon(); } },
    { label: t.muted ? 'ミュート解除' : 'ミュート', enabled: !!t.view, click: () => toggleMute(t) },
    { label: 'このタブを休止', enabled: !!t.view, click: () => sleepTab(t, true) },
    { label: 'このタブと横に並べる（分割表示）', enabled: t.id !== activeId && !!activeTab(), click: () => { split = { a: activeId, b: t.id }; wake(t); activateTab(activeId); } },
  ];
  if (others.length) {
    tpl.push({
      label: 'ワークスペースへ移動',
      submenu: others.map((w) => ({
        label: w.name,
        click: () => {
          const wasActive = activeId === t.id;
          t.ws = w.id;
          if (wasActive) {
            const rest = wsOrdered(currentWs);
            if (rest.length) activateTab(rest[0].id);
            else createTab(NEWTAB_URL);
          }
          scheduleState();
          saveSessionSoon();
        },
      })),
    });
  }
  tpl.push({ type: 'separator' });
  tpl.push({ label: '他のタブを閉じる', click: () => { for (const x of [...tabs]) if (x.id !== t.id && x.ws === t.ws && !x.pinned) closeTab(x.id); } });
  tpl.push({ label: 'タブを閉じる', click: () => closeTab(t.id) });
  Menu.buildFromTemplate(tpl).popup({ window: win });
}

function showMainMenu() {
  const wsItems = settings.data.workspaces.map((w, i) => ({
    label: w.name, type: 'radio', checked: w.id === currentWs, click: () => switchWs(w.id), accelerator: undefined,
  }));
  const tpl = [
    cmdItem('tab.new'), cmdItem('tab.restore'), cmdItem('view.split'),
    { type: 'separator' },
    { label: 'ワークスペース', submenu: [...wsItems, { type: 'separator' }, cmdItem('ws.new'), cmdItem('ws.rename'), cmdItem('ws.delete')] },
    { type: 'separator' },
    cmdItem('panel.downloads'), cmdItem('panel.history'), cmdItem('panel.bookmarks'), cmdItem('panel.notes'),
    { type: 'separator' },
    {
      label: 'ツール',
      submenu: [
        cmdItem('tools.reader'), cmdItem('tools.dark'), cmdItem('tools.picker'), cmdItem('tools.unhide'), cmdItem('tools.pip'),
        { type: 'separator' },
        cmdItem('tools.shot'), cmdItem('tools.shotFull'), cmdItem('tools.copyClean'),
        { type: 'separator' },
        cmdItem('tools.translate'), cmdItem('tools.mobile'), cmdItem('tools.external'), cmdItem('tools.devtools'),
      ],
    },
    {
      label: 'ズーム',
      submenu: [cmdItem('view.zoomIn'), cmdItem('view.zoomOut'), cmdItem('view.zoomReset')],
    },
    {
      label: '集中モード',
      submenu: [cmdItem('focus.start25'), cmdItem('focus.start50'), cmdItem('focus.startCustom'), cmdItem('focus.stop')],
    },
    {
      label: 'ウィンドウ',
      submenu: [
        { label: '常に最前面', type: 'checkbox', checked: !!settings.data.alwaysOnTop, click: () => runCommand('win.top') },
        { type: 'separator' },
        ...[100, 80, 60, 40].map((o) => ({ label: `透明度 ${o}%`, type: 'radio', checked: settings.data.opacity === o, click: () => runCommand('win.opacity' + o) })),
        { type: 'separator' },
        cmdItem('view.fullscreen'),
      ],
    },
    { type: 'separator' },
    cmdItem('view.layout'), cmdItem('view.bookmarksBar'),
    cmdItem('panel.settings'), cmdItem('panel.help'),
    { type: 'separator' },
    cmdItem('data.clear'), cmdItem('app.about'), cmdItem('app.quit'),
  ];
  Menu.buildFromTemplate(tpl).popup({ window: win });
}

function showShieldMenu() {
  const a = activeTab();
  if (!a) return;
  const h = hostOf(a.url);
  const hidden = h ? (sites.data.hidden[h] || []).length : 0;
  const tpl = [
    { label: `このページでブロックした広告・トラッカー: ${a.blocked}件`, enabled: false },
    { type: 'separator' },
    { label: 'このサイトで広告ブロック', type: 'checkbox', checked: !(h && sites.data.adblockOff.includes(h)), enabled: !!h, click: toggleSiteAdblock },
    { label: '広告ブロック（全サイト）', type: 'checkbox', checked: !!settings.data.adblock, click: () => runCommand('set.adblock') },
    { type: 'separator' },
    cmdItem('tools.picker'),
    { label: `非表示にした要素を元に戻す（${hidden}件）`, enabled: hidden > 0, click: unhideAll },
  ];
  Menu.buildFromTemplate(tpl).popup({ window: win });
}

// ---------------------------------------------------------------------------
// ダウンロード
// ---------------------------------------------------------------------------
function uniquePath(dir, name) {
  const ext = path.extname(name);
  const base = path.basename(name, ext);
  let p = path.join(dir, name);
  for (let i = 1; fs.existsSync(p); i++) p = path.join(dir, `${base} (${i})${ext}`);
  return p;
}

const pubDl = (d) => ({ id: d.id, name: d.name, url: d.url, total: d.total, received: d.received, state: d.state, path: d.path, ts: d.ts });
let dlTimer = null;
function pushDownloads() {
  if (dlTimer) return;
  dlTimer = setTimeout(() => { dlTimer = null; refreshPanel('downloads'); scheduleState(); }, 120);
}

function setupDownloads() {
  session.defaultSession.on('will-download', (_e, item, srcWc) => {
    // ダウンロードのためだけに開かれた空のタブは自動で閉じる（復元対象にもしない）
    const srcTab = srcWc && tabByWcId(srcWc.id);
    if (srcTab && !srcWc.getURL() && tabs.length > 1) setImmediate(() => closeTab(srcTab.id, true));
    const id = ++dlSeq;
    const p = uniquePath(app.getPath('downloads'), item.getFilename());
    item.setSavePath(p);
    const d = { id, name: path.basename(p), url: item.getURL(), total: item.getTotalBytes(), received: 0, state: 'progressing', path: p, ts: Date.now() };
    downloads.unshift(d);
    dlItems.set(id, item);
    item.on('updated', (_ev, state) => {
      d.received = item.getReceivedBytes();
      d.total = item.getTotalBytes();
      d.state = state === 'interrupted' ? 'interrupted' : item.isPaused() ? 'paused' : 'progressing';
      pushDownloads();
    });
    item.once('done', (_ev, state) => {
      d.received = item.getReceivedBytes();
      d.state = state;
      dlItems.delete(id);
      pushDownloads();
      if (state === 'completed') toast(`ダウンロード完了: ${d.name}`);
    });
    toast(`ダウンロード開始: ${d.name}`);
    pushDownloads();
  });
}

// ---------------------------------------------------------------------------
// ネットワークまわり（広告ブロック・HTTPS化・追跡除去・集中モード・UA）
// ---------------------------------------------------------------------------
function setupNetwork() {
  const ses = session.defaultSession;
  const chromeFull = process.versions.chrome;
  const chromeMajor = chromeFull.split('.')[0];
  REAL_UA = ses.getUserAgent().replace(/ Electron\/[\d.]+/, '').replace(/ komorebi\/[\d.]+/, '');
  MOBILE_UA = `Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/${chromeFull} Mobile Safari/537.36`;
  app.userAgentFallback = REAL_UA;
  ses.setUserAgent(REAL_UA);

  const CH_UA = `"Chromium";v="${chromeMajor}", "Google Chrome";v="${chromeMajor}", "Not?A_Brand";v="99"`;
  const CH_UA_FULL = `"Chromium";v="${chromeFull}", "Google Chrome";v="${chromeFull}", "Not?A_Brand";v="99.0.0.0"`;
  const platform = process.platform === 'darwin' ? '"macOS"' : process.platform === 'linux' ? '"Linux"' : '"Windows"';
  const drop = (h, name) => { for (const k of Object.keys(h)) if (k.toLowerCase() === name) delete h[k]; };

  // 本物のChromeと同じUA・クライアントヒントを送る（Googleログイン等の判定対策）
  ses.webRequest.onBeforeSendHeaders({ urls: ['*://*/*'] }, (details, cb) => {
    const h = details.requestHeaders;
    const tab = tabByWcId(details.webContentsId);
    const mobile = !!(tab && tab.mobile);
    drop(h, 'user-agent'); h['User-Agent'] = mobile ? MOBILE_UA : REAL_UA;
    drop(h, 'sec-ch-ua'); h['sec-ch-ua'] = CH_UA;
    drop(h, 'sec-ch-ua-mobile'); h['sec-ch-ua-mobile'] = mobile ? '?1' : '?0';
    drop(h, 'sec-ch-ua-platform'); h['sec-ch-ua-platform'] = mobile ? '"Android"' : platform;
    drop(h, 'sec-ch-ua-full-version-list'); h['sec-ch-ua-full-version-list'] = CH_UA_FULL;
    drop(h, 'sec-ch-ua-full-version'); h['sec-ch-ua-full-version'] = `"${chromeFull}"`;
    cb({ requestHeaders: h });
  });

  ses.webRequest.onBeforeRequest({ urls: ['http://*/*', 'https://*/*'] }, (details, cb) => {
    let u;
    try { u = new URL(details.url); } catch { return cb({}); }
    const isMain = details.resourceType === 'mainFrame';
    const tab = tabByWcId(details.webContentsId);
    const s = settings.data;

    // 集中モード中は気が散るサイトへの移動を止める
    if (isMain && isFocusBlocked(details.url)) {
      cb({ cancel: true });
      if (tab && tab.view) setImmediate(() => { if (tab.view) tab.view.webContents.loadURL(focusPageUrl(details.url)); });
      return;
    }
    // 追跡パラメータを自動で取り除く
    if (isMain && s.stripTracking) {
      const c = cleanTrackingUrl(details.url);
      if (c !== details.url) return cb({ redirectURL: c });
    }
    // HTTP → HTTPS 自動アップグレード
    if (isMain && u.protocol === 'http:' && s.httpsUpgrade && !isLocalHost(u.hostname) && !noUpgrade.has(u.hostname)) {
      upgraded.add(u.hostname);
      return cb({ redirectURL: 'https://' + details.url.slice(7) });
    }
    // 広告・トラッカーのブロック
    if (!isMain && s.adblock && isAdHost(u.hostname)) {
      const pageHost = tab ? hostOf(tab.url) : '';
      if (!(pageHost && sites.data.adblockOff.includes(pageHost))) {
        if (tab) { tab.blocked++; scheduleState(); }
        return cb({ cancel: true });
      }
    }
    cb({});
  });

  // 権限: 安全なものは自動許可、カメラ等は確認してサイトごとに記憶
  const AUTO = new Set(['fullscreen', 'clipboard-sanitized-write', 'pointerLock']);
  const ASK = { media: 'カメラ・マイク', geolocation: '位置情報', notifications: '通知', 'clipboard-read': 'クリップボードの読み取り', midi: 'MIDIデバイス' };
  const permKey = (url, perm) => `${hostOf(url)}|${perm}`;
  ses.setPermissionCheckHandler((_wc, perm, origin) => AUTO.has(perm) || sites.data.perms[permKey(origin, perm)] === true);
  ses.setPermissionRequestHandler(async (wc, perm, cb, details) => {
    if (AUTO.has(perm)) return cb(true);
    const label = ASK[perm];
    if (!label) return cb(false);
    const url = details.requestingUrl || wc.getURL();
    const key = permKey(url, perm);
    if (key in sites.data.perms) return cb(sites.data.perms[key]);
    const r = await dialog.showMessageBox(win, {
      type: 'question', buttons: ['許可', 'ブロック'], defaultId: 1, cancelId: 1,
      title: '権限のリクエスト', message: `${hostOf(url) || 'このページ'} が${label}の使用を求めています`,
      checkboxLabel: 'この選択を記憶する',
    });
    const allow = r.response === 0;
    if (r.checkboxChecked) { sites.data.perms[key] = allow; sites.save(); }
    cb(allow);
  });
}

// ---------------------------------------------------------------------------
// ウィンドウ
// ---------------------------------------------------------------------------
function createWindow() {
  const b = settings.data.bounds || {};
  win = new BrowserWindow({
    width: b.width || 1360,
    height: b.height || 860,
    x: b.x,
    y: b.y,
    minWidth: 640,
    minHeight: 420,
    backgroundColor: BG,
    title: 'Komorebi',
    show: false,
    autoHideMenuBar: true,
    titleBarStyle: 'hidden',
    titleBarOverlay: { color: BG, symbolColor: '#e8eaed', height: ui.titleH },
    webPreferences: { preload: UI_PRELOAD, contextIsolation: true, nodeIntegration: false, sandbox: true },
  });
  if (b.maximized) win.maximize();
  win.once('ready-to-show', () => win.show());
  win.loadFile(path.join(UI_DIR, 'index.html'));
  win.webContents.on('will-navigate', (e) => e.preventDefault());
  win.webContents.setWindowOpenHandler(() => ({ action: 'deny' }));
  attachShortcuts(win.webContents);

  const saveBounds = () => {
    if (!win || win.isDestroyed() || win.isMinimized()) return;
    const nb = win.isMaximized() ? { ...(settings.data.bounds || {}), maximized: true } : { ...win.getBounds(), maximized: false };
    settings.set('bounds', nb);
  };
  win.on('resize', () => { layout(); saveBounds(); });
  win.on('move', saveBounds);
  win.on('maximize', () => { layout(); saveBounds(); });
  win.on('unmaximize', () => { layout(); saveBounds(); });
  win.on('enter-full-screen', layout);
  win.on('leave-full-screen', layout);
  win.on('restore', layout);
  win.on('show', layout);
  win.on('closed', () => {
    win = null;
    overlay = null;
    tabs.length = 0;
    activeId = null;
  });

  createOverlay();
  if (DEV) win.webContents.openDevTools({ mode: 'detach' });
}

// ---------------------------------------------------------------------------
// IPC（UI・オーバーレイ・ページからの要求）
// ---------------------------------------------------------------------------
const fromUI = (e) => !!win && (e.sender === win.webContents || (overlay && e.sender === overlay.webContents));

function on(channel, fn) {
  ipcMain.on(channel, (e, ...args) => { if (fromUI(e)) fn(e, ...args); });
}
function handle(channel, fn) {
  ipcMain.handle(channel, (e, ...args) => (fromUI(e) ? fn(e, ...args) : null));
}

function setupIpc() {
  on('k:ui-ready', () => {
    uiReady = true;
    if (!tabs.length) restoreOrOpenFirst();
    applySetting('opacity');
    applySetting('alwaysOnTop');
    scheduleState();
  });
  on('k:ui-layout', (_e, l) => {
    const prevTitle = ui.titleH;
    ui = { top: Math.round(l.top), left: Math.round(l.left), titleH: Math.round(l.titleH) || 40 };
    layout();
    if (ui.titleH !== prevTitle && win) {
      try { win.setTitleBarOverlay({ color: BG, symbolColor: '#e8eaed', height: ui.titleH }); } catch { /* 非対応環境 */ }
    }
  });

  // 汎用コマンド
  on('k:cmd', (_e, id) => runCommand(id));

  // タブ
  on('k:tab-activate', (_e, id) => activateTab(id));
  on('k:tab-close', (_e, id) => closeTab(id));
  on('k:tab-new', () => runCommand('tab.new'));
  on('k:tab-menu', (_e, id) => showTabMenu(id));
  on('k:tab-mute', (_e, id) => toggleMute(getTab(id)));
  on('k:tab-move', (_e, { id, beforeId }) => {
    const t = getTab(id);
    if (!t || id === beforeId) return;
    tabs.splice(tabs.indexOf(t), 1);
    const bi = beforeId ? tabs.findIndex((x) => x.id === beforeId) : -1;
    if (bi >= 0) tabs.splice(bi, 0, t);
    else tabs.push(t);
    scheduleState();
    saveSessionSoon();
  });

  // ワークスペース
  on('k:ws-switch', (_e, id) => switchWs(id));
  on('k:ws-new', () => newWs());
  on('k:ws-menu', (_e, id) => {
    Menu.buildFromTemplate([
      { label: '名前を変更', click: () => renameWs(id) },
      { label: '削除', click: () => deleteWs(id) },
    ]).popup({ window: win });
  });

  // ナビゲーション・メニュー
  on('k:nav', (_e, what) => {
    const map = { back: 'nav.back', forward: 'nav.forward', reload: 'nav.reload', stop: 'nav.stop', home: 'nav.home' };
    if (map[what]) runCommand(map[what]);
  });
  on('k:menu', showMainMenu);
  on('k:shield-menu', showShieldMenu);
  on('k:open-url', (_e, { url, newTab }) => (newTab ? createTab(url, { after: activeId }) : navigate(url)));
  on('k:bm-menu', (_e, url) => {
    Menu.buildFromTemplate([
      { label: '新しいタブで開く', click: () => createTab(url, { after: activeId }) },
      { label: '横に並べて開く（分割表示）', click: () => openInSplit(url) },
      { type: 'separator' },
      { label: 'ブックマークを削除', click: () => { const i = bookmarks.data.findIndex((b) => b.url === url); if (i >= 0) { bookmarks.data.splice(i, 1); bookmarks.save(); refreshPanel('bookmarks'); scheduleState(); } } },
    ]).popup({ window: win });
  });

  // アドレスバー
  on('k:omni-input', (_e, { text, rect }) => updateSuggest(text, rect));
  on('k:omni-key', (_e, dir) => {
    if (overlayMode !== 'suggest' || !omni.items.length) return;
    const n = omni.items.length;
    omni.sel = dir === 'down' ? (omni.sel + 1 >= n ? -1 : omni.sel + 1) : (omni.sel - 1 < -1 ? n - 1 : omni.sel - 1);
    sendOverlay('k:ov-sel', omni.sel);
    const it = omni.items[omni.sel];
    win.webContents.send('k:omni-fill', it ? (it.kind === 'tab' || it.kind === 'search' ? it.title : it.url) : omni.text);
  });
  on('k:omni-enter', (_e, text) => {
    if (overlayMode === 'suggest' && omni.sel >= 0 && omni.items[omni.sel]) return pickSuggest(omni.items[omni.sel]);
    hideSuggest();
    navigate(toURL(text));
    win.webContents.send('k:omni-done');
  });
  on('k:omni-close', () => hideSuggest());
  on('k:suggest-pick', (_e, idx) => { const it = omni.items[idx]; if (it) pickSuggest(it); });

  // ページ内検索
  on('k:find', (_e, { text, forward, next }) => {
    const wc = activeWC();
    if (!wc) return;
    if (!text) { wc.stopFindInPage('clearSelection'); return win.webContents.send('k:find-result', { active: 0, total: 0 }); }
    // 注意: findNext は true のときだけ渡す。false を明示すると最初の検索結果(found-in-page)が返らない
    const opts = { forward: forward !== false };
    if (next) opts.findNext = true;
    wc.findInPage(text, opts);
  });
  on('k:find-stop', () => { const wc = activeWC(); if (wc) wc.stopFindInPage('clearSelection'); focusActive(); });

  // オーバーレイ
  on('k:ov-ready', () => {
    overlayReady = true;
    for (const [c, p] of overlayQueue) overlay.webContents.send(c, p);
    overlayQueue = [];
  });
  on('k:ov-close', () => hideOverlay());
  on('k:palette-run', (_e, { item, mod }) => runPaletteItem(item, mod));
  on('k:prompt-result', (_e, { id, value }) => {
    if (!pendingPrompt || pendingPrompt.id !== id) return;
    const p = pendingPrompt;
    pendingPrompt = null;
    hideOverlay();
    p.resolve(value);
  });
  on('k:panel-set', (_e, name) => { panelName = name; });

  handle('k:palette-query', (_e, q) => paletteQuery(q));
  handle('k:panel-data', (_e, name, arg) => {
    const q = String(arg || '').toLowerCase();
    switch (name) {
      case 'history': {
        let list = [...history.data].reverse();
        if (q) list = list.filter((h) => `${h.title} ${h.url}`.toLowerCase().includes(q));
        return list.slice(0, 300);
      }
      case 'bookmarks':
        return bookmarks.data.filter((b) => !q || `${b.title} ${b.url}`.toLowerCase().includes(q));
      case 'downloads': return downloads.map(pubDl);
      case 'notes': return notes.data;
      case 'settings': {
        const { workspaces, bounds, currentWs: _c, ...rest } = settings.data;
        return { ...rest, engines: Object.entries(ENGINES).map(([id, e]) => ({ id, name: e.name })), bangs: Object.keys(BANGS) };
      }
      case 'help': return COMMANDS.map((c) => ({ id: c.id, label: c.label, keys: c.keys }));
      default: return null;
    }
  });

  // パネルからの操作
  on('k:history-delete', (_e, ts) => { const i = history.data.findIndex((h) => h.ts === ts); if (i >= 0) { history.data.splice(i, 1); history.save(); } refreshPanel('history'); });
  on('k:bookmark-remove', (_e, url) => { const i = bookmarks.data.findIndex((b) => b.url === url); if (i >= 0) { bookmarks.data.splice(i, 1); bookmarks.save(); } refreshPanel('bookmarks'); scheduleState(); });
  on('k:bookmark-add-current', () => { const a = activeTab(); if (a && !isBookmarked(a.url)) toggleBookmark(); });
  on('k:dl-open', (_e, id) => { const d = downloads.find((x) => x.id === id); if (d) shell.openPath(d.path); });
  on('k:dl-reveal', (_e, id) => { const d = downloads.find((x) => x.id === id); if (d) shell.showItemInFolder(d.path); });
  on('k:dl-cancel', (_e, id) => { const it = dlItems.get(id); if (it) it.cancel(); });
  on('k:dl-remove', (_e, id) => { const i = downloads.findIndex((x) => x.id === id); if (i >= 0) downloads.splice(i, 1); pushDownloads(); });
  on('k:dl-clear', () => { for (let i = downloads.length - 1; i >= 0; i--) if (downloads[i].state !== 'progressing') downloads.splice(i, 1); pushDownloads(); });
  handle('k:note-new', () => {
    const n = { id: Date.now(), text: '', ts: Date.now() };
    notes.data.unshift(n);
    notes.save();
    return n.id;
  });
  on('k:note-save', (_e, { id, text }) => { const n = notes.data.find((x) => x.id === id); if (n) { n.text = text; n.ts = Date.now(); notes.save(); } });
  on('k:note-delete', (_e, id) => { const i = notes.data.findIndex((x) => x.id === id); if (i >= 0) { notes.data.splice(i, 1); notes.save(); } });
  on('k:note-insert-url', () => { const a = activeTab(); return a && sendOverlay('k:note-url', `${a.title}\n${a.url}`); });
  on('k:setting', (_e, { key, value }) => {
    if (!(key in DEFAULT_SETTINGS) || ['workspaces', 'currentWs', 'bounds'].includes(key)) return;
    settings.data[key] = value;
    settings.save();
    applySetting(key);
    if (key === 'sleepMinutes') toast(value ? `${value}分使わないタブを自動で休止します` : 'タブの自動休止をオフにしました');
  });

  // 新しいタブページ・集中モード画面からの要求（送信元URLを必ず確認する）
  const fromInternalPage = (e, base) => !!e.senderFrame && e.senderFrame.url.startsWith(base);
  ipcMain.handle('k:newtab-data', (e) => {
    if (!fromInternalPage(e, NEWTAB_URL)) return null;
    return {
      bookmarks: bookmarks.data.slice(0, 12),
      engine: (ENGINES[settings.data.searchEngine] || ENGINES.google).name,
      focusUntil,
      recentClosed: closedStack.slice(-4).reverse(),
    };
  });
  ipcMain.on('k:newtab-go', (e, text) => {
    if (!fromInternalPage(e, NEWTAB_URL)) return;
    const t = tabs.find((x) => x.view && x.view.webContents.id === e.sender.id);
    if (t) navigateTab(t, toURL(text));
  });
  ipcMain.on('k:newtab-cmd', (e, id) => {
    if (!fromInternalPage(e, NEWTAB_URL)) return;
    if (['focus.start25', 'focus.start50', 'panel.notes', 'view.palette', 'tab.restore', 'panel.history'].includes(id)) runCommand(id);
  });
  ipcMain.on('k:focus-stop', (e) => {
    if (fromInternalPage(e, FOCUS_URL)) stopFocus();
  });
}

// ---------------------------------------------------------------------------
// アプリのライフサイクル
// ---------------------------------------------------------------------------
app.on('second-instance', (_e, argv) => {
  if (!win) return;
  if (win.isMinimized()) win.restore();
  win.focus();
  const url = argv.find((a) => /^https?:\/\//i.test(a));
  if (url) createTab(url, { after: activeId });
});

app.whenReady().then(() => {
  const dir = app.getPath('userData');
  settings = new Store(path.join(dir, 'settings.json'), DEFAULT_SETTINGS);
  sites = new Store(path.join(dir, 'sites.json'), { zoom: {}, dark: {}, adblockOff: [], hidden: {}, perms: {} });
  bookmarks = new Store(path.join(dir, 'bookmarks.json'), [
    { url: 'https://www.google.com', title: 'Google' },
    { url: 'https://github.com', title: 'GitHub' },
    { url: 'https://www.youtube.com', title: 'YouTube' },
    { url: 'https://ja.wikipedia.org', title: 'Wikipedia' },
  ]);
  history = new Store(path.join(dir, 'history.json'), []);
  notes = new Store(path.join(dir, 'notes.json'), []);
  sessionStore = new Store(path.join(dir, 'session.json'), { tabs: [], active: -1 });
  currentWs = validWs(settings.data.currentWs);

  Menu.setApplicationMenu(null); // ショートカットは自前で処理する
  setupNetwork();
  setupDownloads();
  setupIpc();
  createWindow();

  // 休止タイマー: 一定時間使っていないタブのメモリを解放する
  setInterval(() => {
    const min = settings.data.sleepMinutes;
    if (!min) return;
    const limit = Date.now() - min * 60000;
    for (const t of tabs) {
      if (t.view && t.lastActive < limit && !t.audible && !t.loading) sleepTab(t);
    }
  }, 30000);

  if (SELFTEST) {
    const file = process.argv[process.argv.indexOf('--selftest') + 1];
    setTimeout(() => {
      require(path.resolve(file))({
        app, getWin: () => win, getOverlay: () => overlay, tabs, getActive: activeTab,
        state: () => ({ activeId, split, currentWs, ui, overlayMode }),
        settings, sites, bookmarks, history, notes, downloads,
        createTab, activateTab, closeTab, sleepTab, runCommand, paletteQuery, updateSuggest,
        openPanel, togglePalette, hideOverlay, askPrompt, toURL, buildSuggestions, startFocus,
        isFocusBlocked, scheduleState, toast, wsOrdered,
        flush: () => { saveSession(); flushAll(); },
      });
    }, 2500);
  }
});

app.on('before-quit', () => {
  saveSession();
  flushAll();
});

app.on('window-all-closed', () => app.quit());
