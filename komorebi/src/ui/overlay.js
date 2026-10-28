'use strict';

const K = window.komorebi;
const $ = (id) => document.getElementById(id);
const L = { palette: $('palette'), suggest: $('suggest'), prompt: $('prompt'), panel: $('panel') };

let mode = null;

// 要素を手早く作る
function h(tag, cls, text) {
  const e = document.createElement(tag);
  if (cls) e.className = cls;
  if (text !== undefined) e.textContent = text;
  return e;
}

const hostOf = (url) => {
  try { return new URL(url).hostname.replace(/^www\./, ''); } catch { return ''; }
};
function tileHue(s) {
  let x = 0;
  for (const c of s) x = (x * 31 + c.charCodeAt(0)) % 360;
  return x;
}
function makeTile(title, url, favicon) {
  const t = h('div', 'tile');
  const label = (hostOf(url) || title || '?').charAt(0).toUpperCase();
  t.style.background = `hsl(${tileHue(hostOf(url) || title || '')} 55% 70%)`;
  if (favicon) {
    const img = document.createElement('img');
    img.src = favicon;
    img.onerror = () => { img.remove(); t.textContent = label; };
    t.appendChild(img);
  } else {
    t.textContent = label;
  }
  return t;
}

function hideAll() {
  for (const k of Object.keys(L)) L[k].hidden = true;
  mode = null;
}

function show(p) {
  hideAll();
  mode = p.mode;
  if (mode === 'palette') openPalette();
  else if (mode === 'suggest') openSuggest(p);
  else if (mode === 'prompt') openPrompt(p);
  else if (mode === 'panel') openPanel(p.panel);
}

// どのモードでも Esc で閉じる
window.addEventListener('keydown', (e) => {
  if (e.key === 'Escape' && mode && mode !== 'suggest') {
    e.preventDefault();
    K.send('k:ov-close');
  }
});

// ===========================================================================
// コマンドパレット
// ===========================================================================
const palInput = $('pal-input');
const palList = $('pal-list');
let palItems = [];
let palSel = 0;
let qSeq = 0;

function openPalette() {
  L.palette.hidden = false;
  palInput.value = '';
  runQuery('');
  requestAnimationFrame(() => palInput.focus());
}

async function runQuery(q) {
  const seq = ++qSeq;
  const items = await K.invoke('k:palette-query', q);
  if (seq !== qSeq) return; // 古い結果は捨てる
  palItems = items || [];
  palSel = 0;
  renderPalette();
}

function renderPalette() {
  palList.replaceChildren();
  if (!palItems.length) {
    palList.appendChild(h('div', 'pal-empty', '見つかりませんでした'));
    return;
  }
  palItems.forEach((it, i) => {
    const d = h('div', 'pal-item' + (i === palSel ? ' sel' : ''));
    d.appendChild(h('span', 'ic', it.icon || '•'));
    const tx = h('div', 'tx');
    tx.append(h('div', 'ti', it.title), h('div', 'su', it.sub || ''));
    d.appendChild(tx);
    d.addEventListener('mousemove', () => { if (palSel !== i) { palSel = i; updatePalSel(false); } });
    d.addEventListener('click', (e) => runPalItem(i, e));
    palList.appendChild(d);
  });
}

function updatePalSel(scroll = true) {
  [...palList.children].forEach((c, i) => c.classList.toggle('sel', i === palSel));
  const c = palList.children[palSel];
  if (c && scroll) c.scrollIntoView({ block: 'nearest' });
}

function runPalItem(i, e) {
  const item = palItems[i];
  if (!item) return;
  K.send('k:palette-run', { item, mod: { ctrl: !!(e && (e.ctrlKey || e.metaKey)) } });
}

palInput.addEventListener('input', () => runQuery(palInput.value));
palInput.addEventListener('keydown', (e) => {
  if (e.isComposing) return;
  if (e.key === 'ArrowDown') { e.preventDefault(); palSel = Math.min(palItems.length - 1, palSel + 1); updatePalSel(); }
  else if (e.key === 'ArrowUp') { e.preventDefault(); palSel = Math.max(0, palSel - 1); updatePalSel(); }
  else if (e.key === 'Enter') { e.preventDefault(); runPalItem(palSel, e); }
});
L.palette.addEventListener('mousedown', (e) => { if (e.target === L.palette) K.send('k:ov-close'); });

// ===========================================================================
// アドレスバーの候補
// ===========================================================================
let sugItems = [];
const SUG_ICON = { search: '🔎', url: '🌐', tab: '🗂', bookmark: '★', history: '🕘' };

function openSuggest(p) {
  L.suggest.hidden = false;
  sugItems = p.items || [];
  renderSuggest(p.sel);
}

function renderSuggest(sel) {
  L.suggest.replaceChildren();
  sugItems.forEach((it, i) => {
    const d = h('div', 'sg-item' + (i === sel ? ' sel' : ''));
    d.appendChild(h('span', 'ic', SUG_ICON[it.kind] || '•'));
    const tx = h('div', 'tx');
    tx.append(h('div', 'ti', it.title), h('div', 'su', it.sub || it.url || ''));
    d.appendChild(tx);
    // blur より先に拾うため click ではなく mousedown
    d.addEventListener('mousedown', (e) => { e.preventDefault(); K.send('k:suggest-pick', i); });
    L.suggest.appendChild(d);
  });
}

// ===========================================================================
// 入力・確認ダイアログ
// ===========================================================================
let promptId = 0;
const prInput = $('pr-input');

function openPrompt(p) {
  L.prompt.hidden = false;
  promptId = p.id;
  $('pr-title').textContent = p.title || '';
  $('pr-label').textContent = p.label || '';
  prInput.hidden = !p.input;
  prInput.value = p.value || '';
  $('pr-ok').textContent = p.confirmLabel || 'OK';
  setTimeout(() => {
    if (p.input) { prInput.focus(); prInput.select(); } else $('pr-ok').focus();
  }, 0);
}
const submitPrompt = (ok) => K.send('k:prompt-result', { id: promptId, value: ok ? (prInput.hidden ? '' : prInput.value) : null });
$('pr-ok').addEventListener('click', () => submitPrompt(true));
$('pr-cancel').addEventListener('click', () => submitPrompt(false));
prInput.addEventListener('keydown', (e) => { if (e.key === 'Enter' && !e.isComposing) submitPrompt(true); });
L.prompt.addEventListener('mousedown', (e) => { if (e.target === L.prompt) submitPrompt(false); });

// ===========================================================================
// パネル
// ===========================================================================
const PANELS = [
  ['history', '履歴'], ['downloads', 'ダウンロード'], ['bookmarks', 'ブックマーク'],
  ['notes', 'メモ'], ['settings', '設定'], ['help', 'ヘルプ'],
];
const body = $('panel-body');
let curPanel = 'history';
let shellFor = null; // いま骨組みを作っているパネル名
const search = { history: '', bookmarks: '', help: '' };
let curNote = null;
let noteTimer = null;

$('panel-close').addEventListener('click', () => K.send('k:ov-close'));

function openPanel(name) {
  L.panel.hidden = false;
  curPanel = name;
  const tabs = $('panel-tabs');
  tabs.replaceChildren();
  for (const [id, label] of PANELS) {
    const b = h('button', id === name ? 'on' : '', label);
    b.addEventListener('click', () => {
      curPanel = id;
      K.send('k:panel-set', id);
      openPanel(id);
    });
    tabs.appendChild(b);
  }
  shellFor = null;
  renderPanel();
}

K.on('k:panel-refresh', (name) => {
  if (mode === 'panel' && name === curPanel) renderPanel();
});

async function renderPanel() {
  const name = curPanel;
  const fresh = shellFor !== name;
  if (fresh) { body.replaceChildren(); shellFor = name; }
  const data = await K.invoke('k:panel-data', name, search[name] || '');
  if (curPanel !== name) return;
  ({ history: renderHistory, downloads: renderDownloads, bookmarks: renderBookmarks, notes: renderNotes, settings: renderSettings, help: renderHelp })[name](data, fresh);
}

// 検索ボックス付きの共通の骨組み
function listShell(name, placeholder, extra) {
  const tools = h('div', 'p-tools');
  const inp = h('input', 'p-search');
  inp.placeholder = placeholder;
  inp.value = search[name] || '';
  inp.addEventListener('input', () => { search[name] = inp.value; renderPanel(); });
  tools.appendChild(inp);
  if (extra) extra.forEach((x) => tools.appendChild(x));
  const list = h('div', 'p-list');
  body.append(tools, list);
  setTimeout(() => inp.focus(), 0);
  return list;
}
const listEl = () => body.querySelector('.p-list');

// ---------- 履歴 ----------
function dayLabel(ts) {
  const d = new Date(ts);
  const t = new Date();
  const same = (a, b) => a.toDateString() === b.toDateString();
  if (same(d, t)) return '今日';
  const y = new Date(t); y.setDate(t.getDate() - 1);
  if (same(d, y)) return '昨日';
  return `${d.getFullYear()}/${d.getMonth() + 1}/${d.getDate()}`;
}
const pad = (n) => String(n).padStart(2, '0');

function renderHistory(items, fresh) {
  if (fresh) {
    const clr = h('button', 'pbtn danger', 'すべて削除');
    clr.addEventListener('click', () => K.send('k:cmd', 'data.clearHistory'));
    listShell('history', '履歴を検索', [clr]);
  }
  const list = listEl();
  list.replaceChildren();
  if (!items.length) { list.appendChild(h('div', 'p-empty', '履歴はありません')); return; }
  let last = '';
  for (const it of items) {
    const g = dayLabel(it.ts);
    if (g !== last) { list.appendChild(h('div', 'p-group', g)); last = g; }
    const row = h('div', 'item');
    const d = new Date(it.ts);
    row.appendChild(h('span', 'time', `${pad(d.getHours())}:${pad(d.getMinutes())}`));
    const tx = h('div', 'tx');
    tx.append(h('div', 'ti', it.title || it.url), h('div', 'su', hostOf(it.url) || it.url));
    row.appendChild(tx);
    const x = h('button', 'x', '✕');
    x.title = '履歴から削除';
    x.addEventListener('click', (e) => { e.stopPropagation(); K.send('k:history-delete', it.ts); });
    row.appendChild(x);
    row.addEventListener('click', (e) => K.send('k:open-url', { url: it.url, newTab: e.ctrlKey }));
    row.addEventListener('auxclick', (e) => { if (e.button === 1) K.send('k:open-url', { url: it.url, newTab: true }); });
    list.appendChild(row);
  }
}

// ---------- ブックマーク ----------
function renderBookmarks(items, fresh) {
  if (fresh) {
    const add = h('button', 'pbtn primary', '＋ このページ');
    add.addEventListener('click', () => K.send('k:bookmark-add-current'));
    listShell('bookmarks', 'ブックマークを検索', [add]);
  }
  const list = listEl();
  list.replaceChildren();
  if (!items.length) { list.appendChild(h('div', 'p-empty', 'ブックマークはありません\nCtrl+D で追加できます')); return; }
  for (const b of items) {
    const row = h('div', 'item');
    row.appendChild(makeTile(b.title, b.url, b.favicon));
    const tx = h('div', 'tx');
    tx.append(h('div', 'ti', b.title || b.url), h('div', 'su', b.url));
    row.appendChild(tx);
    const x = h('button', 'x', '✕');
    x.title = '削除';
    x.addEventListener('click', (e) => { e.stopPropagation(); K.send('k:bookmark-remove', b.url); });
    row.appendChild(x);
    row.addEventListener('click', (e) => K.send('k:open-url', { url: b.url, newTab: e.ctrlKey }));
    row.addEventListener('auxclick', (e) => { if (e.button === 1) K.send('k:open-url', { url: b.url, newTab: true }); });
    list.appendChild(row);
  }
}

// ---------- ダウンロード ----------
function fmtBytes(n) {
  if (!n || n < 0) return '—';
  const u = ['B', 'KB', 'MB', 'GB'];
  let i = 0;
  while (n >= 1024 && i < u.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(i ? 1 : 0)} ${u[i]}`;
}

function renderDownloads(items, fresh) {
  if (fresh) {
    const tools = h('div', 'p-tools');
    tools.appendChild(h('div', '', 'ダウンロードしたファイル'));
    tools.lastChild.style.flex = '1';
    const clr = h('button', 'pbtn', '完了を消去');
    clr.addEventListener('click', () => K.send('k:dl-clear'));
    tools.appendChild(clr);
    body.append(tools, h('div', 'p-list'));
  }
  const list = listEl();
  list.replaceChildren();
  if (!items.length) { list.appendChild(h('div', 'p-empty', 'ダウンロードはありません')); return; }
  for (const d of items) {
    const box = h('div', 'dl');
    box.appendChild(h('div', 'nm', d.name));
    const pct = d.total > 0 ? Math.round((d.received / d.total) * 100) : 0;
    const stateLabel = { progressing: `${pct}%`, paused: '一時停止中', completed: '完了', cancelled: 'キャンセル', interrupted: '中断' }[d.state] || d.state;
    box.appendChild(h('div', 'meta', `${stateLabel} ・ ${fmtBytes(d.received)} / ${fmtBytes(d.total)} ・ ${hostOf(d.url)}`));
    if (d.state === 'progressing' || d.state === 'paused') {
      const bar = h('div', 'bar');
      const i = document.createElement('i');
      i.style.width = pct + '%';
      bar.appendChild(i);
      box.appendChild(bar);
    }
    const acts = h('div', 'acts');
    const btn = (label, fn, cls) => { const b = h('button', 'pbtn' + (cls ? ' ' + cls : ''), label); b.addEventListener('click', fn); acts.appendChild(b); };
    if (d.state === 'completed') { btn('開く', () => K.send('k:dl-open', d.id), 'primary'); btn('フォルダで表示', () => K.send('k:dl-reveal', d.id)); }
    if (d.state === 'progressing' || d.state === 'paused') btn('キャンセル', () => K.send('k:dl-cancel', d.id), 'danger');
    else btn('一覧から削除', () => K.send('k:dl-remove', d.id));
    box.appendChild(acts);
    list.appendChild(box);
  }
}

// ---------- メモ ----------
const noteTitle = (n) => (n.text.split('\n').find((l) => l.trim()) || '（空のメモ）').trim().slice(0, 40);

async function newNote() {
  const id = await K.invoke('k:note-new');
  curNote = id;
  shellFor = null;
  renderPanel();
}

function renderNotes(notes, fresh) {
  if (fresh) {
    const wrap = h('div', 'notes-wrap');
    const tools = h('div', 'p-tools');
    const add = h('button', 'pbtn primary', '＋ 新規メモ');
    add.addEventListener('click', newNote);
    const url = h('button', 'pbtn', '現在のURLを挿入');
    url.addEventListener('click', () => K.send('k:note-insert-url'));
    const del = h('button', 'pbtn danger', '削除');
    del.addEventListener('click', () => {
      if (curNote !== null) { K.send('k:note-delete', curNote); curNote = null; }
      setTimeout(renderPanel, 60);
    });
    tools.append(add, url, del);
    const list = h('div', 'note-list');
    const ta = h('textarea', 'note-edit');
    ta.placeholder = 'ここにメモを書くと自動で保存されます。\nページ上で文字を選択 → 右クリック →「選択範囲をメモに追加」も使えます。';
    ta.addEventListener('input', () => {
      clearTimeout(noteTimer);
      const id = curNote;
      noteTimer = setTimeout(() => K.send('k:note-save', { id, text: ta.value }), 350);
      const it = list.querySelector('.note-item.on');
      if (it) it.firstChild.textContent = noteTitle({ text: ta.value });
    });
    wrap.append(tools, list, ta);
    body.appendChild(wrap);
  }
  const list = body.querySelector('.note-list');
  const ta = body.querySelector('.note-edit');
  if (curNote === null || !notes.some((n) => n.id === curNote)) curNote = notes.length ? notes[0].id : null;
  list.replaceChildren();
  for (const n of notes) {
    const it = h('div', 'note-item' + (n.id === curNote ? ' on' : ''));
    it.appendChild(document.createTextNode(noteTitle(n)));
    it.appendChild(h('small', '', new Date(n.ts).toLocaleDateString('ja-JP')));
    it.addEventListener('click', () => { curNote = n.id; renderNotes(notes, false); });
    list.appendChild(it);
  }
  const cur = notes.find((n) => n.id === curNote);
  ta.disabled = !cur;
  if (document.activeElement !== ta) ta.value = cur ? cur.text : '';
  else if (cur && ta.value === '') ta.value = cur.text;
  if (!cur) ta.placeholder = '「＋ 新規メモ」でメモを作成します';
}

K.on('k:note-url', (text) => {
  const ta = body.querySelector('.note-edit');
  if (!ta || ta.disabled) return;
  const pos = ta.selectionStart;
  ta.value = ta.value.slice(0, pos) + text + ta.value.slice(pos);
  ta.dispatchEvent(new Event('input'));
  ta.focus();
});

// ---------- 設定 ----------
function sec(title) { return h('div', 'sec', title); }
function row(label, desc, control) {
  const r = h('div', 'row');
  const lb = h('div', 'lb');
  lb.appendChild(h('b', '', label));
  if (desc) lb.appendChild(h('small', '', desc));
  r.append(lb, control);
  return r;
}
const set = (key, value) => K.send('k:setting', { key, value });
function toggle(cfg, key) {
  const c = document.createElement('input');
  c.type = 'checkbox';
  c.className = 'sw';
  c.checked = !!cfg[key];
  c.addEventListener('change', () => set(key, c.checked));
  return c;
}
function select(cfg, key, options) {
  const s = document.createElement('select');
  for (const [v, label] of options) {
    const o = document.createElement('option');
    o.value = String(v);
    o.textContent = label;
    if (String(cfg[key]) === String(v)) o.selected = true;
    s.appendChild(o);
  }
  s.addEventListener('change', () => {
    const num = typeof cfg[key] === 'number';
    set(key, num ? Number(s.value) : s.value);
  });
  return s;
}

function renderSettings(cfg, fresh) {
  // 設定は操作中に作り直すと入力が飛ぶので、パネルを開いたときだけ描画する
  if (!fresh) return;
  const wrap = h('div', 'p-scroll');
  wrap.appendChild(sec('一般'));
  wrap.appendChild(row('起動時に前回のタブを復元', 'タブは休止状態で復元されるので軽く起動します', toggle(cfg, 'restoreSession')));
  wrap.appendChild(row('検索エンジン', 'アドレスバーの検索に使います', select(cfg, 'searchEngine', cfg.engines.map((e) => [e.id, e.name]))));
  wrap.appendChild(row('タブの位置', '上（横タブ）/ 左（縦タブ・Ctrl+Shift+L）', select(cfg, 'tabLayout', [['top', '上（横タブ）'], ['side', '左（縦タブ）']])));
  wrap.appendChild(row('ブックマークバーを表示', '', toggle(cfg, 'showBookmarksBar')));
  wrap.appendChild(row('全サイトを強制ダークモード', '個別の設定は Ctrl+Shift+D で上書きできます', toggle(cfg, 'forceDark')));

  wrap.appendChild(sec('プライバシー'));
  wrap.appendChild(row('広告・トラッカーをブロック', '主要な広告配信・計測ドメインへの通信を遮断', toggle(cfg, 'adblock')));
  wrap.appendChild(row('HTTPSへ自動アップグレード', 'http:// を https:// に切り替え。非対応サイトは自動でHTTPに戻します', toggle(cfg, 'httpsUpgrade')));
  wrap.appendChild(row('追跡パラメータを自動で除去', 'utm_* / fbclid / gclid などをURLから取り除きます', toggle(cfg, 'stripTracking')));

  wrap.appendChild(sec('パフォーマンス'));
  wrap.appendChild(row('使っていないタブを自動で休止', '休止したタブはメモリを解放し、開くと再読み込みします',
    select(cfg, 'sleepMinutes', [[0, 'しない'], [5, '5分'], [10, '10分'], [15, '15分'], [30, '30分'], [60, '60分']])));

  wrap.appendChild(sec('ウィンドウ'));
  wrap.appendChild(row('常に最前面に表示', '', toggle(cfg, 'alwaysOnTop')));
  const rg = document.createElement('input');
  rg.type = 'range'; rg.min = '30'; rg.max = '100'; rg.step = '10'; rg.value = String(cfg.opacity);
  rg.addEventListener('change', () => set('opacity', Number(rg.value)));
  wrap.appendChild(row('ウィンドウの透明度', '動画を見ながら作業するときなどに', rg));

  wrap.appendChild(sec('集中モード'));
  wrap.appendChild(h('div', 'hint', '集中モード中は、下のサイトを開こうとするとブロックされます（1行に1ドメイン）。'));
  const ta = document.createElement('textarea');
  ta.className = 'cfg';
  ta.value = (cfg.focusSites || []).join('\n');
  ta.addEventListener('blur', () => set('focusSites', ta.value.split('\n').map((s) => s.trim().toLowerCase()).filter(Boolean)));
  const box = h('div'); box.style.padding = '8px 0';
  box.appendChild(ta);
  const go = h('div'); go.style.cssText = 'display:flex;gap:6px;margin-top:8px';
  for (const m of [25, 50]) {
    const b = h('button', 'pbtn primary', `${m}分 集中する`);
    b.addEventListener('click', () => K.send('k:cmd', 'focus.start' + m));
    go.appendChild(b);
  }
  box.appendChild(go);
  wrap.appendChild(box);

  wrap.appendChild(sec('アドレスバーの検索ショートカット'));
  wrap.appendChild(h('div', 'hint', '「yt ねこ」のように、先頭にキーワードを付けて検索するとそのサイトで検索します。'));
  const chips = h('div', 'chips');
  chips.style.padding = '8px 0';
  for (const b of cfg.bangs) { const c = h('span', 'chip'); c.innerHTML = `<b>${b}</b>`; chips.appendChild(c); }
  wrap.appendChild(chips);
  body.replaceChildren(wrap);
}

// ---------- ヘルプ ----------
function renderHelp(cmds, fresh) {
  if (fresh) {
    listShell('help', 'コマンドを検索');
    const hint = h('div', 'hint');
    hint.textContent = 'Ctrl+K でコマンドパレット。> コマンド / @ タブ / * ブックマーク / ? 履歴 で絞り込めます。Alt+1〜9 でワークスペース切替。';
    hint.style.paddingBottom = '6px';
    body.insertBefore(hint, listEl());
  }
  const q = (search.help || '').toLowerCase();
  const list = listEl();
  list.replaceChildren();
  for (const c of cmds) {
    if (q && !`${c.label} ${c.id}`.toLowerCase().includes(q)) continue;
    const r = h('div', 'cmd-row');
    r.appendChild(h('span', 'lb', c.label));
    if (c.keys) r.appendChild(h('kbd', '', c.keys));
    r.addEventListener('click', () => { K.send('k:ov-close'); setTimeout(() => K.send('k:cmd', c.id), 30); });
    list.appendChild(r);
  }
}

// ===========================================================================
// メインプロセスからの通知
// ===========================================================================
K.on('k:ov-show', show);
K.on('k:ov-hide', hideAll);
K.on('k:ov-sel', (sel) => {
  [...L.suggest.children].forEach((c, i) => c.classList.toggle('sel', i === sel));
});

window.addEventListener('DOMContentLoaded', () => K.send('k:ov-ready'));
