'use strict';

// 動作確認用セルフテスト。  npx electron . --selftest dev/selftest.js
// 出力先は環境変数 KMR_OUT（結果JSONとスクリーンショットを保存）。
// 一時プロファイルで動くので、普段の設定やセッションには影響しない。
// （クリップボードだけは触るので、テスト後に元へ戻す）
const fs = require('fs');
const path = require('path');
const http = require('http');
const { clipboard } = require('electron');

const ARTICLE = '<html><head><title>テスト記事</title></head><body><nav>メニュー<a href="#">リンク</a></nav>' +
  '<div id="promo" style="height:60px;background:#fc0;margin:10px">広告バナー（消す対象）</div>' +
  '<article><h1>木漏れ日のなかで</h1>' +
  Array.from({ length: 6 }, (_, i) => `<p>これは第${i + 1}段落です。リーダーモードが本文を正しく抽出できるかを確かめるための、十分に長い文章をここに書いています。日本語の文章でも読了時間の計算ができます。</p>`).join('') +
  '</article><footer>フッター</footer>' +
  '<img src="https://ad.doubleclick.net/pixel.gif"><img src="https://www.google-analytics.com/collect.gif"></body></html>';

module.exports = async (api) => {
  const out = process.env.KMR_OUT || path.join(__dirname, 'out');
  fs.mkdirSync(out, { recursive: true });
  const results = [];
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const check = (name, ok, detail = '') => {
    results.push({ name, ok: !!ok, detail: String(detail) });
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  -> ' + detail : ''}`);
  };
  const shot = async (wc, name) => {
    try {
      fs.writeFileSync(path.join(out, name + '.png'), (await wc.capturePage()).toPNG());
    } catch (e) {
      console.log('撮影失敗', name, e.message);
    }
  };
  const waitLoad = async (tab, ms = 10000) => {
    const t0 = Date.now();
    await sleep(250);
    while (Date.now() - t0 < ms) {
      if (tab.view && !tab.loading && tab.view.webContents.getURL()) return true;
      await sleep(150);
    }
    return false;
  };
  const js = (tab, code) => tab.view.webContents.executeJavaScript(code);
  const win = api.getWin();
  const ov = api.getOverlay();
  // ウィンドウの表示状態（キャプチャ失敗の切り分け用）
  const winInfo = () => {
    const b = win.getContentBounds();
    return `min=${win.isMinimized()} vis=${win.isVisible()} focus=${win.isFocused()} content=${b.width}x${b.height}`;
  };

  // クリップボードを退避
  const prevText = clipboard.readText();
  const prevImg = clipboard.readImage();

  // ローカルのテスト用HTTPサーバー（127.0.0.1 はHTTPS化の対象外）
  const server = http.createServer((req, res) => {
    if (req.url.startsWith('/article')) {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
      res.end(ARTICLE);
    } else if (req.url.startsWith('/tall')) {
      res.writeHead(200, { 'content-type': 'text/html; charset=utf-8' });
      res.end('<html><body style="margin:0"><div style="height:3000px;background:linear-gradient(#e33,#33e)">縦長ページ</div></body></html>');
    } else if (req.url.startsWith('/file')) {
      res.writeHead(200, { 'content-type': 'application/octet-stream', 'content-disposition': 'attachment; filename="kmr-test.bin"' });
      res.end(Buffer.alloc(4096, 7));
    } else {
      res.writeHead(404);
      res.end('not found');
    }
  });
  await new Promise((r) => server.listen(0, '127.0.0.1', r));
  const base = `http://127.0.0.1:${server.address().port}`;
  api.app.setPath('downloads', out);
  api.app.setPath('pictures', out);

  try {
    // --- 起動直後の状態 ---------------------------------------------------
    await sleep(1000);
    check('起動時にタブが1つできる', api.tabs.length === 1, `tabs=${api.tabs.length}`);
    const first = api.getActive();
    check('新しいタブページを表示', first && first.url.includes('newtab.html'), first && first.url);
    check('外枠の高さが報告されている', api.state().ui.top > 60, JSON.stringify(api.state().ui));
    await sleep(800);
    await shot(win.webContents, '01-ui-top');
    await shot(first.view.webContents, '02-newtab');

    const sc = await js(first, "document.querySelectorAll('#grid a.sc').length");
    check('新しいタブにショートカットが並ぶ', sc >= 4, `count=${sc}`);
    const api2 = await js(first, "typeof window.komorebiNewtab === 'object'");
    check('新しいタブにだけ専用APIが公開される', api2 === true);

    // 新しいタブの検索ボックス → メインのURL解釈（キーワード対応）→ 遷移
    await js(first, "komorebiNewtab.go('example.com'); 1");
    check('新しいタブの検索から遷移できる', await waitLoad(first, 12000) && first.view.webContents.getURL().startsWith('https://example.com'), first.view.webContents.getURL());
    const leak = await js(first, "typeof window.komorebiNewtab + '/' + typeof window.komorebi");
    check('外部サイトにはAPIが一切公開されない', leak === 'undefined/undefined', leak);

    // --- URL解釈 ---------------------------------------------------------
    check('「yt ねこ」でYouTube検索', api.toURL('yt ねこ').startsWith('https://www.youtube.com/results?search_query='), api.toURL('yt ねこ'));
    check('ドメインはhttpsで開く', api.toURL('example.com') === 'https://example.com');
    check('単語はGoogle検索', api.toURL('foo bar').startsWith('https://www.google.com/search?q='));
    check('localhostはhttp', api.toURL('localhost:3000') === 'http://localhost:3000');

    // --- パレット --------------------------------------------------------
    const pc = api.paletteQuery('分割');
    check('パレットでコマンド検索', pc.some((i) => i.id === 'view.split'));
    const calc = api.paletteQuery('1+2*3');
    check('パレットで計算', calc[0] && calc[0].kind === 'calc' && calc[0].value === '7', calc[0] && calc[0].title);
    check('@でタブだけ検索', api.paletteQuery('@example').every((i) => i.kind === 'tab'));
    api.togglePalette();
    await sleep(700);
    check('パレット表示中', api.state().overlayMode === 'palette');
    // 実際にオーバーレイ上で入力 → Enter してコマンドが走るか
    await ov.webContents.executeJavaScript(`(() => { const i = document.getElementById('pal-input'); i.value = 'ダウンロード'; i.dispatchEvent(new Event('input')); })()`);
    await sleep(500);
    await shot(ov.webContents, '03-palette');
    await ov.webContents.executeJavaScript(`document.getElementById('pal-input').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))`);
    await sleep(700);
    check('パレットからコマンドを実行できる（ダウンロードパネルが開く）', api.state().overlayMode === 'panel');
    api.hideOverlay(false);

    // --- 実ページ（HTTP）での機能 ----------------------------------------
    const aid = api.createTab(base + '/article');
    const at = api.tabs.find((t) => t.id === aid);
    await waitLoad(at);
    await sleep(1200);
    check('広告ドメインへの通信をブロック', at.blocked >= 2, `blocked=${at.blocked}`);

    api.runCommand('tools.reader');
    await sleep(1500);
    const rtxt = await js(at, 'document.body.innerText');
    check('リーダーモードに切替', at.reader === true, `reader=${at.reader}`);
    check('本文が抽出される（ナビ・フッターは除く）', rtxt.includes('第3段落') && !rtxt.includes('メニュー') && !rtxt.includes('フッター'), rtxt.slice(0, 50).replace(/\n/g, ' '));
    check('読了時間が表示される', /約\d+分/.test(rtxt));
    await shot(at.view.webContents, '04-reader');
    api.runCommand('tools.reader');
    await waitLoad(at);
    await sleep(500);
    check('リーダーモードを解除（元のページに戻る）', at.reader === false && (await js(at, '!!document.getElementById("promo")')));

    // 要素ブロッカー: 実際にクリックして消す
    console.log('INFO  ウィンドウ状態(ブロッカー前):', winInfo(), JSON.stringify(at.view.getBounds()));
    api.runCommand('tools.picker');
    await sleep(500);
    const r = JSON.parse(await js(at, 'JSON.stringify(document.getElementById("promo").getBoundingClientRect())'));
    const cx = Math.round(r.x + r.width / 2);
    const cy = Math.round(r.y + r.height / 2);
    const wc = at.view.webContents;
    wc.sendInputEvent({ type: 'mouseMove', x: cx, y: cy });
    await sleep(200);
    wc.sendInputEvent({ type: 'mouseDown', x: cx, y: cy, button: 'left', clickCount: 1 });
    wc.sendInputEvent({ type: 'mouseUp', x: cx, y: cy, button: 'left', clickCount: 1 });
    await sleep(900);
    const rules = api.sites.data.hidden['127.0.0.1'] || [];
    check('要素ブロッカーでセレクタが保存される', rules.length === 1 && rules[0].includes('promo'), JSON.stringify(rules));
    check('クリックした要素が消える', (await js(at, 'getComputedStyle(document.getElementById("promo")).display')) === 'none');
    wc.reload();
    await waitLoad(at);
    await sleep(800);
    check('再読み込み後も非表示が維持される', (await js(at, 'getComputedStyle(document.getElementById("promo")).display')) === 'none');
    api.runCommand('tools.unhide');
    await waitLoad(at);
    await sleep(600);
    check('非表示を解除できる', (await js(at, 'getComputedStyle(document.getElementById("promo")).display')) !== 'none');

    // 強制ダークモード
    api.runCommand('tools.dark');
    await sleep(700);
    const filt = await js(at, 'getComputedStyle(document.documentElement).filter');
    check('サイト別ダークモードが効く', filt.includes('invert'), filt);
    await shot(at.view.webContents, '05-dark');
    wc.reload();
    await waitLoad(at);
    await sleep(800);
    check('ダークモードがサイトごとに記憶される', (await js(at, 'getComputedStyle(document.documentElement).filter')).includes('invert'));
    api.runCommand('tools.dark');
    await sleep(500);
    check('ダークモードを解除できる', !(await js(at, 'getComputedStyle(document.documentElement).filter')).includes('invert'));

    // サイトごとのズーム記憶
    api.runCommand('view.zoomIn');
    await sleep(200);
    check('ズームがサイトごとに保存される', Math.abs((api.sites.data.zoom['127.0.0.1'] || 0) - 1.1) < 0.001, JSON.stringify(api.sites.data.zoom));
    wc.reload();
    await waitLoad(at);
    await sleep(800);
    check('再読み込み後もズームが復元される', Math.abs(wc.getZoomFactor() - 1.1) < 0.001, `zoom=${wc.getZoomFactor()}`);
    api.runCommand('view.zoomReset');

    // ページ内検索（UIの入力 → メイン → 件数表示まで通す）
    api.activateTab(aid);
    await sleep(300);
    api.runCommand('view.find');
    await sleep(400);
    await win.webContents.executeJavaScript(`(() => { const i = document.getElementById('find-input'); i.value = '段落'; i.dispatchEvent(new Event('input')); })()`);
    await sleep(900);
    const cnt = await win.webContents.executeJavaScript(`document.getElementById('find-count').textContent`);
    check('ページ内検索で件数が表示される', /1\s*\/\s*6/.test(cnt), cnt);
    await shot(win.webContents, '05b-find');
    await win.webContents.executeJavaScript(`document.getElementById('find-close').click()`);
    await sleep(300);

    // スクリーンショット（縦長ページで「ページ全体」が本当に全体か）
    const sizeOf = (f) => require('electron').nativeImage.createFromPath(f).getSize();
    const shotDir = path.join(out, 'Komorebi');
    const tid = api.createTab(base + '/tall');
    await waitLoad(api.tabs.find((t) => t.id === tid));
    api.runCommand('tools.shot');
    await sleep(900);
    api.runCommand('tools.shotFull');
    await sleep(2200);
    const shots = fs.existsSync(shotDir) ? fs.readdirSync(shotDir).filter((f) => f.endsWith('.png')).sort() : [];
    check('スクリーンショットが2枚保存される', shots.length >= 2, `${shots.length}枚 / ${winInfo()}`);
    if (shots.length >= 2) {
      const vis = sizeOf(path.join(shotDir, shots[shots.length - 2]));
      const full = sizeOf(path.join(shotDir, shots[shots.length - 1]));
      check('「ページ全体」は表示範囲より縦に長い', full.height > vis.height * 1.5, `表示範囲=${vis.width}x${vis.height} 全体=${full.width}x${full.height}`);
    }
    api.closeTab(tid);

    // ダウンロード（空タブは自動で閉じる）
    const tabsBeforeDl = api.tabs.length;
    api.createTab(base + '/file');
    await sleep(2800);
    const dl = api.downloads[0];
    check('ダウンロードが完了して記録される', dl && dl.state === 'completed' && fs.existsSync(dl.path), dl && `${dl.name} ${dl.state}`);
    check('ダウンロード用の空タブが残らない', api.tabs.length === tabsBeforeDl, `before=${tabsBeforeDl} after=${api.tabs.length}`);

    // --- ネットワーク系（外部接続が必要） --------------------------------
    const nid = api.createTab('https://example.com/?utm_source=news&fbclid=abc&keep=1');
    const nt = api.tabs.find((t) => t.id === nid);
    if (await waitLoad(nt, 12000)) {
      const u = nt.view.webContents.getURL();
      check('追跡パラメータを自動除去（keepは残る）', u === 'https://example.com/?keep=1', u);
    }
    const hid = api.createTab('http://example.com/');
    const ht = api.tabs.find((t) => t.id === hid);
    if (await waitLoad(ht, 12000)) {
      const u = ht.view.webContents.getURL();
      check('HTTPをHTTPSへ自動アップグレード', u.startsWith('https://'), u);
    }

    // --- 分割表示 --------------------------------------------------------
    api.activateTab(nid);
    await sleep(400);
    api.runCommand('view.split');
    await sleep(700);
    const st = api.state();
    check('分割表示が有効になる', !!st.split, JSON.stringify(st.split));
    if (st.split) {
      const A = api.tabs.find((t) => t.id === st.split.a);
      const B = api.tabs.find((t) => t.id === st.split.b);
      const ba = A.view.getBounds();
      const bb = B.view.getBounds();
      check('2つのビューが左右に並ぶ', ba.x < bb.x && ba.width > 100 && bb.width > 100 && ba.y === bb.y, `${JSON.stringify(ba)} | ${JSON.stringify(bb)}`);
      check('両方のビューが表示されている', typeof A.view.getVisible !== 'function' || (A.view.getVisible() && B.view.getVisible()));
    }
    await shot(win.webContents, '06-ui-split');
    api.runCommand('view.split');
    await sleep(300);
    check('分割表示を解除できる', !api.state().split);

    // --- タブ休止 --------------------------------------------------------
    api.activateTab(aid);
    await sleep(300);
    check('バックグラウンドのタブを休止できる', api.sleepTab(nt) && nt.view === null);
    check('アクティブなタブは休止されない', api.sleepTab(api.getActive()) === false);
    api.activateTab(nid);
    await sleep(500);
    check('休止タブを開くと復帰する', nt.view !== null);

    // --- ワークスペース --------------------------------------------------
    api.settings.data.workspaces.push({ id: 'wtest', name: '仕事', color: '#f28b82' });
    const before = api.wsOrdered().length;
    api.runCommand('ws.switch:wtest');
    await sleep(600);
    check('ワークスペースを切り替えると別のタブ群になる', api.state().currentWs === 'wtest' && api.wsOrdered().length === 1 && before > 1, `ws=${api.state().currentWs} tabs=${api.wsOrdered().length}`);
    api.runCommand('ws.switch:w1');
    await sleep(500);
    check('元のワークスペースに戻る', api.state().currentWs === 'w1' && api.wsOrdered().length === before);

    // --- 閉じたタブの復元 ------------------------------------------------
    const cur = api.tabs.length;
    api.closeTab(nid);
    await sleep(300);
    api.runCommand('tab.restore');
    await sleep(500);
    check('閉じたタブを復元できる', api.tabs.length === cur, `tabs=${api.tabs.length}`);

    // --- キーボードショートカット（実際のキー入力イベント） ----------------
    const swc = api.getActive().view.webContents;
    const nTabs = api.tabs.length;
    swc.focus();
    swc.sendInputEvent({ type: 'keyDown', keyCode: 'T', modifiers: ['control'] });
    swc.sendInputEvent({ type: 'keyUp', keyCode: 'T', modifiers: ['control'] });
    await sleep(700);
    check('Ctrl+T で新しいタブが開く', api.tabs.length === nTabs + 1, `${nTabs}->${api.tabs.length}`);
    api.getActive().view.webContents.sendInputEvent({ type: 'keyDown', keyCode: 'W', modifiers: ['control'] });
    await sleep(600);
    check('Ctrl+W でタブが閉じる', api.tabs.length === nTabs, `tabs=${api.tabs.length}`);
    api.getActive().view.webContents.sendInputEvent({ type: 'keyDown', keyCode: 'K', modifiers: ['control'] });
    await sleep(600);
    check('Ctrl+K でパレットが開く', api.state().overlayMode === 'palette');
    api.hideOverlay(false);

    // --- ブックマーク・履歴 ---------------------------------------------
    const nb = api.bookmarks.data.length;
    api.activateTab(ht.id);
    await sleep(300);
    api.runCommand('bookmark.toggle');
    await sleep(200);
    check('ブックマークを追加できる', api.bookmarks.data.length === nb + 1, `${nb}->${api.bookmarks.data.length}`);
    check('履歴が記録される', api.history.data.length >= 3, `count=${api.history.data.length}`);

    // --- アドレスバー候補 ------------------------------------------------
    const sug = api.buildSuggestions('example');
    check('候補に検索・タブ・履歴が出る', sug.length >= 2 && sug[0].kind === 'search', sug.map((s) => s.kind).join(','));
    api.updateSuggest('exa', { left: 120, top: 46, bottom: 78, width: 700, height: 32 });
    await sleep(600);
    check('候補ドロップダウンが表示される', api.state().overlayMode === 'suggest');
    await shot(ov.webContents, '07-suggest');
    api.hideOverlay(false);

    // --- パネル ----------------------------------------------------------
    for (const [name, file] of [['settings', '08-panel-settings'], ['history', '09-panel-history'], ['help', '10-panel-help'], ['downloads', '11-panel-downloads']]) {
      api.openPanel(name);
      await sleep(700);
      check(`パネル「${name}」が開く`, api.state().overlayMode === 'panel');
      await shot(ov.webContents, file);
      api.hideOverlay(false);
    }

    // --- 入力ダイアログ --------------------------------------------------
    let pr = api.askPrompt({ title: 'ワークスペースを追加', label: '名前', value: '勉強', confirmLabel: '作成' });
    await sleep(600);
    check('入力ダイアログが表示される', api.state().overlayMode === 'prompt');
    await shot(ov.webContents, '12-prompt');
    await ov.webContents.executeJavaScript(`document.getElementById('pr-input').value = 'テスト入力'; document.getElementById('pr-ok').click()`);
    check('入力した文字が返る', (await pr) === 'テスト入力');
    pr = api.askPrompt({ title: '確認', label: 'x', input: false });
    await sleep(500);
    await ov.webContents.executeJavaScript(`document.getElementById('pr-cancel').click()`);
    check('キャンセルするとnullが返る', (await pr) === null);

    // --- アドレスバー操作（入力 → 候補 → ↓ → Enter → 遷移）-----------------
    const otid = api.createTab('about:blank');
    await sleep(500);
    await win.webContents.executeJavaScript(`(() => { const a = document.getElementById('address'); a.focus(); a.value = 'example.com/?omni=1'; a.dispatchEvent(new Event('input')); })()`);
    await sleep(600);
    check('入力すると候補が出る', api.state().overlayMode === 'suggest');
    await win.webContents.executeJavaScript(`document.getElementById('address').dispatchEvent(new KeyboardEvent('keydown', { key: 'ArrowDown', bubbles: true }))`);
    await sleep(400);
    const filled = await win.webContents.executeJavaScript(`document.getElementById('address').value`);
    check('↓で候補を選ぶとアドレスバーに反映される', filled.startsWith('https://example.com'), filled);
    await win.webContents.executeJavaScript(`document.getElementById('address').dispatchEvent(new KeyboardEvent('keydown', { key: 'Enter', bubbles: true }))`);
    const ot = api.tabs.find((t) => t.id === otid);
    await waitLoad(ot, 12000);
    check('Enterで遷移する', ot.view.webContents.getURL() === 'https://example.com/?omni=1', ot.view.webContents.getURL());
    api.closeTab(otid);

    // --- 集中モード ------------------------------------------------------
    api.startFocus(1);
    check('集中モード中はYouTubeをブロック判定', api.isFocusBlocked('https://www.youtube.com/watch?v=1'));
    check('集中モード中でも他サイトは通る', !api.isFocusBlocked('https://example.com/'));
    const fid = api.createTab('https://www.youtube.com/');
    const ft = api.tabs.find((t) => t.id === fid);
    await sleep(2500);
    const furl = ft.view ? ft.view.webContents.getURL() : '';
    check('集中モード中は遷移が集中画面に差し替わる', furl.includes('focus.html'), furl.slice(-60));
    await shot(ft.view.webContents, '13-focus');

    // --- 縦タブレイアウト ------------------------------------------------
    api.activateTab(aid);
    api.settings.data.tabLayout = 'side';
    api.scheduleState();
    await sleep(1000);
    check('縦タブでは左にサイドバー分の余白ができる', api.state().ui.left > 150, JSON.stringify(api.state().ui));
    await shot(win.webContents, '14-ui-side');
    api.settings.data.sidebarCollapsed = true;
    api.scheduleState();
    await sleep(800);
    check('サイドバーを折りたためる', api.state().ui.left < 100, `left=${api.state().ui.left}`);
    await shot(win.webContents, '15-ui-side-collapsed');
    api.settings.data.tabLayout = 'top';
    api.settings.data.sidebarCollapsed = false;
    api.scheduleState();
    await sleep(600);
    check('横タブに戻せる', api.state().ui.left === 0);
  } catch (e) {
    check('テスト中に例外が発生しない', false, e && e.stack ? e.stack.split('\n').slice(0, 3).join(' | ') : e);
  }

  // 後片付け
  server.close();
  if (!prevImg.isEmpty()) clipboard.writeImage(prevImg);
  else clipboard.writeText(prevText);

  api.flush(); // 復元テスト用にセッションを書き出す
  const failed = results.filter((r) => !r.ok);
  fs.writeFileSync(path.join(out, 'results.json'), JSON.stringify({ total: results.length, failed: failed.length, results }, null, 2));
  console.log(`\n== ${results.length - failed.length}/${results.length} PASS ==`);
  setTimeout(() => api.app.exit(failed.length ? 1 : 0), 300);
};
