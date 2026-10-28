using System.Reflection;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Komorebi;

static class WebViewExt
{
    // WinForms 版の WebView2 は CoreWebView2Controller を公開していない。
    // キー入力の横取り(AcceleratorKeyPressed)やフォーカス通知に必要なので、非公開フィールドから取り出す。
    // ※ Microsoft.Web.WebView2 のバージョンを上げるときは、このフィールド名が残っているか確認する。
    static readonly FieldInfo Field =
        typeof(WebView2).GetField("_coreWebView2Controller", BindingFlags.NonPublic | BindingFlags.Instance);

    public static CoreWebView2Controller Ctl(this WebView2 v)
        => (CoreWebView2Controller)Field?.GetValue(v);
}
