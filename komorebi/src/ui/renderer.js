'use strict';

const K = window.komorebi;
const $ = (id) => document.getElementById(id);

const el = {
  tabs: document.createElement('div'),
  wsBar: document.createElement('div'),
  wsName: document.createElement('span'),
  main: $('main'),
  sidebar: $('sidebar'),
  tabstrip: $('tabstrip'),
  toolbar: $('toolbar'),
  omnibox: $('omnibox'),
  address: $('address'),
  scheme: $('scheme-icon'),
  back: $('back'),
  forward: $('forward'),
  reload: $('reload'),
  star: $('star'),
  reader: $('btn-reader'),
  dark: $('btn-dark'),
  shield: $('btn-shield'),
  shieldBadge: $('shield-badge'),
  split: $('btn-split'),
  dl: $('btn-dl'),
  dlBadge: $('dl-badge'),
  focusChip: $('focus-chip'),
  bookmarks: $('bookmarks'),
  progress: $('progress'),
  findbar: $('findbar'),
  findInput: $('find-input'),
  findCount: $('find-count'),
  toast: $('toast'),
};
el.tabs.id = 'tabs';
el.wsBar.id = 'ws-bar';
el.wsName.id = 'ws-name';

let S = null; // メインプロセスから届く最新の状態
let editing = false; // アドレスバー編集中か
let dragId = null;

const SVG = {
  reload: '<svg viewBox="0 0 24 24"><path d="M12 6V3L8 7l4 4V8a4 4 0 1 1-4 4H6a6 6 0 1 0 6-6z"/></svg>',
  stop: '<svg viewBox="0 0 24 24"><path d="M6 6h12v12H6z"/></svg>',
  close: '<svg viewBox="0 0 10 10"><path d="M1 1l8 8M9 1l-8 8"/></svg>',
  vol: '<svg viewBox="0 0 24 24"><path d="M3 9v6h4l5 5V4L7 9H3zm13.5 3A4.5 4.5 0 0 0 14 7.97v8.05c1.48-.73 2.5-2.25 2.5-4.02z"/></svg>',
  mute: '<svg viewBox="0 0 24 24"><path d="M16.5 12A4.5 4.5 0 0 0 14 7.97v2.21l2.45 2.45c.03-.2.05-.41.05-.63zm2.5 0c0 .94-.2 1.82-.54 2.64l1.51 1.51A8.8 8.8 0 0 0 21 12c0-4.28-2.99-7.86-7-8.77v2.06c2.89.86 5 3.54 5 6.71zM4.27 3 3 4.27 7.73 9H3v6h4l5 5v-6.73l4.25 4.25c-.67.52-1.42.93-2.25 1.18v2.06a8.99 8.99 0 0 0 3.69-1.81L19.73 21 21 19.73l-9-9L4.27 3zM12 4 9.91 6.09 12 8.18V4z"/></svg>',
};

// ---------------------------------------------------------------------------
// ヘルパー
// ---------------------------------------------------------------------------
const hostOf = (url) => {
  try {
    const u = new URL(url);
    return /^https?:$/.test(u.protocol) ? u.hostname : '';
  } catch {
    return '';
  }
};

// ファビコンが無いときのイニシャルタイル（外部サービスにアクセスしない）
function hue(str) {
  let h = 0;
  for (const c of str) h = (h * 31 + c.charCodeAt(0)) % 360;
  return h;
}
function tile(label, seed) {
  const d = document.createElement('div');
  d.className = 'favicon';
  d.textContent = (label || '?').trim().charAt(0).toUpperCase();
  d.style.background = `hsl(${hue(seed || label || '')} 55% 70%)`;
  return d;
}
function iconFor(item, seedUrl) {
  if (item.favicon) {
    const img = document.createElement('img');
    img.className = 'favicon';
    img.src = item.favicon;
    img.onerror = () => img.replaceWith(tile(item.title, seedUrl));
    return img;
  }
  const isInternal = !hostOf(seedUrl);
  const t = tile(isInternal ? '木' : (hostOf(seedUrl).replace(/^www\./, '') || item.title), seedUrl);
  if (isInternal) t.style.background = 'hsl(100 45% 72%)';
  return t;
}

const prettyUrl = (url) => (/^https?:/i.test(url || '') ? url : '');

// ---------------------------------------------------------------------------
// 描画
// ---------------------------------------------------------------------------
function placeSlots() {
  const side = S.settings.tabLayout === 'side';
  document.body.className = 'layout-' + (side ? 'side' : 'top') + (side && S.settings.sidebarCollapsed ? ' collapsed' : '');
  const wsHost = side ? $('side-head') : $('ws-slot-top');
  const tabHost = side ? $('side-body') : $('tabs-slot-top');
  if (el.wsBar.parentElement !== wsHost) wsHost.append(el.wsBar, el.wsName);
  if (el.tabs.parentElement !== tabHost) tabHost.appendChild(el.tabs);
}

function render() {
  placeSlots();
  renderWorkspaces();
  renderTabs();
  renderToolbar();
  renderBookmarks();
  reportLayout();
}

function renderWorkspaces() {
  el.wsBar.replaceChildren();
  for (const w of S.workspaces) {
    const c = document.createElement('div');
    c.className = 'ws' + (w.id === S.currentWs ? ' active' : '');
    c.style.setProperty('--c', w.color);
    c.textContent = w.name.charAt(0);
    c.title = `${w.name}（Alt+${S.workspaces.indexOf(w) + 1}）`;
    c.addEventListener('click', () => K.send('k:ws-switch', w.id));
    c.addEventListener('contextmenu', (e) => { e.preventDefault(); K.send('k:ws-menu', w.id); });
    el.wsBar.appendChild(c);
  }
  const add = document.createElement('div');
  add.className = 'ws-add';
  add.textContent = '+';
  add.title = 'ワークスペースを追加';
  add.addEventListener('click', () => K.send('k:ws-new'));
  el.wsBar.appendChild(add);
  const cur = S.workspaces.find((w) => w.id === S.currentWs);
  el.wsName.textContent = cur ? cur.name : '';
  document.documentElement.style.setProperty('--ws', cur ? cur.color : '#9fd48b');
}

function makeTab(t) {
  const tab = document.createElement('div');
  tab.className = 'tab' + (t.id === S.activeId ? ' active' : '') + (t.sleeping ? ' sleeping' : '') +
    (t.pinned ? ' pinned' : '') + (t.inSplit ? ' in-split' : '');
  tab.title = t.sleeping ? `${t.title}（休止中）` : t.title;
  tab.draggable = true;

  let fav;
  if (t.loading) {
    fav = document.createElement('div');
    fav.className = 'favicon spinner';
  } else {
    fav = iconFor(t, t.url);
  }

  const title = document.createElement('span');
  title.className = 'title';
  title.textContent = t.title || '新しいタブ';

  tab.append(fav, title);

  if (t.audible || t.muted) {
    const au = document.createElement('div');
    au.className = 'audio' + (t.muted ? ' muted' : '');
    au.innerHTML = t.muted ? SVG.mute : SVG.vol;
    au.title = t.muted ? 'ミュート解除' : 'タブをミュート';
    au.addEventListener('mousedown', (e) => { e.stopPropagation(); K.send('k:tab-mute', t.id); });
    tab.appendChild(au);
  }

  const close = document.createElement('div');
  close.className = 'close';
  close.innerHTML = SVG.close;
  close.addEventListener('mousedown', (e) => { e.stopPropagation(); K.send('k:tab-close', t.id); });
  tab.appendChild(close);

  tab.addEventListener('mousedown', (e) => {
    if (e.button === 0) K.send('k:tab-activate', t.id);
    else if (e.button === 1) { e.preventDefault(); K.send('k:tab-close', t.id); }
  });
  tab.addEventListener('contextmenu', (e) => { e.preventDefault(); K.send('k:tab-menu', t.id); });

  // ドラッグで並べ替え
  tab.addEventListener('dragstart', (e) => { dragId = t.id; e.dataTransfer.effectAllowed = 'move'; e.dataTransfer.setData('text/plain', String(t.id)); });
  tab.addEventListener('dragover', (e) => { if (dragId !== null) { e.preventDefault(); tab.classList.add('dragover'); } });
  tab.addEventListener('dragleave', () => tab.classList.remove('dragover'));
  tab.addEventListener('drop', (e) => {
    e.preventDefault();
    tab.classList.remove('dragover');
    if (dragId !== null && dragId !== t.id) K.send('k:tab-move', { id: dragId, beforeId: t.id });
    dragId = null;
  });
  tab.addEventListener('dragend', () => { dragId = null; });
  return tab;
}

function renderTabs() {
  el.tabs.replaceChildren(...S.tabs.map(makeTab));
}

function renderToolbar() {
  const i = S.info;
  el.back.disabled = !i || !i.canGoBack;
  el.forward.disabled = !i || !i.canGoForward;

  const loading = !!(i && i.loading);
  el.reload.title = loading ? '読み込みを停止' : '再読み込み (Ctrl+R)';
  el.reload.innerHTML = loading ? SVG.stop : SVG.reload;

  if (!editing) el.address.value = i ? prettyUrl(i.url) : '';

  el.scheme.className = 'scheme';
  if (i) {
    if (i.url.startsWith('https://')) el.scheme.classList.add('secure');
    else if (i.url.startsWith('http://')) el.scheme.classList.add('insecure');
    else el.scheme.classList.add('local');
  }

  el.star.classList.toggle('saved', !!(i && i.bookmarked));
  el.reader.classList.toggle('on', !!(i && i.reader));
  el.dark.classList.toggle('on', !!(i && i.dark));

  // シールド: 広告ブロックの状態とブロック数
  const blocking = S.settings.adblock && !(i && i.adblockOff);
  el.shield.classList.toggle('off', !blocking);
  const n = i ? i.blocked : 0;
  el.shieldBadge.hidden = n === 0;
  el.shieldBadge.textContent = n > 99 ? '99+' : String(n);

  el.split.classList.toggle('on', S.split);
  el.dlBadge.hidden = !S.downloadsActive;
  el.dlBadge.textContent = String(S.downloadsActive);

  el.progress.classList.toggle('loading', loading);
  el.progress.style.width = loading ? '80%' : '100%';
  if (!loading) setTimeout(() => { el.progress.style.width = '0'; }, 250);

  tickFocus();
}

function renderBookmarks() {
  el.bookmarks.replaceChildren();
  for (const b of S.bookmarks) {
    const chip = document.createElement('div');
    chip.className = 'bm';
    chip.title = b.url;
    const ico = iconFor({ favicon: b.favicon, title: b.title }, b.url);
    ico.style.width = ico.style.height = '14px';
    const span = document.createElement('span');
    span.className = 't';
    span.textContent = b.title || b.url;
    chip.append(ico, span);
    chip.addEventListener('mousedown', (e) => {
      if (e.button === 0) K.send('k:open-url', { url: b.url, newTab: false });
      else if (e.button === 1) { e.preventDefault(); K.send('k:open-url', { url: b.url, newTab: true }); }
    });
    chip.addEventListener('contextmenu', (e) => { e.preventDefault(); K.send('k:bm-menu', b.url); });
    el.bookmarks.appendChild(chip);
  }
}

// 集中モードの残り時間表示
function tickFocus() {
  const left = S ? S.focusUntil - Date.now() : 0;
  if (!S || left <= 0) { el.focusChip.hidden = true; return; }
  const m = Math.floor(left / 60000);
  const s = Math.floor((left % 60000) / 1000);
  el.focusChip.hidden = false;
  el.focusChip.textContent = `🎯 ${m}:${String(s).padStart(2, '0')}`;
}
setInterval(tickFocus, 1000);

// ---------------------------------------------------------------------------
// 外枠のサイズをメインプロセスへ伝える（ページの表示位置の計算に使う）
// ---------------------------------------------------------------------------
let lastLayout = '';
function reportLayout() {
  const side = document.body.classList.contains('layout-side');
  const top = Math.ceil(el.main.getBoundingClientRect().height);
  const left = side ? Math.ceil(el.sidebar.getBoundingClientRect().width) : 0;
  const titleH = Math.round(side ? el.toolbar.offsetHeight : el.tabstrip.offsetHeight) || 40;
  const key = `${top}|${left}|${titleH}`;
  if (key === lastLayout) return;
  lastLayout = key;
  K.send('k:ui-layout', { top, left, titleH });
}
new ResizeObserver(reportLayout).observe(el.main);
new ResizeObserver(reportLayout).observe(el.sidebar);

// ---------------------------------------------------------------------------
// ボタン
// ---------------------------------------------------------------------------
const cmd = (id) => () => K.send('k:cmd', id);
$('new-tab-top').addEventListener('click', cmd('tab.new'));
$('side-new').addEventListener('click', cmd('tab.new'));
$('side-palette').addEventListener('click', cmd('view.palette'));
$('btn-palette').addEventListener('click', cmd('view.palette'));
$('btn-split').addEventListener('click', cmd('view.split'));
$('btn-dl').addEventListener('click', cmd('panel.downloads'));
$('btn-notes').addEventListener('click', cmd('panel.notes'));
el.star.addEventListener('click', cmd('bookmark.toggle'));
el.reader.addEventListener('click', cmd('tools.reader'));
el.dark.addEventListener('click', cmd('tools.dark'));
el.shield.addEventListener('click', () => K.send('k:shield-menu'));
$('menu').addEventListener('click', () => K.send('k:menu'));
el.back.addEventListener('click', () => K.send('k:nav', 'back'));
el.forward.addEventListener('click', () => K.send('k:nav', 'forward'));
el.reload.addEventListener('click', () => K.send('k:nav', S && S.info && S.info.loading ? 'stop' : 'reload'));
el.focusChip.addEventListener('click', cmd('focus.stop'));
$('side-toggle').addEventListener('click', () => {
  K.send('k:setting', { key: 'sidebarCollapsed', value: !S.settings.sidebarCollapsed });
});

// ---------------------------------------------------------------------------
// アドレスバー
// ---------------------------------------------------------------------------
function omniRect() {
  const r = el.omnibox.getBoundingClientRect();
  return { left: r.left, top: r.top, bottom: r.bottom, width: r.width, height: r.height };
}

el.address.addEventListener('focus', () => {
  editing = true;
  requestAnimationFrame(() => el.address.select());
});
el.address.addEventListener('blur', () => {
  editing = false;
  // 候補のクリックを先に処理させるため少し待ってから閉じる
  setTimeout(() => { if (document.activeElement !== el.address) K.send('k:omni-close'); }, 160);
  if (S) renderToolbar();
});
el.address.addEventListener('input', () => {
  K.send('k:omni-input', { text: el.address.value, rect: omniRect() });
});
el.address.addEventListener('keydown', (e) => {
  if (e.isComposing) return;
  if (e.key === 'ArrowDown' || e.key === 'ArrowUp') {
    e.preventDefault();
    K.send('k:omni-key', e.key === 'ArrowDown' ? 'down' : 'up');
  } else if (e.key === 'Enter') {
    K.send('k:omni-enter', el.address.value);
  } else if (e.key === 'Escape') {
    K.send('k:omni-close');
    editing = false;
    el.address.blur();
  }
});

// ---------------------------------------------------------------------------
// ページ内検索
// ---------------------------------------------------------------------------
function openFind() {
  el.findbar.hidden = false;
  el.findInput.focus();
  el.findInput.select();
}
function closeFind() {
  el.findbar.hidden = true;
  el.findCount.textContent = '';
  K.send('k:find-stop');
}
function doFind(forward, next) {
  K.send('k:find', { text: el.findInput.value, forward, next });
}
el.findInput.addEventListener('input', () => doFind(true, false));
el.findInput.addEventListener('keydown', (e) => {
  if (e.isComposing) return;
  if (e.key === 'Enter') doFind(!e.shiftKey, true);
  else if (e.key === 'Escape') closeFind();
});
$('find-next').addEventListener('click', () => doFind(true, true));
$('find-prev').addEventListener('click', () => doFind(false, true));
$('find-close').addEventListener('click', closeFind);

// ---------------------------------------------------------------------------
// トースト
// ---------------------------------------------------------------------------
let toastTimer = null;
function showToast(msg) {
  el.toast.textContent = msg;
  el.toast.classList.add('show');
  clearTimeout(toastTimer);
  toastTimer = setTimeout(() => el.toast.classList.remove('show'), 2600);
}

// ---------------------------------------------------------------------------
// メインプロセスからの通知
// ---------------------------------------------------------------------------
K.on('k:state', (s) => { S = s; render(); });
K.on('k:toast', showToast);
K.on('k:focus-address', () => { el.address.focus(); el.address.select(); });
K.on('k:omni-fill', (v) => { el.address.value = v; });
K.on('k:omni-done', () => { editing = false; el.address.blur(); });
K.on('k:find-open', openFind);
K.on('k:find-result', (r) => {
  el.findCount.textContent = r.total ? `${r.active} / ${r.total}` : (el.findInput.value ? '見つかりません' : '');
});

window.addEventListener('DOMContentLoaded', () => {
  K.send('k:ui-ready');
});
