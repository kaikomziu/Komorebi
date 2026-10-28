'use strict';

// メモリ計測用（外部から計測する）。新しいタブ + 外部サイト3つを開いて安定させ、
// 準備完了の目印ファイルを置いてしばらく待つ。
//   npx electron . --selftest dev/selftest-mem.js
const fs = require('fs');
const path = require('path');

module.exports = async (api) => {
  const out = process.env.KMR_OUT || path.join(__dirname, 'out');
  fs.mkdirSync(out, { recursive: true });
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms));
  for (const u of ['https://example.com/', 'https://example.org/', 'https://example.net/']) api.createTab(u);
  await sleep(15000);
  fs.writeFileSync(path.join(out, 'mem-ready.txt'), 'ready');
  await sleep(45000);
  api.app.exit(0);
};
