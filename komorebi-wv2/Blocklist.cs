namespace Komorebi;

// 広告・トラッカーのドメイン一覧（サフィックス一致で判定）と、URLの追跡パラメータ除去
static class Blocklist
{
    public static readonly HashSet<string> AdHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // Google 系の広告・計測
        "doubleclick.net", "googlesyndication.com", "googleadservices.com", "googletagservices.com",
        "google-analytics.com", "adservice.google.com", "pagead2.googlesyndication.com", "2mdn.net",
        "admob.com", "adsense.com",
        // 大手アドネットワーク
        "adnxs.com", "rubiconproject.com", "pubmatic.com", "openx.net", "criteo.com", "criteo.net",
        "taboola.com", "outbrain.com", "adsrvr.org", "advertising.com", "moatads.com",
        "amazon-adsystem.com", "teads.tv", "smartadserver.com", "casalemedia.com", "bidswitch.net",
        "contextweb.com", "lijit.com", "sharethrough.com", "33across.com", "mathtag.com",
        "adform.net", "adroll.com", "yieldmo.com", "indexww.com", "media.net", "revcontent.com",
        "mgid.com", "zemanta.com", "adcolony.com", "applovin.com", "inmobi.com", "smaato.net",
        "tapad.com", "serving-sys.com", "adition.com", "adtechus.com", "yahoo-ads.com",
        "ads.yahoo.com", "gemini.yahoo.com",
        // 計測・トラッキング
        "scorecardresearch.com", "quantserve.com", "quantcount.com", "hotjar.com", "hotjar.io",
        "mixpanel.com", "segment.io", "segment.com", "bluekai.com", "krxd.net", "demdex.net",
        "everesttech.net", "omtrdc.net", "chartbeat.com", "chartbeat.net", "fullstory.com",
        "mouseflow.com", "crazyegg.com", "luckyorange.com", "clarity.ms", "branch.io",
        "adjust.com", "appsflyer.com", "kochava.com", "singular.net", "bat.bing.com",
        "ads.linkedin.com", "analytics.tiktok.com", "ads-twitter.com", "analytics.twitter.com",
        "px.ads.linkedin.com",
        // 日本の広告ネットワーク
        "i-mobile.co.jp", "microad.jp", "microad.net", "adingo.jp", "ad-stir.com", "impact-ad.jp",
        "nend.net", "fout.jp", "fluct.jp", "logly.co.jp", "popin.cc", "adlantis.jp", "ad-m.asia",
        "adnetwork.jp", "adk2.com", "adtdp.com", "danet.jp", "socdm.com", "yads.c.yimg.jp",
        "ads.yimg.jp", "tg.socdm.com", "gssprt.jp", "dable.io", "yjtag.yahoo.co.jp", "shinobi.jp",
        // その他
        "ads.pubmatic.com", "adsafeprotected.com", "iasds01.com", "doubleverify.com", "zedo.com",
        "exoclick.com", "popads.net", "propellerads.com", "popcash.net", "juicyads.com",
        "trafficjunky.net", "adsterra.com", "clickadu.com",
    };

    // ホスト名が広告ドメイン（またはそのサブドメイン）か
    public static bool IsAdHost(string host)
    {
        var h = (host ?? "").ToLowerInvariant();
        while (h.Length > 0)
        {
            if (AdHosts.Contains(h)) return true;
            var i = h.IndexOf('.');
            if (i < 0) break;
            h = h[(i + 1)..];
        }
        return false;
    }

    // WebView2 のリクエストフィルタ用パターン（ホスト自身とサブドメインの2本）
    public static IEnumerable<string> FilterPatterns()
    {
        foreach (var h in AdHosts)
        {
            if (h.Contains('/')) continue;
            yield return $"*://{h}/*";
            yield return $"*://*.{h}/*";
        }
    }

    static readonly HashSet<string> TrackingParams = new(StringComparer.OrdinalIgnoreCase)
    {
        "utm_source", "utm_medium", "utm_campaign", "utm_term", "utm_content", "utm_id",
        "utm_name", "utm_brand", "fbclid", "gclid", "gclsrc", "dclid", "msclkid", "yclid",
        "mc_cid", "mc_eid", "igshid", "_hsenc", "_hsmi", "ref_src", "ref_url", "twclid",
        "ttclid", "wbraid", "gbraid", "vero_id", "mkt_tok", "oly_enc_id", "oly_anon_id",
        "__s", "rb_clickid", "s_cid", "trk", "trkcampaign",
    };

    // 追跡パラメータを取り除いたURLを返す（変化がなければ元の文字列をそのまま返す）。
    // 残すパラメータは元のエンコードのまま保つ。
    public static string CleanTrackingUrl(string url)
    {
        try
        {
            if (!Uri.TryCreate(url, UriKind.Absolute, out var u)) return url;
            if (u.Scheme != "http" && u.Scheme != "https") return url;
            var q = u.Query;
            if (q.Length <= 1) return url;
            var parts = q.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries);
            var keep = parts.Where(p =>
            {
                var name = Uri.UnescapeDataString(p.Split('=', 2)[0]);
                return !TrackingParams.Contains(name);
            }).ToArray();
            if (keep.Length == parts.Length) return url;
            return u.GetLeftPart(UriPartial.Path) + (keep.Length > 0 ? "?" + string.Join("&", keep) : "") + u.Fragment;
        }
        catch
        {
            return url;
        }
    }
}
