using System.Collections.Concurrent;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using CefSharp;
using CefSharp.OffScreen;
using CefSharp.Web;
using Newtonsoft.Json;
using Valve.VR;

namespace VrBrowserTestCore
{
    public partial class Form1 : Form
    {
        private const string ContainerHtml = @"<!DOCTYPE html>
<html>
<head>
<meta charset=""utf-8"">
<style>
  html, body {
    margin: 0;
    padding: 0;
    width: 100%;
    height: 100%;
    overflow: hidden;
    background: transparent !important;
  }
  iframe.vr-tab-frame {
    position: fixed;
    top: 0;
    left: 0;
    width: 100%;
    height: 100%;
    border: none;
    margin: 0;
    padding: 0;
    background: transparent !important;
    pointer-events: none;
  }
</style>
<script>
  window.syncTabs = function(tabs) {
    const existing = new Map();
    document.querySelectorAll('iframe.vr-tab-frame').forEach(el => {
      existing.set(el.id, el);
    });
    const validIds = new Set();
    tabs.forEach((t, index) => {
      const url = (t.url || '').trim();
      if (!url) return;
      const id = 'tab_' + t.id;
      validIds.add(id);
      let iframe = existing.get(id);
      if (!iframe) {
        iframe = document.createElement('iframe');
        iframe.id = id;
        iframe.name = id;
        iframe.className = 'vr-tab-frame';
        iframe.allow = 'autoplay; microphone; camera; encrypted-media; fullscreen';
        iframe.setAttribute('data-url', url);
        iframe.src = url;
        document.body.appendChild(iframe);
      } else if (iframe.getAttribute('data-url') !== url) {
        iframe.setAttribute('data-url', url);
        iframe.src = url;
      }
      iframe.style.zIndex = String(index + 1);
    });
    existing.forEach((el, id) => {
      if (!validIds.has(id)) {
        el.src = 'about:blank';
        el.remove();
      }
    });
  };

  window.reloadTab = function(tabId, url, index) {
    const id = 'tab_' + tabId;
    let iframe = document.getElementById(id);
    const cleanUrl = (url || '').trim();
    if (!cleanUrl) {
      if (iframe) {
        iframe.src = 'about:blank';
        iframe.remove();
      }
      return;
    }
    if (!iframe) {
      iframe = document.createElement('iframe');
      iframe.id = id;
      iframe.name = id;
      iframe.className = 'vr-tab-frame';
      iframe.allow = 'autoplay; microphone; camera; encrypted-media; fullscreen';
      document.body.appendChild(iframe);
    }
    if (typeof index === 'number') {
      iframe.style.zIndex = String(index + 1);
    }
    iframe.setAttribute('data-url', cleanUrl);
    iframe.src = cleanUrl;
  };
</script>
</head>
<body></body>
</html>";

        private AppSettings ThisAppSettings = new();
        readonly static string PathToExe = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location) + "\\";

        // Overlay & Direct3D 11 / CEF state
        CVRSystem? VrSystem = null;
        volatile bool VrStarted = false;
        uint HmdId;
        ulong OverlayHandle = 0;

        private D3D11OverlayRenderer? _d3dRenderer = null;
        private ChromiumWebBrowser? _mainBrowser = null;
        private volatile bool _containerLoaded = false;
        private bool _isLoadingSettingsUi = false;

        // Maps top-level iframe CEF Identifier -> Tab.Tag so even if a site modifies window.name we still know its Tab
        private readonly ConcurrentDictionary<string, Guid> _frameIdToTabTag = new();

        public Form1()
        {
            InitializeComponent();
        }

        private void Form1_Shown(object sender, EventArgs e)
        {
            _isLoadingSettingsUi = true;
            if (File.Exists("AppSettings.json"))
            {
                string data = File.ReadAllText("AppSettings.json");
                ThisAppSettings = JsonConvert.DeserializeObject<AppSettings>(data) ?? new AppSettings();
            }
            else
            {
                Tab tab = new()
                {
                    Tag = new Guid()
                };
                ThisAppSettings.Tabs.Add(tab);
            }

            NuCurvature.Value = (decimal)Math.Clamp(ThisAppSettings.Curvature, (float)NuCurvature.Minimum, (float)NuCurvature.Maximum);
            NuSize.Value = (decimal)Math.Clamp(ThisAppSettings.Size, (float)NuSize.Minimum, (float)NuSize.Maximum);
            NuOffset.Value = (decimal)Math.Clamp(ThisAppSettings.Offset, (float)NuOffset.Minimum, (float)NuOffset.Maximum);
            CoColorFormat.SelectedIndex = Math.Clamp(ThisAppSettings.ColorFormat, 0, CoColorFormat.Items.Count - 1);
            CoResolution.SelectedIndex = Math.Clamp(ThisAppSettings.ResolutionIndex, 0, CoResolution.Items.Count - 1);
            NuFps.Value = Math.Clamp(ThisAppSettings.Fps, (int)NuFps.Minimum, (int)NuFps.Maximum);
            ChMinimizeOnStart.Checked = ThisAppSettings.MinimizeOnStart;
            ChMinimizeToTray.Checked = ThisAppSettings.MinimizeToTray;
            _isLoadingSettingsUi = false;

            if (ThisAppSettings.MinimizeOnStart)
            {
                WindowState = FormWindowState.Minimized;
            }

            LoadPages();
            TryToStartMainProcess();
        }

        private void Form1_Resize(object sender, EventArgs e)
        {
            if (WindowState == FormWindowState.Minimized)
            {
                if (ThisAppSettings.MinimizeToTray)
                {
                    Hide();
                    ShowInTaskbar = false;
                }
            }
        }

        private void TiStartVr_Tick(object sender, EventArgs e)
        {
            TryToStartMainProcess();
        }

        private void TryToStartMainProcess()
        {
            if (!InitVr(out OverlayHandle))
                return;

            var (width, height) = ThisAppSettings.GetResolution();
            _d3dRenderer = new D3D11OverlayRenderer(OverlayHandle, width, height, ThisAppSettings.ColorFormat);

            TiStartVr.Stop();

            var settings = new CefSettings()
            {
                CefCommandLineArgs = {
                    ["enable-media-stream"] = "1",
                    ["autoplay-policy"] = "no-user-gesture-required",
                    ["enable-audio"] = "1",
                    ["disable-web-security"] = "1",
                    ["allow-running-insecure-content"] = "1",
                    ["disable-site-isolation-trials"] = "1",
                    ["disable-features"] = "BlockInsecurePrivateNetworkRequests,IsolateOrigins,site-per-process",
                    ["disable-background-networking"] = "1",
                    ["disable-component-update"] = "1",
                    ["disable-sync"] = "1",
                },
                WindowlessRenderingEnabled = true,
            };
            settings.CefCommandLineArgs.Remove("mute-audio");
            settings.CachePath = PathToExe + "cefcache\\";
            try
            {
                if (Directory.Exists(settings.CachePath))
                {
                    Directory.Delete(settings.CachePath, true);
                }
                Directory.CreateDirectory(settings.CachePath);
            }
            catch { }

            Cef.Initialize(settings);

            IBrowserSettings browserSettings = CefSharp.Core.ObjectFactory.CreateBrowserSettings(autoDispose: false);
            browserSettings.WindowlessFrameRate = Math.Clamp(ThisAppSettings.Fps, 1, 60);

            _mainBrowser = new ChromiumWebBrowser(
                address: "about:blank",
                browserSettings: browserSettings,
                requestContext: null,
                automaticallyCreateBrowser: true,
                onAfterBrowserCreated: null,
                useLegacyRenderHandler: false)
            {
                Size = new Size(width, height),
                RenderHandler = _d3dRenderer
            };

            _mainBrowser.BrowserInitialized += (s, e) =>
            {
                _mainBrowser.LoadHtml(ContainerHtml, "https://vrbrowser.local/");
            };
            _mainBrowser.FrameLoadStart += Browser_FrameLoadStart;
            _mainBrowser.FrameLoadEnd += Browser_FrameLoadEnd;
        }

        private Tab? ResolveTabForFrame(IFrame frame, out IFrame? topSubFrame)
        {
            topSubFrame = null;
            if (frame == null || !frame.IsValid || frame.IsMain)
                return null;

            IFrame? current = frame;
            while (current != null && current.IsValid)
            {
                var parent = current.Parent;
                if (parent != null && parent.IsValid && parent.IsMain)
                {
                    topSubFrame = current;
                    break;
                }
                current = parent;
            }

            if (topSubFrame == null || !topSubFrame.IsValid)
                return null;

            string name = topSubFrame.Name ?? "";
            if (name.StartsWith("tab_", StringComparison.OrdinalIgnoreCase) &&
                Guid.TryParse(name.AsSpan(4), out Guid parsedGuid))
            {
                _frameIdToTabTag[topSubFrame.Identifier] = parsedGuid;
                return GetTabByGuid(parsedGuid);
            }

            if (_frameIdToTabTag.TryGetValue(topSubFrame.Identifier, out Guid cachedGuid))
            {
                return GetTabByGuid(cachedGuid);
            }

            return null;
        }

        private void Browser_FrameLoadStart(object? sender, FrameLoadStartEventArgs e)
        {
            if (!e.Frame.IsValid || e.Frame.IsMain)
                return;

            var tab = ResolveTabForFrame(e.Frame, out _);
            if (tab != null)
            {
                ApplyCssAndAudioToFrame(e.Frame, tab);
            }
        }

        private void Browser_FrameLoadEnd(object? sender, FrameLoadEndEventArgs e)
        {
            if (!e.Frame.IsValid)
                return;

            if (e.Frame.IsMain)
            {
                if (e.Frame.Url != null && e.Frame.Url.StartsWith("https://vrbrowser.local", StringComparison.OrdinalIgnoreCase))
                {
                    _containerLoaded = true;
                    UpdateBrowserHostMuteState();
                    SyncTabsToBrowser();
                }
                return;
            }

            var tab = ResolveTabForFrame(e.Frame, out _);
            if (tab != null)
            {
                UpdateBrowserHostMuteState();
                ApplyCssAndAudioToFrame(e.Frame, tab);
            }
        }

        private void SyncTabsToBrowser()
        {
            if (!_containerLoaded || _mainBrowser == null || _mainBrowser.IsDisposed || !_mainBrowser.IsBrowserInitialized)
                return;

            var payload = ThisAppSettings.Tabs.Select(t => new
            {
                id = t.Tag.ToString("N"),
                url = t.Url ?? ""
            }).ToList();

            string json = JsonConvert.SerializeObject(payload);
            _mainBrowser.ExecuteScriptAsync($"if (window.syncTabs) window.syncTabs({json});");
            UpdateBrowserHostMuteState();
        }

        private void ReloadSingleTabInBrowser(Tab tab)
        {
            if (!_containerLoaded || _mainBrowser == null || _mainBrowser.IsDisposed || !_mainBrowser.IsBrowserInitialized)
                return;

            int index = ThisAppSettings.Tabs.IndexOf(tab);
            string idJson = JsonConvert.SerializeObject(tab.Tag.ToString("N"));
            string urlJson = JsonConvert.SerializeObject(tab.Url ?? "");
            _mainBrowser.ExecuteScriptAsync($"if (window.reloadTab) window.reloadTab({idJson}, {urlJson}, {index});");
            UpdateBrowserHostMuteState();
        }

        private void UpdateBrowserHostMuteState()
        {
            if (_mainBrowser == null || _mainBrowser.IsDisposed || !_mainBrowser.IsBrowserInitialized)
                return;

            bool anyAudioEnabled = ThisAppSettings.Tabs.Any(t => t.AudioEnabled && !string.IsNullOrWhiteSpace(t.Url));
            _mainBrowser.GetBrowserHost()?.SetAudioMuted(!anyAudioEnabled);
        }

        private void ApplyTabCssAndAudioToAllFrames(Tab tab)
        {
            if (_mainBrowser == null || _mainBrowser.IsDisposed || !_mainBrowser.IsBrowserInitialized)
                return;

            try
            {
                var browser = _mainBrowser.GetBrowser();
                if (browser == null) return;

                var identifiers = browser.GetFrameIdentifiers();
                foreach (var id in identifiers)
                {
                    var frame = browser.GetFrameByIdentifier(id);
                    if (frame != null && frame.IsValid && !frame.IsMain)
                    {
                        var ownerTab = ResolveTabForFrame(frame, out _);
                        if (ownerTab != null && ownerTab.Tag == tab.Tag)
                        {
                            ApplyCssAndAudioToFrame(frame, tab);
                        }
                    }
                }
            }
            catch { }
        }

        private static void ApplyCssAndAudioToFrame(IFrame frame, Tab tab)
        {
            if (frame == null || !frame.IsValid)
                return;

            string cssJson = JsonConvert.SerializeObject(tab.Css ?? "");
            string mutedBool = (!tab.AudioEnabled).ToString().ToLowerInvariant();

            string script = $@"
(function(cssText, isMuted) {{
  try {{
    var applyStyle = function() {{
      var root = document.head || document.documentElement;
      if (!root) return;
      var styleId = '__vr_overlay_custom_css';
      var style = document.getElementById(styleId);
      if (!style) {{
        style = document.createElement('style');
        style.id = styleId;
        style.type = 'text/css';
        root.appendChild(style);
      }}
      if (style.textContent !== cssText) {{
        style.textContent = cssText;
      }}
    }};
    applyStyle();
    if (document.readyState === 'loading') {{
      document.addEventListener('DOMContentLoaded', applyStyle, {{ once: true }});
    }}

    window.__vrAudioMuted = isMuted;
    var applyMute = function(rootNode) {{
      if (!rootNode || !rootNode.querySelectorAll) return;
      rootNode.querySelectorAll('audio, video').forEach(function(el) {{
        el.muted = window.__vrAudioMuted;
      }});
    }};
    applyMute(document);

    if (!window.__vrAudioHookInstalled) {{
      window.__vrAudioHookInstalled = true;
      var origPlay = HTMLMediaElement.prototype.play;
      HTMLMediaElement.prototype.play = function() {{
        if (window.__vrAudioMuted) {{
          this.muted = true;
        }}
        return origPlay.apply(this, arguments);
      }};
      if (typeof MutationObserver !== 'undefined' && document.documentElement) {{
        new MutationObserver(function(mutations) {{
          for (var i = 0; i < mutations.length; i++) {{
            var added = mutations[i].addedNodes;
            for (var j = 0; j < added.length; j++) {{
              var node = added[j];
              if (node.tagName === 'AUDIO' || node.tagName === 'VIDEO') {{
                node.muted = window.__vrAudioMuted;
              }} else if (node.querySelectorAll) {{
                applyMute(node);
              }}
            }}
          }}
        }}).observe(document.documentElement, {{ childList: true, subtree: true }});
      }}
    }}
  }} catch (e) {{}}
}})({cssJson}, {mutedBool});";

            frame.ExecuteJavaScriptAsync(script);
        }

        private bool AssociateOverlayToDevice(ulong overlayHandle, uint deviceIndex)
        {
            HmdMatrix34_t transform = new()
            {
                m0 = 1,
                m1 = 0,
                m2 = 0,
                m3 = 0,
                m4 = 0,
                m5 = 1,
                m6 = 0,
                m7 = 0,
                m8 = 0,
                m9 = 0,
                m10 = 1,
                m11 = -ThisAppSettings.Offset
            };

            OpenVR.Overlay.SetOverlayTransformTrackedDeviceRelative(overlayHandle, deviceIndex, ref transform);
            return true;
        }

        private void Form1_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (VrStarted)
            {
                VrStarted = false;
                OpenVR.Overlay?.HideOverlay(OverlayHandle);
                OpenVR.Overlay?.DestroyOverlay(OverlayHandle);
                OpenVR.Shutdown();
            }

            if (_mainBrowser != null)
            {
                _mainBrowser.FrameLoadStart -= Browser_FrameLoadStart;
                _mainBrowser.FrameLoadEnd -= Browser_FrameLoadEnd;
                _mainBrowser.RenderHandler = null;
                _mainBrowser.Dispose();
                _mainBrowser = null;
            }

            if (Cef.IsInitialized == true)
            {
                Cef.Shutdown();
            }

            _d3dRenderer?.Dispose();
            _d3dRenderer = null;

            NoMain.Visible = false;
            NoMain.Dispose();
        }

        static bool IsProcessRunning(string processName)
        {
            Process[] processes = Process.GetProcessesByName(processName);
            bool running = processes.Length > 0;
            foreach (var p in processes)
            {
                p.Dispose();
            }
            return running;
        }

        bool InitVr(out ulong overlayHandle)
        {
            overlayHandle = 0;
            try
            {
                if (!IsProcessRunning("vrserver") && !IsProcessRunning("vrcompositor"))
                    throw new Exception("");

                var systemErr = EVRInitError.None;
                VrSystem = OpenVR.Init(ref systemErr, EVRApplicationType.VRApplication_Overlay);
                if (systemErr != EVRInitError.None)
                    throw new Exception($"OpenVR.Init " + systemErr.ToString());

                uint[] indexes = new uint[1];
                VrSystem.GetSortedTrackedDeviceIndicesOfClass(ETrackedDeviceClass.HMD, indexes, 0);
                HmdId = indexes[0];

                var overlayErr = EVROverlayError.None;
                overlayErr = OpenVR.Overlay.CreateOverlay("browser", "browser", ref overlayHandle);
                if (overlayErr != EVROverlayError.None)
                {
                    if (overlayErr == EVROverlayError.KeyInUse)
                    {
                        OpenVR.Overlay.FindOverlay("browser", ref overlayHandle);
                        OpenVR.Overlay.DestroyOverlay(overlayHandle);
                    }
                    throw new Exception($"CreateOverlay " + overlayErr.ToString());
                }
                OverlayHandle = overlayHandle;
                overlayErr = OpenVR.Overlay.SetOverlayCurvature(overlayHandle, ThisAppSettings.Curvature);
                if (overlayErr != EVROverlayError.None)
                    throw new Exception($"SetOverlayCurvature " + overlayErr.ToString());

                overlayErr = OpenVR.Overlay.SetOverlayWidthInMeters(overlayHandle, ThisAppSettings.Size);
                if (overlayErr != EVROverlayError.None)
                    throw new Exception($"SetOverlayWidthInMeters " + overlayErr.ToString());

                OpenVR.Overlay.SetOverlayColor(overlayHandle, 1, 1, 1);
                OpenVR.Overlay.SetOverlayAlpha(overlayHandle, 1);

                OpenVR.Overlay.ShowOverlay(overlayHandle);

                // Standard DirectX top-left UV bounds (0..1)
                VRTextureBounds_t bounds;
                bounds.uMin = 0;
                bounds.uMax = 1;
                bounds.vMin = 0;
                bounds.vMax = 1;
                OpenVR.Overlay.SetOverlayTextureBounds(overlayHandle, ref bounds);

                AssociateOverlayToDevice(overlayHandle, HmdId);
                VrStarted = true;

                Thread vrEventThread = new(CheckingForExitSteamVr)
                {
                    IsBackground = true,
                    Name = "SteamVrEventPollThread"
                };
                vrEventThread.Start();

                return true;
            }
            catch (Exception ex)
            {
                Debug.WriteLine("VR is not initialized. " + ex.Message);
                return false;
            }
        }

        private void CheckingForExitSteamVr()
        {
            uint eventSize = (uint)Marshal.SizeOf<VREvent_t>();
            VREvent_t vrEvent = default;
            while (VrStarted)
            {
                try
                {
                    while (OpenVR.System != null && OpenVR.System.PollNextEvent(ref vrEvent, eventSize))
                    {
                        if ((EVREventType)vrEvent.eventType == EVREventType.VREvent_Quit)
                        {
                            OpenVR.System.AcknowledgeQuit_Exiting();
                            VrStarted = false;
                            BeginInvoke(new Action(Close));
                            return;
                        }
                    }
                }
                catch
                {
                }
                Thread.Sleep(100);
            }
        }

        private void NuSize_ValueChanged(object sender, EventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            ThisAppSettings.Size = (float)NuSize.Value;
        }

        private void NuOffset_ValueChanged(object sender, EventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            ThisAppSettings.Offset = (float)NuOffset.Value;
        }

        private void NuCurvature_ValueChanged(object sender, EventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            ThisAppSettings.Curvature = (float)NuCurvature.Value;
        }

        private void CoColorFormat_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            ThisAppSettings.ColorFormat = CoColorFormat.SelectedIndex;
            SaveAppSettings();
            ApplyResolutionAndColorFormat();
        }

        private void CoResolution_SelectedIndexChanged(object sender, EventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            ThisAppSettings.ResolutionIndex = CoResolution.SelectedIndex;
            SaveAppSettings();
            ApplyResolutionAndColorFormat();
        }

        private void NuFps_ValueChanged(object sender, EventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            ThisAppSettings.Fps = (int)NuFps.Value;
            SaveAppSettings();
            if (_mainBrowser != null && !_mainBrowser.IsDisposed && _mainBrowser.IsBrowserInitialized)
            {
                try
                {
                    var host = _mainBrowser.GetBrowserHost();
                    if (host != null)
                    {
                        host.WindowlessFrameRate = Math.Clamp(ThisAppSettings.Fps, 1, 60);
                    }
                }
                catch { }
            }
        }

        private void ApplyResolutionAndColorFormat()
        {
            if (!VrStarted || _d3dRenderer == null) return;

            var (width, height) = ThisAppSettings.GetResolution();
            _d3dRenderer.SetConfiguration(width, height, ThisAppSettings.ColorFormat);

            if (_mainBrowser != null && !_mainBrowser.IsDisposed)
            {
                _mainBrowser.Size = new Size(width, height);
                if (_mainBrowser.IsBrowserInitialized)
                {
                    _mainBrowser.GetBrowserHost()?.Invalidate(PaintElementType.View);
                }
            }
        }

        private void LoadPages()
        {
            foreach (var tab in ThisAppSettings.Tabs)
            {
                if (tab.Tag != new Guid())
                {
                    tabControl1.TabPages.Add("New");
                    var tabPage = tabControl1.TabPages[^1];

                    tabPage.Tag = tab.Tag;
                    tabPage.Name = "TaBrowser" + tabPage.Tag.ToString();
                    tabPage.BackColor = Color.White;

                    foreach (Control c in TaBrowser.Controls)
                    {
                        var clonedControl = CloneControl(c, (Guid)tabPage.Tag);
                        tabPage.Controls.Add(clonedControl);
                    }
                }

                var tabPageForNaming = FindControlByName(this, nameof(TaBrowser), tab.Tag) as TabPage;
                if (tabPageForNaming != null && tab.Url.Contains("//"))
                {
                    string name = tab.Url[(tab.Url.IndexOf("//") + 2)..];
                    if (name.Contains('/'))
                    {
                        name = name[..name.IndexOf("/")];
                    }
                    tabPageForNaming.Text = name;
                }

                if (FindControlByName(this, nameof(TeUrl), tab.Tag) is TextBox teUrl)
                    teUrl.Text = tab.Url;
                if (FindControlByName(this, nameof(RiCss), tab.Tag) is RichTextBox riCss)
                    riCss.Text = tab.Css;
                if (FindControlByName(this, nameof(ChAudioEnabled), tab.Tag) is CheckBox chAudio)
                    chAudio.Checked = tab.AudioEnabled;
            }
        }

        private void SaveAppSettings()
        {
            string str = JsonConvert.SerializeObject(ThisAppSettings);
            File.WriteAllText("AppSettings.json", str);
        }

        private Tab? GetTabByGuid(Guid tag)
        {
            foreach (var tab in ThisAppSettings.Tabs)
            {
                if (tab.Tag == tag)
                    return tab;
            }
            return null;
        }

        private static Control? FindControlByName(Control parent, string name, Guid tag)
        {
            if (parent.Name.StartsWith(name) && (tag == new Guid() || parent.Name.EndsWith(tag.ToString())))
            {
                return parent;
            }

            foreach (Control control in parent.Controls)
            {
                var foundControl = FindControlByName(control, name, tag);
                if (foundControl != null)
                {
                    return foundControl;
                }
            }

            return null;
        }

        private void BuTabAdd_Click(object sender, EventArgs e)
        {
            Tab newTab = new();
            ThisAppSettings.Tabs.Add(newTab);

            tabControl1.TabPages.Add("New");
            var page = tabControl1.TabPages[^1];

            page.Tag = newTab.Tag;
            page.Name = "TaBrowser" + page.Tag.ToString();
            page.BackColor = Color.White;

            foreach (Control c in TaBrowser.Controls)
            {
                var clonedControl = CloneControl(c, (Guid)page.Tag);
                page.Controls.Add(clonedControl);
            }
        }

        private Control CloneControl(Control original, Guid tag)
        {
            var type = original.GetType();
            var clonedControl = (Control)Activator.CreateInstance(type)!;

            clonedControl.Size = original.Size;
            clonedControl.Location = original.Location;
            clonedControl.Text = original.Text;
            clonedControl.Tag = tag;
            clonedControl.Name = original.Name + tag.ToString();
            clonedControl.BackColor = original.BackColor;
            clonedControl.ForeColor = original.ForeColor;

            if (clonedControl is TextBox clonedTextBox)
            {
                clonedTextBox.Text = "";
                if (clonedTextBox.Name.StartsWith("TeUrl"))
                {
                    clonedTextBox.TextChanged += TextBoxUrl_TextChanged;
                }
            }

            if (clonedControl is RichTextBox clonedRichTextBox)
            {
                clonedRichTextBox.Text = "";
                if (clonedRichTextBox.Name.StartsWith("RiCss"))
                {
                    clonedRichTextBox.TextChanged += RichTextBoxCss_TextChanged;
                }
            }

            if (clonedControl is CheckBox clonedCheckBox)
            {
                if (clonedCheckBox.Name.StartsWith("ChAudioEnabled"))
                {
                    clonedCheckBox.CheckedChanged += CheckBoxAudioEnabled_CheckedChanged;
                }
            }

            if (original is Button button && clonedControl is Button clonedBtn)
            {
                clonedBtn.ImageList = button.ImageList;
                clonedBtn.ImageIndex = button.ImageIndex;
                if (clonedBtn.Name.StartsWith("BuTabDelete"))
                {
                    clonedBtn.Click += BuPageDelete_Click;
                }
                if (clonedBtn.Name.StartsWith("BuReload"))
                {
                    clonedBtn.Click += BuReload_Click;
                }
            }

            if (original is GroupBox gb && clonedControl is GroupBox clonedGb)
            {
                foreach (Control c in gb.Controls)
                {
                    var cc = CloneControl(c, tag);
                    clonedGb.Controls.Add(cc);
                }
            }

            return clonedControl;
        }

        private void CheckBoxAudioEnabled_CheckedChanged(object? sender, EventArgs e)
        {
            if (sender is not CheckBox control || control.Tag == null) return;

            var tab = GetTabByGuid(Guid.Parse(control.Tag.ToString()!));
            if (tab == null) return;

            tab.AudioEnabled = control.Checked;
            UpdateBrowserHostMuteState();
            ApplyTabCssAndAudioToAllFrames(tab);
            SaveAppSettings();
        }

        private void RichTextBoxCss_TextChanged(object? sender, EventArgs e)
        {
            if (sender is not RichTextBox control || control.Tag == null) return;

            var tab = GetTabByGuid(Guid.Parse(control.Tag.ToString()!));
            if (tab == null) return;

            tab.Css = control.Text;
            ApplyTabCssAndAudioToAllFrames(tab);
        }

        private void TextBoxUrl_TextChanged(object? sender, EventArgs e)
        {
            if (sender is not TextBox control || control.Tag == null) return;

            var tab = GetTabByGuid(Guid.Parse(control.Tag.ToString()!));
            if (tab == null) return;

            tab.Url = control.Text;
        }

        private void BuReload_Click(object? sender, EventArgs e)
        {
            if (sender is not Button control || control.Tag == null) return;

            var tab = GetTabByGuid(Guid.Parse(control.Tag.ToString()!));
            if (tab == null) return;

            var tabPage = FindControlByName(this, "TaBrowser", tab.Tag);
            if (tabPage == null) return;

            if (tab.Url.Contains("//"))
            {
                string name = tab.Url[(tab.Url.IndexOf("//") + 2)..];
                if (name.Contains('/'))
                {
                    name = name[..name.IndexOf("/")];
                }
                tabPage.Text = name;
            }

            SaveAppSettings();
            ReloadSingleTabInBrowser(tab);
        }

        private void BuPageDelete_Click(object? sender, EventArgs e)
        {
            if (sender is not Button control || control.Tag == null) return;

            if (MessageBox.Show("Do you want to delete tab?", "Deleting tab", MessageBoxButtons.YesNo) == DialogResult.No)
            {
                return;
            }

            var tab = GetTabByGuid(Guid.Parse(control.Tag.ToString()!));
            if (tab == null) return;

            ThisAppSettings.Tabs.Remove(tab);

            TabPage? toDelete = null;
            foreach (TabPage tabPage in tabControl1.TabPages)
            {
                if (tabPage.Tag?.ToString() == control.Tag?.ToString())
                {
                    toDelete = tabPage;
                    break;
                }
            }

            if (toDelete != null)
            {
                tabControl1.TabPages.Remove(toDelete);
            }

            SaveAppSettings();
            SyncTabsToBrowser();
        }

        private void BuApplyVrSettings_Click(object sender, EventArgs e)
        {
            SaveAppSettings();
            try
            {
                if (!VrStarted) return;
                OpenVR.Overlay.SetOverlayWidthInMeters(OverlayHandle, ThisAppSettings.Size);
                AssociateOverlayToDevice(OverlayHandle, HmdId);
                OpenVR.Overlay.SetOverlayCurvature(OverlayHandle, ThisAppSettings.Curvature);
            }
            catch { }
        }

        private void ChMinimizeOnStart_CheckedChanged(object sender, EventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            ThisAppSettings.MinimizeOnStart = ChMinimizeOnStart.Checked;
            SaveAppSettings();
        }

        private void ChMinimizeToTray_CheckedChanged(object sender, EventArgs e)
        {
            if (_isLoadingSettingsUi) return;
            ThisAppSettings.MinimizeToTray = ChMinimizeToTray.Checked;
            SaveAppSettings();
        }

        private void NoMain_MouseClick(object sender, MouseEventArgs e)
        {
            Show();
            this.WindowState = FormWindowState.Normal;
            ShowInTaskbar = true;
            Activate();
        }
    }
}
