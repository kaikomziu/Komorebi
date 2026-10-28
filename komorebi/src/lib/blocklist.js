'use strict';

// 広告・トラッカーのドメイン一覧（サフィックス一致で判定）
const AD_HOSTS = new Set([
  // Google 系の広告・計測
  'doubleclick.net', 'googlesyndication.com', 'googleadservices.com', 'googletagservices.com',
  'google-analytics.com', 'adservice.google.com', 'pagead2.googlesyndication.com', '2mdn.net',
  'admob.com', 'adsense.com',
  // 大手アドネットワーク
  'adnxs.com', 'rubiconproject.com', 'pubmatic.com', 'openx.net', 'criteo.com', 'criteo.net',
  'taboola.com', 'outbrain.com', 'adsrvr.org', 'advertising.com', 'moatads.com',
  'amazon-adsystem.com', 'teads.tv', 'smartadserver.com', 'casalemedia.com', 'bidswitch.net',
  'contextweb.com', 'lijit.com', 'sharethrough.com', '33across.com', 'mathtag.com',
  'adform.net', 'adroll.com', 'yieldmo.com', 'indexww.com', 'media.net', 'revcontent.com',
  'mgid.com', 'zemanta.com', 'adcolony.com', 'applovin.com', 'unityads.unity3d.com',
  'inmobi.com', 'smaato.net', 'tapad.com', 'turn.com', 'serving-sys.com', 'adition.com',
  'adtechus.com', 'yahoo-ads.com', 'ads.yahoo.com', 'gemini.yahoo.com',
  // 計測・トラッキング
  'scorecardresearch.com', 'quantserve.com', 'quantcount.com', 'hotjar.com', 'hotjar.io',
  'mixpanel.com', 'segment.io', 'segment.com', 'bluekai.com', 'krxd.net', 'demdex.net',
  'everesttech.net', 'omtrdc.net', 'chartbeat.com', 'chartbeat.net', 'newrelic.com',
  'fullstory.com', 'mouseflow.com', 'crazyegg.com', 'luckyorange.com', 'clarity.ms',
  'branch.io', 'adjust.com', 'appsflyer.com', 'kochava.com', 'singular.net',
  'bat.bing.com', 'ads.linkedin.com', 'analytics.tiktok.com', 'ads-twitter.com',
  'analytics.twitter.com', 'px.ads.linkedin.com', 'facebook-hardware.com',
  // 日本の広告ネットワーク
  'i-mobile.co.jp', 'microad.jp', 'microad.net', 'adingo.jp', 'ad-stir.com', 'impact-ad.jp',
  'nend.net', 'fout.jp', 'fluct.jp', 'logly.co.jp', 'popin.cc', 'yimg.jp/ads',
  'adlantis.jp', 'ad-m.asia', 'adnetwork.jp', 'adk2.com', 'adtdp.com', 'danet.jp',
  'socdm.com', 'yads.c.yimg.jp', 'ads.yimg.jp', 'tg.socdm.com', 'gssprt.jp',
  'dable.io', 'yjtag.yahoo.co.jp', 'ads-twitter.com', 'shinobi.jp',
  // その他
  'ads.pubmatic.com', 'adsafeprotected.com', 'iasds01.com', 'doubleverify.com',
  'zedo.com', 'exoclick.com', 'popads.net', 'propellerads.com', 'popcash.net',
  'juicyads.com', 'trafficjunky.net', 'adsterra.com', 'clickadu.com',
]);

// ホスト名が広告ドメイン（またはそのサブドメイン）かどうか
function isAdHost(host) {
  let h = String(host || '').toLowerCase();
  while (h) {
    if (AD_HOSTS.has(h)) return true;
    const i = h.indexOf('.');
    if (i < 0) break;
    h = h.slice(i + 1);
  }
  return false;
}

// URLに付く追跡用パラメータ
const TRACKING_PARAMS = new Set([
  'utm_source', 'utm_medium', 'utm_campaign', 'utm_term', 'utm_content', 'utm_id',
  'utm_name', 'utm_brand', 'fbclid', 'gclid', 'gclsrc', 'dclid', 'msclkid', 'yclid',
  'mc_cid', 'mc_eid', 'igshid', '_hsenc', '_hsmi', 'ref_src', 'ref_url', 'twclid',
  'ttclid', 'wbraid', 'gbraid', 'vero_id', 'mkt_tok', 'oly_enc_id', 'oly_anon_id',
  '__s', 'rb_clickid', 's_cid', 'trk', 'trkcampaign',
]);

// 追跡パラメータを取り除いたURLを返す（変化がなければ元の文字列をそのまま返す）
function cleanTrackingUrl(urlStr) {
  try {
    const u = new URL(urlStr);
    if (u.protocol !== 'http:' && u.protocol !== 'https:') return urlStr;
    let changed = false;
    for (const key of [...u.searchParams.keys()]) {
      if (TRACKING_PARAMS.has(key.toLowerCase())) {
        u.searchParams.delete(key);
        changed = true;
      }
    }
    return changed ? u.toString() : urlStr;
  } catch {
    return urlStr;
  }
}

module.exports = { isAdHost, cleanTrackingUrl, AD_HOSTS };
