'use strict';

const fs = require('fs');
const path = require('path');

// JSONファイルに保存する小さなストア。書き込みは遅延させてまとめる。
const instances = new Set();

class Store {
  constructor(file, defaults) {
    this.file = file;
    this.timer = null;
    let loaded = null;
    try {
      loaded = JSON.parse(fs.readFileSync(file, 'utf-8'));
    } catch {
      loaded = null;
    }
    // 既定値に保存済みの値を重ねる（新しい設定項目が増えても壊れない）
    this.data = loaded === null
      ? structuredClone(defaults)
      : Array.isArray(defaults)
        ? loaded
        : { ...structuredClone(defaults), ...loaded };
    instances.add(this);
  }

  get(key) {
    return this.data[key];
  }

  set(key, value) {
    this.data[key] = value;
    this.save();
  }

  save() {
    clearTimeout(this.timer);
    this.timer = setTimeout(() => this.flush(), 600);
  }

  flush() {
    clearTimeout(this.timer);
    try {
      fs.mkdirSync(path.dirname(this.file), { recursive: true });
      fs.writeFileSync(this.file, JSON.stringify(this.data, null, 2));
    } catch (e) {
      console.error('ストア保存に失敗', this.file, e);
    }
  }
}

// 終了時に未保存の変更をすべて書き出す
function flushAll() {
  for (const s of instances) s.flush();
}

module.exports = { Store, flushAll };
