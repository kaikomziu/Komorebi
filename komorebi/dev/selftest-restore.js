'use strict';

// セッション復元の確認（selftest.js の直後に KMR_KEEP=1 で実行する）
//   KMR_KEEP=1 npx electron . --selftest dev/selftest-restore.js
const fs = require('fs');
const path = require('path');

module.exports = async (api) => {
  const out = process.env.KMR_OUT || path.join(__dirname, 'out');
  fs.mkdirSync(out, { recursive: true });
  const results = [];
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  const check = (name, ok, detail = '') => {
    results.push({ name, ok: !!ok });
    console.log(`${ok ? 'PASS' : 'FAIL'}  ${name}${detail ? '  -> ' + detail : ''}`);
  };

  await sleep(1500);
  const awake = api.tabs.filter((t) => t.view);
  check('前回のタブが復元される', api.tabs.length >= 5, `tabs=${api.tabs.length}`);
  check('開いているタブ以外は休止状態で復元される（起動が軽い）', awake.length === 1, `awake=${awake.length}`);
  check('アクティブなタブが決まっている', !!api.getActive() && !!api.getActive().view);
  check('ワークスペースが復元される', api.settings.data.workspaces.some((w) => w.id === 'wtest'));
  check('ブックマークが保存されている', api.bookmarks.data.length >= 5, `count=${api.bookmarks.data.length}`);
  check('履歴が保存されている', api.history.data.length >= 3, `count=${api.history.data.length}`);
  check('サイトごとの設定（非表示要素・ズーム）が保存されている', !!api.sites.data.zoom || !!api.sites.data.hidden);

  const slept = api.tabs.find((t) => !t.view && /^https?:/.test(t.url));
  if (slept) {
    api.activateTab(slept.id);
    await sleep(2500);
    check('休止タブを開くと読み込まれる', !!slept.view && slept.view.webContents.getURL().startsWith('http'), slept.url);
  }

  const failed = results.filter((r) => !r.ok);
  console.log(`\n== ${results.length - failed.length}/${results.length} PASS ==`);
  setTimeout(() => api.app.exit(failed.length ? 1 : 0), 300);
};
