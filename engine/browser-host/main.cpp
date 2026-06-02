// browser-host.exe — Minimaler WebView2-Host für VR-Overlay-Sources.
//
// Erzeugt ein chromeless Win32-Top-Level-Window (WS_POPUP) mit WebView2-Inhalt und transparentem
// Standard-Background. Layer-DLL captured dieses Fenster via Windows.Graphics.Capture und projiziert
// es als XrCompositionLayerQuad in iRacing.
//
// Aufruf:
//   browser-host.exe --url=<url> [--title=<title>] [--width=<px>] [--height=<px>]
//
// Wenn --title gesetzt ist, überschreibt es jede Page-Title-Änderung. Damit ist der Window-Title
// stabil und der Layer kann zuverlässig per Substring matchen.

#include <Windows.h>
#include <dwmapi.h>
#include <shellapi.h>
#include <wrl.h>

#pragma comment(lib, "dwmapi.lib")

#include <filesystem>
#include <fstream>
#include <mutex>
#include <string>

#include "WebView2.h"

using Microsoft::WRL::Callback;
using Microsoft::WRL::ComPtr;

namespace {

// Globale State (eine EXE = ein Fenster = ein WebView2).
HWND g_hwnd{nullptr};
ComPtr<ICoreWebView2Controller> g_controller;
ComPtr<ICoreWebView2> g_webview;
std::wstring g_initialUrl;
std::wstring g_fixedTitle;
int g_initialWidth{800};
int g_initialHeight{600};
int g_contentWidth{800};  // CSS-Pixel-Breite des Contents (= Window/RenderScale)
int g_contentHeight{600};
float g_renderScale{2.0f}; // Faktor, mit dem Window-Größe und ZoomFactor multipliziert werden
bool g_explicitSize{false}; // true wenn --width / --height explizit übergeben — Auto-Fit dann aus
bool g_openDevtools{false};
bool g_cloak{false};
// Opaker Fenster-Hintergrund (Preview) statt transparent (VR-Compositing). Opt-in via --bg=RRGGBB.
bool g_opaqueBg{false};
unsigned int g_bgR{0}, g_bgG{0}, g_bgB{0};
// Dekoriertes, verschieb-/schließbares Fenster (Preview) statt rahmenlosem Popup.
bool g_decorated{false};
std::wstring g_caption; // Fenster-Titel (Anzeige); fällt auf g_fixedTitle zurück.
bool g_dumpDom{false};
// Chromeless (Preview): rahmenlos ABER resizebar (WS_THICKFRAME, keine Titelzeile) +
// kleiner Drag-Handle in der Ecke. Verschieben geht nicht über den Inhalt, weil der
// WebView2 die Maus abfängt — daher ein eigenes Kind-Fenster ÜBER dem WebView2.
bool g_chromeless{false};
HWND g_dragHandle{nullptr};    // Move-Griff (oben-links)
HWND g_resizeGrip{nullptr};    // Resize-Griff (unten-rechts)
constexpr int DRAG_HANDLE_SIZE = 22;
constexpr int DRAG_HANDLE_MARGIN = 6;
// USERDATA-Modi des Griff-Fensters.
constexpr LONG_PTR HANDLE_MODE_MOVE = 0;
constexpr LONG_PTR HANDLE_MODE_RESIZE = 1;

// Auto-Fit: nach Navigation messen wir die intrinsische Content-Größe via JS und schrumpfen das
// Fenster darauf — dadurch enthält die WGC-Capture nur den Inhalt, keine leeren Ränder.
constexpr UINT WM_FIT_CONTENT = WM_USER + 1;
constexpr UINT_PTR FIT_TIMER_ID = 1;

// File log next to the EXE for diagnostics.
void LogLine(const std::string& msg) {
    static std::mutex mu;
    std::lock_guard<std::mutex> lock(mu);

    static std::wstring logPath = []() {
        wchar_t exe[MAX_PATH];
        ::GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::wstring p = exe;
        return p.substr(0, p.find_last_of(L"\\/")) + L"\\browser-host.log";
    }();

    SYSTEMTIME st;
    ::GetLocalTime(&st);
    char ts[64];
    sprintf_s(ts, "%02d:%02d:%02d.%03d  pid=%lu  ", st.wHour, st.wMinute, st.wSecond, st.wMilliseconds,
              ::GetCurrentProcessId());

    std::ofstream f(logPath, std::ios::app);
    if (f) {
        f << ts << msg << "\n";
    }
}

void ApplyFixedTitle() {
    if (!g_hwnd) {
        return;
    }
    // Anzeige-Titel: --caption falls gesetzt, sonst der WGC-Match-Key g_fixedTitle.
    const std::wstring& cap = !g_caption.empty() ? g_caption : g_fixedTitle;
    if (cap.empty()) return;
    ::SetWindowTextW(g_hwnd, cap.c_str());
}

// Schreibt die gemessene CSS-Content-Größe in eine Datei, die WPF via FileSystemWatcher
// aufnimmt. Dadurch kann das WPF PixelWidth/Height ohne User-Eingabe korrekt setzen.
// Pfad: %TEMP%\vroverlay-host-<title>.size, Format: "WxH" als UTF-8.
void WriteContentSizeFile(int w, int h) {
    if (g_fixedTitle.empty()) return;
    wchar_t tempPath[MAX_PATH];
    const DWORD len = ::GetTempPathW(MAX_PATH, tempPath);
    if (len == 0) return;
    std::wstring path = std::wstring(tempPath, len) + L"vroverlay-host-" + g_fixedTitle + L".size";
    std::ofstream f(path, std::ios::trunc);
    if (!f.is_open()) return;
    f << w << "x" << h;
    LogLine(std::string("Content size reported: ") + std::to_string(w) + "x" + std::to_string(h));
}

// Diagnose-Dump (--dump-dom): schreibt einen JSON-Schnappschuss in browser-host.log
// neben der EXE (gleiche Datei wie sonstige Logs). Einfacher zu finden für User.
void WriteDomDumpFile(const std::wstring& json) {
    int u8len = ::WideCharToMultiByte(CP_UTF8, 0, json.c_str(), (int)json.size(), nullptr, 0, nullptr, nullptr);
    if (u8len <= 0) return;
    std::string utf8((size_t)u8len, '\0');
    ::WideCharToMultiByte(CP_UTF8, 0, json.c_str(), (int)json.size(), utf8.data(), u8len, nullptr, nullptr);
    LogLine("===== DOM DUMP BEGIN =====");
    LogLine(utf8);
    LogLine("===== DOM DUMP END =====");
}

// Hält die Griff-Fenster im Z-Order über dem WebView2 und schiebt den Resize-Griff
// in die untere rechte Ecke (Move-Griff bleibt fix oben-links).
void RaiseHandles() {
    if (!g_hwnd) return;
    RECT rc;
    ::GetClientRect(g_hwnd, &rc);
    // Move-Griff oben rechts.
    if (g_dragHandle) {
        ::SetWindowPos(g_dragHandle, HWND_TOP,
                       rc.right - DRAG_HANDLE_SIZE - DRAG_HANDLE_MARGIN,
                       DRAG_HANDLE_MARGIN,
                       DRAG_HANDLE_SIZE, DRAG_HANDLE_SIZE, SWP_NOACTIVATE);
        ::InvalidateRect(g_dragHandle, nullptr, TRUE);
    }
    // Resize-Griff unten rechts.
    if (g_resizeGrip) {
        ::SetWindowPos(g_resizeGrip, HWND_TOP,
                       rc.right - DRAG_HANDLE_SIZE - DRAG_HANDLE_MARGIN,
                       rc.bottom - DRAG_HANDLE_SIZE - DRAG_HANDLE_MARGIN,
                       DRAG_HANDLE_SIZE, DRAG_HANDLE_SIZE, SWP_NOACTIVATE);
        ::InvalidateRect(g_resizeGrip, nullptr, TRUE);
    }
}

// Griff-Fenster (Eck-Button): überträgt einen Linksklick als Move bzw. Resize auf
// das Parent-Fenster (ReleaseCapture + WM_NCLBUTTONDOWN mit HTCAPTION/HTBOTTOMRIGHT).
// Nötig, weil der WebView2 die Maus über dem Inhalt abfängt — die OS-Frame-Kanten
// sind via WM_NCCALCSIZE entfernt (keine Titelleiste). Modus in GWLP_USERDATA.
LRESULT CALLBACK HandleProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    const LONG_PTR mode = ::GetWindowLongPtrW(hwnd, GWLP_USERDATA);
    switch (msg) {
    case WM_LBUTTONDOWN:
        ::ReleaseCapture();
        ::SendMessageW(::GetParent(hwnd), WM_NCLBUTTONDOWN,
                       mode == HANDLE_MODE_RESIZE ? HTBOTTOMRIGHT : HTCAPTION, 0);
        return 0;
    case WM_SETCURSOR:
        ::SetCursor(::LoadCursorW(nullptr,
                    mode == HANDLE_MODE_RESIZE ? IDC_SIZENWSE : IDC_SIZEALL));
        return TRUE;
    case WM_PAINT: {
        PAINTSTRUCT ps;
        HDC dc = ::BeginPaint(hwnd, &ps);
        RECT rc;
        ::GetClientRect(hwnd, &rc);
        HBRUSH bg = ::CreateSolidBrush(RGB(30, 30, 30));
        ::FillRect(dc, &rc, bg);
        ::DeleteObject(bg);
        const int cx = rc.right - rc.left, cy = rc.bottom - rc.top;
        if (mode == HANDLE_MODE_RESIZE) {
            // Diagonale Greif-Punkte (unten-rechts) als Resize-Affordanz.
            HBRUSH dot = ::CreateSolidBrush(RGB(200, 200, 200));
            for (int i = 0; i < 3; ++i) {
                RECT d{cx - 6 - i * 5, cy - 6, cx - 6 - i * 5 + 2, cy - 4};
                ::FillRect(dc, &d, dot);
                RECT e{cx - 6, cy - 6 - i * 5, cx - 4, cy - 6 - i * 5 + 2};
                ::FillRect(dc, &e, dot);
            }
            ::DeleteObject(dot);
        } else {
            // Standard-Windows „Move"-Symbol (4-Pfeil-Kreuz, GDI).
            const int mx = cx / 2, my = cy / 2, L = 6, a = 3;
            HPEN pen = ::CreatePen(PS_SOLID, 2, RGB(220, 220, 220));
            HGDIOBJ oldPen = ::SelectObject(dc, pen);
            ::MoveToEx(dc, mx, my - L, nullptr); ::LineTo(dc, mx, my + L + 1);
            ::MoveToEx(dc, mx - L, my, nullptr); ::LineTo(dc, mx + L + 1, my);
            ::SelectObject(dc, oldPen);
            ::DeleteObject(pen);
            HBRUSH head = ::CreateSolidBrush(RGB(220, 220, 220));
            HGDIOBJ oldBr = ::SelectObject(dc, head);
            HGDIOBJ oldPn = ::SelectObject(dc, ::GetStockObject(NULL_PEN));
            POINT up[3] = {{mx, my - L - 3}, {mx - a, my - L}, {mx + a, my - L}};
            POINT dn[3] = {{mx, my + L + 3}, {mx - a, my + L}, {mx + a, my + L}};
            POINT lf[3] = {{mx - L - 3, my}, {mx - L, my - a}, {mx - L, my + a}};
            POINT rt[3] = {{mx + L + 3, my}, {mx + L, my - a}, {mx + L, my + a}};
            ::Polygon(dc, up, 3); ::Polygon(dc, dn, 3);
            ::Polygon(dc, lf, 3); ::Polygon(dc, rt, 3);
            ::SelectObject(dc, oldPn);
            ::SelectObject(dc, oldBr);
            ::DeleteObject(head);
        }
        ::EndPaint(hwnd, &ps);
        return 0;
    }
    }
    return ::DefWindowProcW(hwnd, msg, wp, lp);
}

HWND CreateHandle(HINSTANCE hInst, LONG_PTR mode) {
    static bool registered = false;
    if (!registered) {
        WNDCLASSEXW wc = {};
        wc.cbSize = sizeof(wc);
        wc.lpfnWndProc = HandleProc;
        wc.hInstance = hInst;
        wc.lpszClassName = L"BrowserHostHandle";
        wc.hCursor = ::LoadCursorW(nullptr, IDC_ARROW);
        wc.hbrBackground = (HBRUSH)GetStockObject(NULL_BRUSH);
        ::RegisterClassExW(&wc);
        registered = true;
    }
    HWND h = ::CreateWindowExW(0, L"BrowserHostHandle", L"",
                               WS_CHILD | WS_VISIBLE,
                               DRAG_HANDLE_MARGIN, DRAG_HANDLE_MARGIN,
                               DRAG_HANDLE_SIZE, DRAG_HANDLE_SIZE,
                               g_hwnd, nullptr, hInst, nullptr);
    if (h) ::SetWindowLongPtrW(h, GWLP_USERDATA, mode);
    return h;
}

void CreateHandles(HINSTANCE hInst) {
    g_dragHandle = CreateHandle(hInst, HANDLE_MODE_MOVE);
    g_resizeGrip = CreateHandle(hInst, HANDLE_MODE_RESIZE);
}

LRESULT CALLBACK WndProc(HWND hwnd, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_NCCALCSIZE:
        // Chromeless: kein Non-Client-Bereich → keine Titelleiste/kein Rahmen,
        // Client-Area = ganzes Fenster. Resizen läuft über den Eck-Griff (HTBOTTOMRIGHT).
        if (g_chromeless && wp == TRUE) {
            return 0;
        }
        break;
    case WM_SIZE:
        if (g_controller) {
            RECT rc;
            GetClientRect(hwnd, &rc);
            g_controller->put_Bounds(rc);
        }
        RaiseHandles();
        return 0;
    case WM_TIMER:
        if (wp == FIT_TIMER_ID) {
            ::KillTimer(hwnd, FIT_TIMER_ID);
            if (g_webview && g_dumpDom) {
                // Diagnose-Dump: window-Globals, CSS-Variablen, Body-Tree → File.
                g_webview->ExecuteScript(
                    L"(function(){"
                    L"var info={};"
                    L"info.title=document.title;"
                    L"info.url=location.href;"
                    L"info.viewport=document.documentElement.clientWidth+'x'+document.documentElement.clientHeight;"
                    L"info.meta=[];"
                    L"document.querySelectorAll('meta').forEach(function(m){"
                    L"var n=m.name||m.getAttribute('property')||m.getAttribute('http-equiv')||'?';"
                    L"info.meta.push(n+'='+(m.content||''));});"
                    L"info.cssVars={};"
                    L"['html','body'].forEach(function(sel){"
                    L"var el=document.querySelector(sel);if(!el)return;"
                    L"var s=getComputedStyle(el);"
                    L"for(var i=0;i<s.length;i++){var p=s[i];"
                    L"if(p.indexOf('--')===0)info.cssVars[sel+'/'+p]=s.getPropertyValue(p).trim();}});"
                    L"info.globals=[];"
                    L"var skipKeys={window:1,document:1,navigator:1,location:1,history:1,screen:1,self:1,parent:1,top:1,frames:1};"
                    L"Object.keys(window).forEach(function(k){"
                    L"if(skipKeys[k])return;"
                    L"if(/^(webkit|chrome|on[a-z]+)$/i.test(k))return;"
                    L"try{var v=window[k];var t=typeof v;"
                    L"if(t==='function'||t==='undefined'||v===null)return;"
                    L"info.globals.push(k+':'+t);}catch(e){}});"
                    L"info.tree=[];"
                    L"function walk(el,depth){"
                    L"if(depth>3||!el)return;"
                    L"var s=getComputedStyle(el);var r=el.getBoundingClientRect();"
                    L"var dataAttrs={};for(var i=0;i<el.attributes.length;i++){"
                    L"var a=el.attributes[i];if(a.name.indexOf('data-')===0)dataAttrs[a.name]=a.value;}"
                    L"info.tree.push({d:depth,tag:el.tagName,id:el.id||'',"
                    L"cls:(el.className+'').substring(0,80),"
                    L"cssW:s.width,cssH:s.height,"
                    L"rect:Math.round(r.width)+'x'+Math.round(r.height),"
                    L"pos:Math.round(r.left)+','+Math.round(r.top),"
                    L"data:dataAttrs});"
                    L"for(var j=0;j<el.children.length&&info.tree.length<200;j++)walk(el.children[j],depth+1);}"
                    L"if(document.body)walk(document.body,0);"
                    L"return JSON.stringify(info);"
                    L"})()",
                    Callback<ICoreWebView2ExecuteScriptCompletedHandler>(
                        [](HRESULT, LPCWSTR result) -> HRESULT {
                            if (!result) return S_OK;
                            std::wstring s = result;
                            // ExecuteScript wrappt das Ergebnis in einen JSON-String (mit \"..."\").
                            // Wir wollen das innere JSON unverarbeitet ins File. Manuell unescapen:
                            // Strip leading/trailing quotes, replace \\ → \, \" → "
                            if (s.size() >= 2 && s.front() == L'"' && s.back() == L'"') {
                                s = s.substr(1, s.size() - 2);
                            }
                            std::wstring out;
                            out.reserve(s.size());
                            for (size_t i = 0; i < s.size(); i++) {
                                if (s[i] == L'\\' && i + 1 < s.size()) {
                                    wchar_t n = s[i + 1];
                                    if (n == L'\\') { out += L'\\'; i++; continue; }
                                    if (n == L'"')  { out += L'"';  i++; continue; }
                                    if (n == L'n')  { out += L'\n'; i++; continue; }
                                    if (n == L't')  { out += L'\t'; i++; continue; }
                                }
                                out += s[i];
                            }
                            WriteDomDumpFile(out);
                            return S_OK;
                        }).Get());
            }
            if (g_webview) {
                g_webview->ExecuteScript(
                    L"(function(){"
                    L"var vw=document.documentElement.clientWidth,vh=document.documentElement.clientHeight;"
                    // 1. SimHub-Dash-Heuristik: bekannte Dash-Container-Selektoren probieren.
                    // SimHub strukturiert seine Overlays mit .dash / #d am äußersten Dash-Element
                    // oder .maincontainer / #fill als Container. Inline-Style trägt die Designgröße.
                    L"function pxFromInline(el){"
                    L"if(!el||!el.style)return null;"
                    L"var iw=el.style.width,ih=el.style.height;"
                    L"if(!iw||!ih||!/px$/.test(iw)||!/px$/.test(ih))return null;"
                    L"var w=parseFloat(iw),h=parseFloat(ih);"
                    L"if(w<30||h<10)return null;"
                    L"return{w:w,h:h};}"
                    L"var dashSelectors=['.dash','#d','.maincontainer','#fill','.screen','.dash-host'];"
                    L"for(var i=0;i<dashSelectors.length;i++){"
                    L"var el=document.querySelector(dashSelectors[i]);"
                    L"var v=pxFromInline(el);"
                    L"if(v)return Math.ceil(v.w)+'x'+Math.ceil(v.h);}"
                    // 2. Fallback: Bounding-Box aller sichtbaren Elemente, geclippt auf Viewport.
                    L"var minX=Infinity,minY=Infinity,maxX=-Infinity,maxY=-Infinity,found=false;"
                    L"document.querySelectorAll('body *').forEach(function(el){"
                    L"var s=getComputedStyle(el);"
                    L"if(s.display==='none'||s.visibility==='hidden'||parseFloat(s.opacity)===0)return;"
                    L"var hasBg=s.backgroundColor!=='rgba(0, 0, 0, 0)'&&s.backgroundColor!=='transparent';"
                    L"var hasBorder=parseFloat(s.borderTopWidth)>0||parseFloat(s.borderBottomWidth)>0"
                    L"||parseFloat(s.borderLeftWidth)>0||parseFloat(s.borderRightWidth)>0;"
                    L"var hasText=el.children.length===0&&el.textContent.trim().length>0;"
                    L"var isMedia=['IMG','CANVAS','SVG','VIDEO'].indexOf(el.tagName)>=0;"
                    L"if(!(hasBg||hasBorder||hasText||isMedia))return;"
                    L"var r=el.getBoundingClientRect();"
                    L"var l=Math.max(0,r.left),t=Math.max(0,r.top),"
                    L"rt=Math.min(vw,r.right),b=Math.min(vh,r.bottom);"
                    L"if(rt<=l||b<=t)return;"
                    L"if(l<minX)minX=l;if(t<minY)minY=t;if(rt>maxX)maxX=rt;if(b>maxY)maxY=b;"
                    L"found=true;});"
                    L"if(!found)return vw+'x'+vh;"
                    L"return Math.ceil(maxX-minX)+'x'+Math.ceil(maxY-minY);"
                    L"})()",
                    Callback<ICoreWebView2ExecuteScriptCompletedHandler>(
                        [](HRESULT, LPCWSTR resultJson) -> HRESULT {
                            if (!resultJson) return S_OK;
                            std::wstring s = resultJson;
                            if (s.size() >= 2 && s.front() == L'"' && s.back() == L'"') {
                                s = s.substr(1, s.size() - 2);
                            }
                            size_t xpos = s.find(L'x');
                            if (xpos == std::wstring::npos) return S_OK;
                            int w = _wtoi(s.substr(0, xpos).c_str());
                            int h = _wtoi(s.substr(xpos + 1).c_str());
                            if (w > 0 && h > 0) {
                                LogLine(std::string("Content measured (CSS) ") + std::to_string(w) + "x" + std::to_string(h));
                                // Reporting an WPF — auch wenn Size explizit gesetzt war.
                                WriteContentSizeFile(w, h);
                                // Auto-Resize nur wenn keine explizite Größe gesetzt.
                                if (!g_explicitSize) {
                                    const int wdev = (int)(w * g_renderScale);
                                    const int hdev = (int)(h * g_renderScale);
                                    ::PostMessageW(g_hwnd, WM_FIT_CONTENT, (WPARAM)wdev, (LPARAM)hdev);
                                }
                            }
                            return S_OK;
                        }).Get());
            }
        }
        return 0;
    case WM_FIT_CONTENT: {
        const int w = (int)wp;
        const int h = (int)lp;
        ::SetWindowPos(hwnd, nullptr, 0, 0, w, h, SWP_NOMOVE | SWP_NOZORDER | SWP_NOACTIVATE);
        return 0;
    }
    case WM_DESTROY:
        ::PostQuitMessage(0);
        return 0;
    }
    return ::DefWindowProcW(hwnd, msg, wp, lp);
}

struct ParsedArgs {
    std::wstring url;
    std::wstring title;
    int width{800};
    int height{600};
    float renderScale{2.0f};
    bool devtools{false};
    bool cloak{false};
    bool dumpDom{false};
    bool opaqueBg{false};
    unsigned int bgR{0}, bgG{0}, bgB{0};
    bool decorated{false};
    bool chromeless{false};
    std::wstring caption;
};

ParsedArgs ParseOurArgs() {
    ParsedArgs a;
    int argc = 0;
    LPWSTR* argv = ::CommandLineToArgvW(::GetCommandLineW(), &argc);
    if (!argv) {
        return a;
    }
    for (int i = 1; i < argc; ++i) {
        const std::wstring arg = argv[i];
        if (arg.rfind(L"--url=", 0) == 0) {
            a.url = arg.substr(6);
        } else if (arg.rfind(L"--title=", 0) == 0) {
            a.title = arg.substr(8);
        } else if (arg.rfind(L"--width=", 0) == 0) {
            a.width = _wtoi(arg.substr(8).c_str());
        } else if (arg.rfind(L"--height=", 0) == 0) {
            a.height = _wtoi(arg.substr(9).c_str());
        } else if (arg.rfind(L"--render-scale=", 0) == 0) {
            a.renderScale = (float)_wtof(arg.substr(15).c_str());
            if (a.renderScale <= 0.f) a.renderScale = 1.f;
        } else if (arg == L"--devtools") {
            a.devtools = true;
        } else if (arg == L"--cloak") {
            a.cloak = true;
        } else if (arg == L"--dump-dom") {
            a.dumpDom = true;
        } else if (arg.rfind(L"--bg=", 0) == 0) {
            std::wstring hex = arg.substr(5);
            if (!hex.empty() && hex[0] == L'#') hex = hex.substr(1);
            if (hex.size() == 6) {
                a.opaqueBg = true;
                a.bgR = wcstoul(hex.substr(0, 2).c_str(), nullptr, 16);
                a.bgG = wcstoul(hex.substr(2, 2).c_str(), nullptr, 16);
                a.bgB = wcstoul(hex.substr(4, 2).c_str(), nullptr, 16);
            }
        } else if (arg == L"--window") {
            a.decorated = true;
        } else if (arg == L"--chromeless") {
            a.chromeless = true;
        } else if (arg.rfind(L"--caption=", 0) == 0) {
            a.caption = arg.substr(10);
        }
    }
    ::LocalFree(argv);
    return a;
}

// Initialisiert WebView2 in g_hwnd. Asynchroner Aufruf — Callbacks setzen g_controller und g_webview.
HRESULT InitWebView2() {
    // Eigener User-Data-Folder pro Instanz: %TEMP%\vroverlay-host-<title>\
    // Verhindert dass mehrere browser-host-Instanzen sich einen WebView2-Chromium-Prozess teilen,
    // sonst killt der entireProcessTree-Kill der einen Instanz die Renderer-Prozesse aller anderen.
    static std::wstring userDataFolder;
    if (userDataFolder.empty() && !g_fixedTitle.empty()) {
        wchar_t tempPath[MAX_PATH];
        const DWORD len = ::GetTempPathW(MAX_PATH, tempPath);
        if (len > 0) {
            userDataFolder = std::wstring(tempPath, len) + L"vroverlay-host-" + g_fixedTitle;
            ::CreateDirectoryW(userDataFolder.c_str(), nullptr);
        }
    }
    const wchar_t* udfArg = userDataFolder.empty() ? nullptr : userDataFolder.c_str();
    LogLine(std::string("CreateCoreWebView2EnvironmentWithOptions, udf=") +
            (udfArg ? std::filesystem::path(udfArg).string() : "default"));
    return ::CreateCoreWebView2EnvironmentWithOptions(
        nullptr,    // browser executable folder = Default (installierte WebView2-Runtime)
        udfArg,     // user data folder per Instanz → eigener Chromium-Prozess
        nullptr,    // environment options = Default
        Callback<ICoreWebView2CreateCoreWebView2EnvironmentCompletedHandler>(
            [](HRESULT envResult, ICoreWebView2Environment* env) -> HRESULT {
                if (FAILED(envResult) || !env) {
                    LogLine(std::string("Environment creation FAILED, hr=") + std::to_string(envResult));
                    ::PostQuitMessage(1);
                    return S_OK;
                }
                LogLine("Environment ready, creating Controller...");
                env->CreateCoreWebView2Controller(
                    g_hwnd,
                    Callback<ICoreWebView2CreateCoreWebView2ControllerCompletedHandler>(
                        [](HRESULT ctrlResult, ICoreWebView2Controller* ctrl) -> HRESULT {
                            if (FAILED(ctrlResult) || !ctrl) {
                                LogLine(std::string("Controller creation FAILED, hr=") +
                                        std::to_string(ctrlResult));
                                ::PostQuitMessage(2);
                                return S_OK;
                            }
                            g_controller = ctrl;

                            // Transparenter Default-Background — CSS body { transparent } scheint durch.
                            ComPtr<ICoreWebView2Controller2> ctrl2;
                            if (SUCCEEDED(ctrl->QueryInterface(IID_PPV_ARGS(&ctrl2)))) {
                                COREWEBVIEW2_COLOR bg = g_opaqueBg
                                    ? COREWEBVIEW2_COLOR{255, (BYTE)g_bgR, (BYTE)g_bgG, (BYTE)g_bgB}
                                    : COREWEBVIEW2_COLOR{0, 0, 0, 0};
                                ctrl2->put_DefaultBackgroundColor(bg);
                                LogLine(g_opaqueBg ? "DefaultBackgroundColor set to opaque"
                                                   : "DefaultBackgroundColor set to transparent");
                            } else {
                                LogLine("ICoreWebView2Controller2 not available — no transparency");
                            }

                            // ZoomFactor: content rendert bei Faktor 2.0 auf doppelte device pixels →
                            // bei kleiner content-Größe (z.B. 200×30) WGC-Capture trotzdem 400×60 für Schärfe.
                            ctrl->put_ZoomFactor(g_renderScale);
                            LogLine(std::string("ZoomFactor set to ") + std::to_string(g_renderScale));

                            // Bounds an Client-Rect anpassen.
                            RECT rc;
                            ::GetClientRect(g_hwnd, &rc);
                            ctrl->put_Bounds(rc);
                            // Griffe wieder nach vorn holen (WebView2-Kind ist jetzt da).
                            RaiseHandles();

                            // Webview holen + navigieren.
                            ctrl->get_CoreWebView2(&g_webview);
                            if (g_webview) {
                                if (g_openDevtools) {
                                    g_webview->OpenDevToolsWindow();
                                    LogLine("DevTools opened (--devtools)");
                                }
                                EventRegistrationToken navToken{};
                                g_webview->add_NavigationCompleted(
                                    Callback<ICoreWebView2NavigationCompletedEventHandler>(
                                        [](ICoreWebView2*, ICoreWebView2NavigationCompletedEventArgs*) -> HRESULT {
                                            // Immer messen — bei !explicitSize zum Resize, sonst nur fürs
                                            // Content-Size-Reporting an WPF.
                                            LogLine("NavigationCompleted, scheduling content measurement");
                                            ::SetTimer(g_hwnd, FIT_TIMER_ID, 400, nullptr);
                                            return S_OK;
                                        }).Get(),
                                    &navToken);

                                LogLine("Navigating to URL");
                                g_webview->Navigate(g_initialUrl.c_str());
                            }

                            // Initial Title anwenden falls --title=<...> gesetzt war.
                            ApplyFixedTitle();
                            return S_OK;
                        }).Get());
                return S_OK;
            }).Get());
}

} // namespace

int APIENTRY wWinMain(_In_ HINSTANCE hInstance, _In_opt_ HINSTANCE, _In_ LPWSTR, _In_ int nCmdShow) {
    LogLine("=== browser-host (WebView2) start ===");

    const ParsedArgs args = ParseOurArgs();
    if (args.url.empty()) {
        ::MessageBoxW(nullptr,
                      L"Usage: browser-host.exe --url=<url> [--title=<title>] [--width=<px>] [--height=<px>]",
                      L"browser-host",
                      MB_OK | MB_ICONINFORMATION);
        return 1;
    }
    g_initialUrl = args.url;
    g_fixedTitle = args.title;
    g_contentWidth = args.width > 0 ? args.width : 800;
    g_contentHeight = args.height > 0 ? args.height : 600;
    g_renderScale = args.renderScale;
    // Render-Scale: Window-Größe verdoppelt o.ä., ZoomFactor entsprechend → Content
    // rendert auf 2× device pixels für sharp WGC-Capture, Aspect bleibt zum Content.
    g_initialWidth = (int)(g_contentWidth * g_renderScale);
    g_initialHeight = (int)(g_contentHeight * g_renderScale);
    g_explicitSize = args.width > 0 && args.height > 0;
    g_openDevtools = args.devtools;
    g_cloak = args.cloak;
    g_opaqueBg = args.opaqueBg;
    g_bgR = args.bgR; g_bgG = args.bgG; g_bgB = args.bgB;
    g_decorated = args.decorated;
    g_chromeless = args.chromeless;
    g_caption = args.caption;
    g_dumpDom = args.dumpDom;
    LogLine("Args parsed");

    // Window-Class registrieren.
    WNDCLASSEXW wc = {};
    wc.cbSize = sizeof(wc);
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInstance;
    wc.lpszClassName = L"BrowserHostWnd";
    wc.hCursor = ::LoadCursorW(nullptr, IDC_ARROW);
    wc.hbrBackground = (HBRUSH)GetStockObject(NULL_BRUSH); // kein Window-Background-Erase
    if (!::RegisterClassExW(&wc)) {
        LogLine("RegisterClassExW failed");
        return 3;
    }

    // Frameless Top-Level-Window (WS_POPUP, sichtbar). Kein WS_THICKFRAME — sonst captured WGC den
    // grauen Resize-Rand mit. Client-Area = ganze Fenstergröße = nur WebView2-Inhalt.
    std::wstring initialTitle = !g_caption.empty() ? g_caption
                              : (g_fixedTitle.empty() ? L"browser-host" : g_fixedTitle);

    // Dekoriert (Preview): normales Fenster mit Titelbalken → verschieb-/schließbar,
    // Taskbar-Eintrag. Sonst: rahmenloses Popup (VR-Capture, kein Taskbar-Eintrag).
    const DWORD exStyle = (g_decorated || g_chromeless) ? WS_EX_APPWINDOW : WS_EX_TOOLWINDOW;
    // Chromeless: rahmenlos, aber WS_THICKFRAME → resizebar an den Kanten, keine Titelzeile.
    const DWORD style = g_chromeless ? (WS_POPUP | WS_THICKFRAME | WS_VISIBLE)
                      : g_decorated  ? (WS_OVERLAPPEDWINDOW | WS_VISIBLE)
                                     : (WS_POPUP | WS_VISIBLE);
    int winW = g_initialWidth, winH = g_initialHeight;
    if (g_decorated) {
        // Client-Area = gewünschte Größe; Fenster inkl. Rahmen/Titel größer machen.
        RECT rc{0, 0, g_initialWidth, g_initialHeight};
        ::AdjustWindowRectEx(&rc, style, FALSE, exStyle);
        winW = rc.right - rc.left;
        winH = rc.bottom - rc.top;
    }
    // Chromeless: WM_NCCALCSIZE entfernt den Non-Client-Bereich → Client == Fenster,
    // daher KEIN AdjustWindowRectEx (sonst wüchse das Fenster bei jedem Show um die
    // Rahmenbreite ≈ 7 CSS-px nach dem RenderScale-Teilen).
    g_hwnd = ::CreateWindowExW(exStyle,
                               L"BrowserHostWnd",
                               initialTitle.c_str(),
                               style,
                               100, 100, winW, winH,
                               nullptr, nullptr, hInstance, nullptr);
    if (!g_hwnd) {
        LogLine("CreateWindowExW failed");
        return 4;
    }
    LogLine("Window created");
    if (g_chromeless) {
        CreateHandles(hInstance);
        RaiseHandles();
    }
    ::ShowWindow(g_hwnd, nCmdShow);

    // Cloak: für den User unsichtbar, DWM komponiert weiter, WGC capturet normal. Opt-in via --cloak,
    // damit man beim Entwickeln das Fenster noch sieht. Im Final-Build immer gesetzt.
    if (g_cloak) {
        BOOL cloakOn = TRUE;
        HRESULT hr = ::DwmSetWindowAttribute(g_hwnd, DWMWA_CLOAK, &cloakOn, sizeof(cloakOn));
        LogLine(std::string("DWM cloak applied, hr=") + std::to_string(hr));
    }

    HRESULT hr = InitWebView2();
    if (FAILED(hr)) {
        LogLine(std::string("InitWebView2 failed at call site, hr=") + std::to_string(hr));
        ::MessageBoxW(g_hwnd,
                      L"WebView2-Runtime fehlt? Installiere Microsoft Edge WebView2 Runtime.",
                      L"browser-host",
                      MB_OK | MB_ICONERROR);
        return 5;
    }

    // Win32 Message Loop. WebView2 läuft async; bis Controller bereit ist sieht das Fenster nur die
    // Standard-Background-Farbe (transparent → durchsichtig zum Desktop).
    MSG msg;
    while (::GetMessageW(&msg, nullptr, 0, 0)) {
        ::TranslateMessage(&msg);
        ::DispatchMessageW(&msg);
    }

    LogLine("=== clean exit ===");
    return (int)msg.wParam;
}
