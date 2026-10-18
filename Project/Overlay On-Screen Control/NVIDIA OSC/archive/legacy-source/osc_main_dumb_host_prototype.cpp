// osc_main.cpp — NVIDIA OSC host ตัวโง่ๆ (รีเซ็ตใหม่ 2026-10-07 ตามสั่ง OWNER)
// หน้าต่างเปล่า + CEF 73 windowed mode (browser ลูกวาด native — ไม่มี OSR/composite)
// โหลดหน้า osc จาก node :59011 (same origin — API/socket/cefquery พอร์ตเดียว)
// ไม่มี: zoom/DSF/compositor/watchdog/GPU path — เพิ่มทีหลังทีละอย่างเมื่อตัวโง่นิ่ง
// ตัวเต็ม (OSR/ULW/GPU/watchdog) เก็บไว้ที่ osc_main.full.cpp
#ifndef NOMINMAX
#define NOMINMAX
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <windows.h>
#include <cstdio>
#include <string>
#include "include/cef_app.h"
#include "include/cef_client.h"
#include "include/cef_browser.h"
#include "include/wrapper/cef_helpers.h"

static const wchar_t kHostExe[] = L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Overlay OSC\\NVIDIA OSC\\NVIDIA OSC.exe";
static const wchar_t kLogFile[] = L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Logs\\NVIDIA OSC console.log";
static const char kStartUrl[] = "http://localhost:59011/index.html";

static void Log(const char* m) {
    HANDLE f = CreateFileW(kLogFile, FILE_APPEND_DATA, FILE_SHARE_READ, nullptr,
        OPEN_ALWAYS, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    SetFilePointer(f, 0, nullptr, FILE_END);
    char line[1100];
    int n = _snprintf_s(line, sizeof(line), _TRUNCATE, "[osc] %s\r\n", m);
    DWORD w = 0; WriteFile(f, line, (DWORD)n, &w, nullptr);
    CloseHandle(f);
}

// ---- minimal client: log console หน้า (debug ง่าย) ----
class OscClient : public CefClient, public CefDisplayHandler {
public:
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    bool OnConsoleMessage(CefRefPtr<CefBrowser>, cef_log_severity_t level,
        const CefString& message, const CefString& source, int line) override {
        std::string s = "[page] " + message.ToString() + " @" + source.ToString() + ":" + std::to_string(line);
        Log(s.c_str());
        return false;
    }
    void OnTitleChange(CefRefPtr<CefBrowser>, const CefString& title) override {
        Log(("[title] " + title.ToString()).c_str());
    }
private:
    IMPLEMENT_REFCOUNTING(OscClient);
};

// ---- window proc: ปิดหน้าต่าง = ออก ----
static HWND g_hwnd = nullptr;
static CefRefPtr<CefBrowser> g_browser;

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM wp, LPARAM lp) {
    switch (msg) {
    case WM_CLOSE: if (g_browser) g_browser->GetHost()->CloseBrowser(true); return 0;
    case WM_DESTROY: PostQuitMessage(0); return 0;
    }
    return DefWindowProcW(h, msg, wp, lp);
}

static void CreateBrowserNow() {
    CefWindowInfo wi;
    RECT rc = { 0, 0, 1600, 900 };
    wi.SetAsChild(g_hwnd, rc);
    CefBrowserSettings bs;
    CefBrowserHost::CreateBrowserSync(wi, new OscClient(), kStartUrl, bs, nullptr);
    Log("[boot] dumb host — browser created (windowed mode, CEF 73)");
}

class CreateBrowserTask : public CefTask {
public:
    void Execute() override { CreateBrowserNow(); }
    IMPLEMENT_REFCOUNTING(CreateBrowserTask);
};

class OscApp : public CefApp, public CefBrowserProcessHandler {
public:
    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }
    void OnContextInitialized() override {
        CEF_REQUIRE_UI_THREAD();
        // บทเรียนเดิม: ห้าม CreateBrowserSync ใน OnContextInitialized — โพสต์ task แทน
        CefPostTask(TID_UI, new CreateBrowserTask());
    }
    IMPLEMENT_REFCOUNTING(OscApp);
};

int APIENTRY wWinMain(HINSTANCE hInst, HINSTANCE, LPWSTR, int nCmdShow) {
    CefMainArgs args(GetModuleHandleW(nullptr));

    CefSettings settings;
    settings.no_sandbox = 1;
    settings.multi_threaded_message_loop = true;   // CEF รัน thread เอง — main แค่ปั๊ม message
    settings.remote_debugging_port = 59099;
    CefString(&settings.browser_subprocess_path) = kHostExe;
    settings.windowless_rendering_enabled = false;  // ★ windowed mode โง่ๆ — ไม่มี OSR

    CefRefPtr<OscApp> app(new OscApp());
    CefInitialize(args, settings, app.get(), nullptr);

    // หน้าต่างโง่ๆ: ขอบปกติ 1600x900 — โชว์เลย (ไม่โปร่ง ไม่ทะลุ — ตัวโง่ก่อน)
    WNDCLASSEXW wc = { sizeof(wc) };
    wc.style = CS_HREDRAW | CS_VREDRAW;
    wc.lpfnWndProc = WndProc;
    wc.hInstance = hInst;
    wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
    wc.hbrBackground = CreateSolidBrush(RGB(20, 20, 20));
    wc.lpszClassName = L"NvidiaOscDumbHost";
    RegisterClassExW(&wc);

    g_hwnd = CreateWindowExW(0, wc.lpszClassName, L"NVIDIA OSC — dumb host (CEF 73)",
        WS_OVERLAPPEDWINDOW | WS_VISIBLE, 60, 60, 1600, 900,
        nullptr, nullptr, hInst, nullptr);

    MSG msg;
    while (GetMessageW(&msg, nullptr, 0, 0) > 0) {
        TranslateMessage(&msg);
        DispatchMessageW(&msg);
    }

    CefShutdown();
    return 0;
}
