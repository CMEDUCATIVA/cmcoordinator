using System;
using System.ComponentModel;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using CefSharp;
using CefSharp.Wpf;
using OpenProject.Browser.Services;
using OpenProject.Browser.Settings;

namespace OpenProject.Browser.WebViewIntegration
{
  public sealed class BrowserManager
  {
    private readonly JavaScriptBridge _javaScriptBridge;
    private readonly BcfierJavascriptInterop _javascriptInterop;
    private readonly ChromiumWebBrowser _chromiumWebBrowser;
    private readonly IDownloadHandler _downloadHandler;
    private readonly IRequestHandler _requestHandler;
    private readonly ILifeSpanHandler _lifeSpanHandler;
    public ChromiumWebBrowser Browser { get; private set; }

    public BrowserManager(
      JavaScriptBridge javaScriptBridge,
      BcfierJavascriptInterop javascriptInterop,
      ChromiumWebBrowser chromiumWebBrowser,
      IDownloadHandler downloadHandler,
      IRequestHandler requestHandler,
      ILifeSpanHandler lifeSpanHandler)
    {
      _javaScriptBridge = javaScriptBridge;
      _javascriptInterop = javascriptInterop;
      _chromiumWebBrowser = chromiumWebBrowser;
      _downloadHandler = downloadHandler;
      _requestHandler = requestHandler;
      _lifeSpanHandler = lifeSpanHandler;
    }

    public void Initialize()
    {
      Browser = _chromiumWebBrowser;

      // Remove the "Revit Add-In settings" div on every page
      Browser.FrameLoadEnd += BrowserOnFrameLoadEnd_RemoveRevitSettingsDiv;

      Browser.DownloadHandler = _downloadHandler;
      // This handles checking of valid urls, otherwise they're opened in
      // the system browser
      Browser.RequestHandler = _requestHandler;
      try
      {
        Serilog.Log.Information("Browser RequestHandler set to {Type}", _requestHandler?.GetType().FullName);
      }
      catch { }
      // This one prevents popups or additional browser windows
      Browser.LifeSpanHandler = _lifeSpanHandler;

      _javaScriptBridge.SetWebBrowser(Browser);

      Browser.LoadingStateChanged += (s, e) =>
      {
        if (!e.IsLoading) // Not loading means the load is complete
        {
          Application.Current.Dispatcher.Invoke(async () => { await InitializeRevitBridgeIfNotPresentAsync(); });
        }
      };

      // Save all page changes, so that the user can resume to the same
      // page as last time she used the add-in.
      DependencyPropertyDescriptor
        .FromProperty(ChromiumWebBrowser.AddressProperty, typeof(ChromiumWebBrowser))
        .AddValueChanged(Browser, (s, e) =>
        {
          var newUrl = Browser.Address;
          // Don't save local file addresses such as the settings page.
          if (newUrl.StartsWith("http"))
          {
            ConfigurationHandler.SaveLastVisitedPage(Browser.Address);
          }
        });

      // Set the initial page after the bridge and navigation handlers are ready.
      Browser.Address = EmbeddedLandingPageHandler.GetEmbeddedLandingPageIndexUrl();
    }

    // Injects JS after the main frame finishes loading to remove the target div
    private void BrowserOnFrameLoadEnd_RemoveRevitSettingsDiv(object sender, FrameLoadEndEventArgs e)
    {
      const string js = @"
(function(){
  // Inject tag chips styling (small colored circles)
  try {
    if (!document.getElementById('rp-tag-chip-style')) {
      const style = document.createElement('style');
      style.id = 'rp-tag-chip-style';
      style.textContent = `
        .rp-tag-chips{display:flex;gap:6px;align-items:center;flex-wrap:wrap;min-height:10px}
        .rp-tag-chip{width:8px;height:8px;border-radius:50%;background:var(--rp-tag-color);display:inline-block;border:1px solid rgba(0,0,0,.15)}
      `;
      document.head && document.head.appendChild(style);
    }
  } catch(_) {}

  const applyTagChips = () => {
    try {
      document.querySelectorAll('td[data-field=""tags""]').forEach(td => {
        const raw = String(td.getAttribute('data-value') || td.textContent || '').trim();
        if (!raw) return;
        if (td.dataset.rpTagsApplied === '1' && td.dataset.rpTagsValue === raw) return;
        td.dataset.rpTagsApplied = '1';
        td.dataset.rpTagsValue = raw;
        const tags = raw.split(',').map(t => t.trim()).filter(Boolean);
        td.textContent = '';
        const wrap = document.createElement('div');
        wrap.className = 'rp-tag-chips';
        const hash = (s) => {
          let h = 0;
          for (let i = 0; i < s.length; i++) h = (h * 31 + s.charCodeAt(i)) >>> 0;
          return h;
        };
        tags.forEach(tag => {
          const chip = document.createElement('span');
          chip.className = 'rp-tag-chip';
          chip.title = tag;
          const hue = hash(tag) % 360;
          chip.style.setProperty('--rp-tag-color', `hsl(${hue}, 70%, 55%)`);
          wrap.appendChild(chip);
        });
        td.appendChild(wrap);
      });
    } catch(_) {}
  };

  const selector = '[data-name=""Revit Add-In settings""]';
  const removeAll = () => {
    try {
      document.querySelectorAll(selector).forEach(el => el.remove());
    } catch(_) {}
  };
  try { console.log('[RevitBridge] injected in frame', window.location && window.location.href); } catch(_) {}
  // Initial removal
  removeAll();
  // Initial tag chips render
  applyTagChips();
  // Keep it removed if the app re-renders it later (SPA)
  if (!window.__rp_removeRevitSettingsObserver) {
    try {
      const obs = new MutationObserver(() => removeAll());
      const root = document.documentElement || document.body;
      if (root) {
        obs.observe(root, {childList: true, subtree: true});
        window.__rp_removeRevitSettingsObserver = obs;
      }
    } catch(_) {}
  }

  // Re-apply tag chips on DOM updates (SPA)
  if (!window.__rp_tagChipsObserver) {
    try {
      const obsTags = new MutationObserver(() => applyTagChips());
      const rootTags = document.documentElement || document.body;
      if (rootTags) {
        obsTags.observe(rootTags, {childList: true, subtree: true});
        window.__rp_tagChipsObserver = obsTags;
      }
    } catch(_) {}
  }

  // Keep confirm dialog, but on accept, notify the add-in to remove the pin.
  try {
    if (!window.__rp_confirmHooked) {
      window.__rp_confirmHooked = true;
      window.__rp_lastDeleteClickTs = 0;
      const isDeleteTarget = (el) => {
        if (!el) return false;
        const btn = el.closest('button, a, [role=""button""]');
        if (!btn) return false;
        const text = (btn.getAttribute('aria-label') || btn.title || btn.innerText || '').toLowerCase();
        const cls = (btn.className || '').toString().toLowerCase();
        const dataQa = (btn.getAttribute('data-qa') || btn.getAttribute('data-test') || '').toLowerCase();
        const hasTrashIcon = btn.querySelector('i[class*=""trash""], svg[aria-label*=""trash""], svg[title*=""trash""], span[class*=""trash""]');
        return hasTrashIcon || /(delete|remove|eliminar|borrar|trash)/.test(text + ' ' + cls + ' ' + dataQa);
      };

      document.addEventListener('click', (e) => {
        if (isDeleteTarget(e.target)) {
          window.__rp_lastDeleteClickTs = Date.now();
          try {
            const btn = e.target.closest('button, a, [role=""button""]');
            const info = {
              href: String(window.location && window.location.href || ''),
              label: String(btn && (btn.getAttribute('aria-label') || btn.title || btn.innerText) || ''),
              cls: String(btn && btn.className || ''),
              dataQa: String(btn && (btn.getAttribute('data-qa') || btn.getAttribute('data-test') || '') || '')
            };
            console.log('[RevitBridge] delete click detected', info);
          } catch(_) {}
        }
      }, true);

      const originalConfirm = window.confirm.bind(window);
      window.confirm = function(msg) {
        const result = originalConfirm(msg);
        try {
          const recentDelete = Date.now() - (window.__rp_lastDeleteClickTs || 0) < 1500;
          const isDeleteMsg = /(seguro|delete|remove|eliminar|borrar)/i.test(String(msg));
          try { console.log('[RevitBridge] confirm called', { msg: String(msg || ''), result, recentDelete, isDeleteMsg }); } catch(_) {}
          if (result && isDeleteMsg && window.RevitBridge) {
            const fn = window.RevitBridge.SendMessageToRevit || window.RevitBridge.sendMessageToRevit;
            if (typeof fn === 'function') {
              const href = String(window.location && window.location.href || '');
              let wpId = null;
              try {
                const m = /\/work_packages\/(\d+)/.exec(href);
                if (m && m[1]) wpId = m[1];
              } catch(_) {}
              const payload = JSON.stringify({
                url: href,
                workPackageId: wpId,
                confirmMessage: String(msg || ''),
                ts: Date.now()
              });
              try { console.log('[RevitBridge] PinRemove confirm accepted', payload); } catch(_) {}
              fn('PinRemove', '0', payload);
            }
          }
        } catch(_) {}
        return result;
      };
    }
  } catch(_) {}

  // Fallback: hook network DELETE to viewpoints and notify add-in.
  try {
    if (!window.__rp_networkHooked) {
      window.__rp_networkHooked = true;
      window.__rp_lastDeleteNotifyTs = 0;
      window.__rp_lastDeleteUrl = '';

      const notifyDelete = (deleteUrl) => {
        try {
          const now = Date.now();
          if (deleteUrl && deleteUrl === window.__rp_lastDeleteUrl && (now - window.__rp_lastDeleteNotifyTs) < 1000) return;
          window.__rp_lastDeleteUrl = deleteUrl || '';
          window.__rp_lastDeleteNotifyTs = now;
          if (!window.RevitBridge) return;
          const fn = window.RevitBridge.SendMessageToRevit || window.RevitBridge.sendMessageToRevit;
          if (typeof fn !== 'function') return;
          const href = String(window.location && window.location.href || '');
          let wpId = null;
          try {
            const m = /\/work_packages\/(\d+)/.exec(href);
            if (m && m[1]) wpId = m[1];
          } catch(_) {}
          const payload = JSON.stringify({
            url: href,
            workPackageId: wpId,
            deleteUrl: String(deleteUrl || ''),
            ts: now
          });
          try { console.log('[RevitBridge] PinRemove via network', payload); } catch(_) {}
          fn('PinRemove', '0', payload);
        } catch(_) {}
      };

      // fetch hook
      if (window.fetch) {
        const originalFetch = window.fetch.bind(window);
        window.fetch = function(input, init) {
          try {
            const url = (typeof input === 'string') ? input : (input && input.url) || '';
            const method = (init && init.method) || (input && input.method) || 'GET';
            if (String(method).toUpperCase() === 'DELETE' && /\/viewpoints\//.test(String(url))) {
              notifyDelete(url);
            }
          } catch(_) {}
          return originalFetch(input, init);
        };
      }

      // XHR hook
      if (window.XMLHttpRequest && window.XMLHttpRequest.prototype) {
        const XHR = window.XMLHttpRequest;
        const origOpen = XHR.prototype.open;
        const origSend = XHR.prototype.send;
        XHR.prototype.open = function(method, url) {
          try { this.__rp_method = method; this.__rp_url = url; } catch(_) {}
          return origOpen.apply(this, arguments);
        };
        XHR.prototype.send = function() {
          try {
            const method = String(this.__rp_method || '').toUpperCase();
            const url = String(this.__rp_url || '');
            if (method === 'DELETE' && /\/viewpoints\//.test(url)) {
              notifyDelete(url);
            }
          } catch(_) {}
          return origSend.apply(this, arguments);
        };
      }
    }
  } catch(_) {}
})();";
      // Execute within the loaded page context
      e.Frame.ExecuteJavaScriptAsync(js);
    }

    private async Task InitializeRevitBridgeIfNotPresentAsync()
    {
      JavascriptResponse revitBridgeIsPresentCheckResponse = await Browser.GetMainFrame()
        .EvaluateScriptAsync("window." + JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME);
      if (revitBridgeIsPresentCheckResponse?.Result != null)
      {
        // No need to register the bridge since it's already bound
        return;
      }

      // Register the bridge between JS and C#
      // This also registers the callback that should be bound to by OpenProject to receive messages from BCFier
      Browser.JavascriptObjectRepository.UnRegisterAll();
      Browser.GetMainFrame()
        .ExecuteJavaScriptAsync($"CefSharp.DeleteBoundObject('{JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME}');");
      Browser.JavascriptObjectRepository.Register(
        JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME, _javascriptInterop, true);

      Browser.GetMainFrame().ExecuteJavaScriptAsync(@"(async function(){
await CefSharp.BindObjectAsync(""" + JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME + @""", ""bound"");
window." + JavaScriptBridge.REVIT_BRIDGE_JAVASCRIPT_NAME +
                                                    @".sendMessageToOpenProject = (message) => {console.log(JSON.parse(message))}; // This is the callback to be used by OpenProject for receiving messages
window.dispatchEvent(new Event('" + JavaScriptBridge.REVIT_READY_EVENT_NAME + @"'));
})();");
      // Now in JS, call this: openProjectBridge.messageFromOpenProject('Message from JS');
    }
  }
}
