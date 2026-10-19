// osc_main.cpp — NVIDIA OSC (ของเรา): host C++ เหมือน NVIDIA Share.exe
//
// เฟส B+C: OSR (offscreen) + layered compositor per-pixel alpha + input forwarding
// + page server :3000 + toggle :59003 + อ่าน NVIDIA Config\nvidia-osc.json — ใช้แทน CefSharp
//
// เลียนแบบของแท้ (จาก import table ของ Share.exe จริง):
//   CEF OnPaint (BGRA) → premultiply → UpdateLayeredWindow (WS_EX_LAYERED)
//   = Layered Window + AlphaBlend + GDI แบบเดียวกับ offscreen_window.cpp ของ NVIDIA
//
// ★ หน้าต่าง/หน้า CEF ค้างตลอดอายุโปรเซสและ HWND topmost เสมอ.
//   Keep the full window region in both states so DComp content remains visible.
//   Closed-state cross-process click-through requires a per-pixel layered surface.
#ifndef NOMINMAX
#define NOMINMAX            // ก่อนทุก header — CEF ดึง windows.h เข้ามาเอง
#endif
#ifndef WIN32_LEAN_AND_MEAN
#define WIN32_LEAN_AND_MEAN
#endif
#include <winsock2.h>
#include "include/cef_app.h"
#include "include/cef_client.h"
#include "include/cef_browser.h"
#include "include/cef_keyboard_handler.h"
#include "include/wrapper/cef_helpers.h"
#include "include/wrapper/cef_message_router.h"
#include "include/base/cef_logging.h"

#include <windows.h>
#include <psapi.h>
#include <windowsx.h>      // GET_X_LPARAM / GET_Y_LPARAM
#include <string>
#include <filesystem>
#include <atomic>
#include <algorithm>
#include <shlwapi.h>
#include <cmath>
#include <vector>
#include <functional>
#include <mutex>
// ★ GPU compositor ทั้งสายแบบของแท้ (offscreen_renderer_d3d11.cpp):
//   CEF shared texture (D3D11) → shader premultiply → swapchain alpha → DComp
//   ไม่มีพิกเซลแตะ CPU เลย (เดิม: premultiply 7MB/เฟรมใน C++ = กระตุก + กิน CPU)
#include <d3d11.h>
#include <d3d11_4.h>   // [FIX-D] ID3D11Multithread (SetMultithreadProtected)
#include <d3d11_3.h>   // ID3D11Device1 + OpenSharedResource1 (NT handle ของ CEF)
#include <dxgi1_2.h>
#include <dcomp.h>
#include <d3dcompiler.h>
#include <cstring>
#include "../../../NVIDIA Adapter/OSC Adapter/Duluka Hook/include/osc_frame_bridge.h"
#include "OscFramePublisher/OscD3D9FramePublisher/OscD3D9FramePublisher.h"
#include "OscFramePublisher/OscD3D11FramePublisher/OscD3D11FramePublisher.h"
#include "OscFramePublisher/Common/OscFrameUtilities.h"
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dcomp.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "psapi.lib")
#pragma comment(lib, "winmm.lib")
#include <mmsystem.h>

// ================= config (แบบง่าย: หา "key": ค่า ใน json) =================
struct OscConfig {
    std::wstring page_dir;
    std::wstring log_file;
    double render_scale = 1.0;
    int design_w = 0, design_h = 0;   // Canvas UI ฐานออกแบบ (1920×1080 = UI 1.0) — 0 = ตามจอ×renderScale
    bool show_on_boot = false;
    bool topmost = true;
    int page_port = 3000;
    int toggle_port = 59003;
    int frame_rate = 60;      // CEF OSR default 30 = กระตุก — ตั้งให้เต็มจอปกติ
    double ui_scale = 1.0;    // คูณเข้า fit ของสูตร zoom แท้ (1.0 = ตรง binary เป๊ะ)
    std::wstring cache_path;
    std::wstring subprocess_path;
    int debug_port = 59099;
    bool windowed = true;          // ★ สเปก OWNER (10:5x): ให้ CEF จัดการ render เอง (GPU native)
    int max_fps = 60;
    bool show_fps = true;          // ★ FPS meter บน overlay (นับเฟรมที่แสดงจริง)
    bool gdi_compositor = false;   // true = UpdateLayeredWindow (แสดงผลชัวร์) แทน DComp
    int render_mode = 0;           // "renderMode": 0=auto (GPU หลัก) 1=บังคับ gpu 2=บังคับ cpu
};
static OscConfig g_cfg;
static std::wstring g_build_root;

static std::wstring GetExecutablePath() {
    std::wstring path(32768, L'\0');
    DWORD length = GetModuleFileNameW(nullptr, path.data(), static_cast<DWORD>(path.size()));
    if (length == 0 || length >= static_cast<DWORD>(path.size())) return L"";
    path.resize(length);
    return path;
}

static std::wstring ResolveBuildPath(const std::wstring& value) {
    std::filesystem::path path(value);
    if (path.is_relative()) path = std::filesystem::path(g_build_root) / path;
    return path.lexically_normal().wstring();
}

static void InitBuildPaths() {
    const auto executable = GetExecutablePath();
    const auto executable_dir = std::filesystem::path(executable).parent_path();
    auto root = executable_dir.parent_path().parent_path().parent_path();
    for (auto current = executable_dir; !current.empty(); current = current.parent_path()) {
        std::error_code error;
        if (std::filesystem::exists(current / L"NvConfig" / L"nvidia-osc.json", error) ||
            std::filesystem::exists(current / L"NVIDIA Config" / L"nvidia-osc.json", error)) {
            root = current;
            break;
        }
        if (current == current.parent_path()) break;
    }
    g_build_root = root.lexically_normal().wstring();
    g_cfg.page_dir = (root / L"Overlay On-Screen Control" / L"NVIDIA NodeAPI" / L"osc").wstring();
    g_cfg.log_file = (root / L"Logs" / L"NVIDIA OSC.log").wstring();
    g_cfg.cache_path = (root / L"Overlay On-Screen Control" / L"NVIDIA OSC" / L"CefCache").wstring();
    g_cfg.subprocess_path = (root / L"Overlay On-Screen Control" / L"NVIDIA OSC" / L"NVIDIA OSC.exe").wstring();
}

static std::wstring CfgStr(const std::string& json, const char* key) {
    std::string pat = "\"" + std::string(key) + "\"";
    size_t p = json.find(pat);
    if (p == std::string::npos) return L"";
    p = json.find(':', p + pat.size());
    if (p == std::string::npos) return L"";
    size_t q1 = json.find('"', p);
    if (q1 == std::string::npos) return L"";
    size_t q2 = json.find('"', q1 + 1);
    if (q2 == std::string::npos) return L"";
    std::string v = json.substr(q1 + 1, q2 - q1 - 1);
    // unescape JSON: \\ → \ , \" → " (ตัวอื่น ๆ พอผ่านได้)
    std::wstring w; w.reserve(v.size());
    for (size_t i = 0; i < v.size(); i++) {
        if (v[i] == '\\' && i + 1 < v.size()) {
            char nx = v[i + 1];
            if (nx == '\\' || nx == '"' || nx == '/') { w.push_back((wchar_t)nx); i++; continue; }
            w.push_back(L'\\');   // escape แปลก ๆ — คง backslash ไว้
            continue;
        }
        w.push_back((wchar_t)(unsigned char)v[i]);
    }
    return w;
}
static double CfgNum(const std::string& json, const char* key, double def) {
    std::string pat = "\"" + std::string(key) + "\"";
    size_t p = json.find(pat);
    if (p == std::string::npos) return def;
    p = json.find(':', p + pat.size());
    if (p == std::string::npos) return def;
    return atof(json.c_str() + p + 1);
}
static void LoadConfig() {
    InitBuildPaths();
    std::error_code cfg_ec;
    auto cfg_candidate = std::filesystem::path(g_build_root) /
        L"NVIDIA Config" / L"nvidia-osc.json";
    if (!std::filesystem::exists(cfg_candidate, cfg_ec))
        cfg_candidate = std::filesystem::path(g_build_root) /
            L"NvConfig" / L"nvidia-osc.json";
    const auto config_path = cfg_candidate.wstring();
    HANDLE f = CreateFileW(config_path.c_str(),
        GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    char buf[8192]; DWORD n = 0; ReadFile(f, buf, sizeof(buf) - 1, &n, nullptr); CloseHandle(f);
    buf[n] = 0;
    std::string json(buf);
    std::wstring page = CfgStr(json, "page");
    if (!page.empty()) {
        // page = ...\osc\index.html → dir = ...\osc
        page = ResolveBuildPath(page);
        size_t k = page.rfind(L'\\');
        if (k != std::wstring::npos) g_cfg.page_dir = page.substr(0, k);
    }
    std::wstring lg = CfgStr(json, "logFile");
    if (!lg.empty()) g_cfg.log_file = ResolveBuildPath(lg);
    g_cfg.render_scale = CfgNum(json, "renderScale", 1.0);
    if (g_cfg.render_scale < 0.25 || g_cfg.render_scale > 1.0) g_cfg.render_scale = 1.0;
    g_cfg.design_w = (int)CfgNum(json, "designWidth", 0);
    g_cfg.design_h = (int)CfgNum(json, "designHeight", 0);
    g_cfg.page_port = (int)CfgNum(json, "pagePort", 3000);
    g_cfg.toggle_port = (int)CfgNum(json, "toggle", 59003);
    g_cfg.frame_rate = (int)CfgNum(json, "frameRate", 60);
    if (g_cfg.frame_rate < 10 || g_cfg.frame_rate > 120) g_cfg.frame_rate = 60;
    g_cfg.ui_scale = CfgNum(json, "uiScale", 1.0);
    if (g_cfg.ui_scale < 0.5 || g_cfg.ui_scale > 2.0) g_cfg.ui_scale = 1.0;
    g_cfg.show_on_boot = CfgNum(json, "showOnBoot", 0) != 0;
    std::wstring cp = CfgStr(json, "cachePath");
    if (!cp.empty()) g_cfg.cache_path = ResolveBuildPath(cp);
    std::wstring sp = CfgStr(json, "subprocessPath");
    if (!sp.empty()) g_cfg.subprocess_path = ResolveBuildPath(sp);
    g_cfg.debug_port = (int)CfgNum(json, "debugPort", 59099);
    g_cfg.gdi_compositor = CfgStr(json, "compositor") == L"gdi";
    g_cfg.windowed = CfgNum(json, "windowed", 1) != 0;
    g_cfg.max_fps = (int)CfgNum(json, "maxFps", 60);
    if (g_cfg.max_fps < 1 || g_cfg.max_fps > 240) g_cfg.max_fps = 240;
    g_cfg.show_fps = CfgNum(json, "showFps", 1) != 0;
    std::wstring renderModeStr = CfgStr(json, "renderMode");
    if (renderModeStr == L"gpu") g_cfg.render_mode = 1;
    else if (renderModeStr == L"cpu") g_cfg.render_mode = 2;
    else g_cfg.render_mode = 0;
}

static HANDLE g_log = INVALID_HANDLE_VALUE;
static void LogInit() {
    std::error_code error;
    std::filesystem::create_directories(std::filesystem::path(g_cfg.log_file).parent_path(), error);
    g_log = CreateFileW(g_cfg.log_file.c_str(), FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr);
}
void Log(const char* msg) {
    HANDLE f = g_log;
    if (f == INVALID_HANDLE_VALUE || !msg) return;
    SYSTEMTIME st; GetLocalTime(&st);
    char line[1200];
    int n = snprintf(line, sizeof(line), "%04d-%02d-%02d %02d:%02d:%02d %s\r\n",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, msg);
    if (n <= 0) return;
    const DWORD bytesToWrite = static_cast<DWORD>(
        n < static_cast<int>(sizeof(line)) ? n : sizeof(line) - 1);
    DWORD written = 0;
    WriteFile(f, line, bytesToWrite, &written, nullptr);
}
static void Log(const std::string& msg) {
    Log(msg.c_str());
}

// ═══ RENDER MODE (สเปก OWNER: GPU เป็นหลัก, fallback ต้องรายงานเสมอ — หลักการ 4.3) ═══
// GPU_SHARED = CEF shared D3D11 texture เต็ม GPU (OnAcceleratedPaint) — โหมดหลัก
// GPU_UPLOAD = CPU paint + GPU upload/shader (CompositeFrame/GpuUploadComposite)
// CPU_GDI    = GDI UpdateLayeredWindow (ทางสุดท้าย เมื่อ GPU ใช้ไม่ได้จริง)
static constexpr int OscRmGpuShared = 0;
static constexpr int OscRmGpuUpload = 1;
static constexpr int OscRmCpuGdi = 2;
static std::atomic<int> g_render_state{ OscRmGpuShared };
static const char* OscRenderStateName(int s) {
    switch (s) {
        case OscRmGpuShared: return "GPU_SHARED";
        case OscRmGpuUpload: return "GPU_UPLOAD";
        default: return "CPU_GDI";
    }
}
static void SetRenderState(int s, const char* reason) {
    const int prev = g_render_state.exchange(s, std::memory_order_relaxed);
    if (prev != s)
        Log(std::string("[mode] SWITCH ") + OscRenderStateName(prev) + " -> " +
            OscRenderStateName(s) + " reason=" + reason);
}
static void LogRenderMode(const char* event, const std::string& detail) {
    Log(std::string("[mode] ") + event + " RENDER_MODE=" +
        OscRenderStateName(g_render_state.load(std::memory_order_relaxed)) +
        (detail.empty() ? std::string() : (" " + detail)));
}

// Low-overhead OSC producer telemetry. Durations are CPU-side elapsed time;
// CopyResource timing measures command submission, not completion on the GPU.
struct OscPerfCounters {
    std::atomic<unsigned long long> viewCallbacks{ 0 };
    std::atomic<unsigned long long> emptyDirtyRects{ 0 };
    std::atomic<unsigned long long> skippedEmptyRects{ 0 };
    std::atomic<unsigned long long> rateLimitedPaints{ 0 };
    std::atomic<unsigned long long> processedPaints{ 0 };
    std::atomic<unsigned long long> callbackTotalUs{ 0 };
    std::atomic<unsigned long long> callbackMaxUs{ 0 };
    std::atomic<unsigned long long> compositeCalls{ 0 };
    std::atomic<unsigned long long> compositeTotalUs{ 0 };
    std::atomic<unsigned long long> compositeMaxUs{ 0 };
    std::atomic<unsigned long long> publishCalls{ 0 };
    std::atomic<unsigned long long> publishTotalUs{ 0 };
    std::atomic<unsigned long long> publishMaxUs{ 0 };
    std::atomic<unsigned long long> presentCalls{ 0 };
    std::atomic<unsigned long long> presentSuccesses{ 0 };
    std::atomic<unsigned long long> presentBusyRetries{ 0 };
    std::atomic<unsigned long long> copyCalls{ 0 };
    std::atomic<unsigned long long> copyCallTotalUs{ 0 };
    std::atomic<unsigned long long> copyCallMaxUs{ 0 };
    std::atomic<unsigned long long> presentTotalUs{ 0 };
    std::atomic<unsigned long long> presentMaxUs{ 0 };
    std::atomic<unsigned long long> presentBusyDrops{ 0 };
    std::atomic<unsigned long long> commitTotalUs{ 0 };
    std::atomic<unsigned long long> commitMaxUs{ 0 };
};
static OscPerfCounters g_osc_perf;

static unsigned long long OscPerfNowUs() {
    static const double ticksPerSecond = []() {
        LARGE_INTEGER frequency{};
        if (!QueryPerformanceFrequency(&frequency) || frequency.QuadPart <= 0)
            return 1.0;
        return static_cast<double>(frequency.QuadPart);
    }();
    LARGE_INTEGER counter{};
    if (!QueryPerformanceCounter(&counter))
        return GetTickCount64() * 1000ULL;
    return static_cast<unsigned long long>(
        static_cast<double>(counter.QuadPart) * 1000000.0 / ticksPerSecond);
}

static void OscPerfAccumulate(
    std::atomic<unsigned long long>& total,
    std::atomic<unsigned long long>& maximum,
    unsigned long long elapsedUs) {
    total.fetch_add(elapsedUs, std::memory_order_relaxed);
    auto observed = maximum.load(std::memory_order_relaxed);
    while (observed < elapsedUs &&
        !maximum.compare_exchange_weak(
            observed, elapsedUs, std::memory_order_relaxed)) {}
}

static void OscPerfRecordComposite(unsigned long long elapsedUs) {
    g_osc_perf.compositeCalls.fetch_add(1, std::memory_order_relaxed);
    OscPerfAccumulate(g_osc_perf.compositeTotalUs, g_osc_perf.compositeMaxUs, elapsedUs);
}

static void OscPerfRecordPublish(unsigned long long elapsedUs) {
    g_osc_perf.publishCalls.fetch_add(1, std::memory_order_relaxed);
    OscPerfAccumulate(g_osc_perf.publishTotalUs, g_osc_perf.publishMaxUs, elapsedUs);
}

static bool OscNonBlockingPresentEnabled() {
    static const bool enabled = []() {
        char value[8] = {};
        return GetEnvironmentVariableA("OSC_PRESENT_DONOTWAIT",
            value, sizeof(value)) > 0 && value[0] == '1';
    }();
    return enabled;
}

static void OscPerfRecordPresent(unsigned long long presentUs,
    unsigned long long commitUs, bool busyDropped = false,
    bool busyRetry = false, bool presentSucceeded = true) {
    g_osc_perf.presentCalls.fetch_add(1, std::memory_order_relaxed);
    if (presentSucceeded)
        g_osc_perf.presentSuccesses.fetch_add(1, std::memory_order_relaxed);
    if (busyDropped)
        g_osc_perf.presentBusyDrops.fetch_add(1, std::memory_order_relaxed);
    if (busyRetry)
        g_osc_perf.presentBusyRetries.fetch_add(1, std::memory_order_relaxed);
    OscPerfAccumulate(g_osc_perf.presentTotalUs, g_osc_perf.presentMaxUs, presentUs);
    OscPerfAccumulate(g_osc_perf.commitTotalUs, g_osc_perf.commitMaxUs, commitUs);
}

static unsigned long long OscPerfAverage(unsigned long long totalUs,
    unsigned long long count) {
    return count ? totalUs / count : 0;
}

static void OscPerfFinishCallback(unsigned long long startUs) {
    const auto elapsedUs = OscPerfNowUs() - startUs;
    OscPerfAccumulate(g_osc_perf.callbackTotalUs, g_osc_perf.callbackMaxUs, elapsedUs);

    static std::atomic<ULONGLONG> lastReportMs{ 0 };
    const ULONGLONG nowMs = GetTickCount64();
    ULONGLONG lastMs = lastReportMs.load(std::memory_order_relaxed);
    if (!lastMs) {
        lastReportMs.compare_exchange_strong(
            lastMs, nowMs, std::memory_order_relaxed);
        return;
    }
    if (nowMs - lastMs < 1000) return;
    if (!lastReportMs.compare_exchange_strong(
        lastMs, nowMs, std::memory_order_relaxed)) return;

    const ULONGLONG windowMs = (std::max)(1ULL, nowMs - lastMs);
    const auto callbacks = g_osc_perf.viewCallbacks.exchange(0, std::memory_order_relaxed);
    const auto empty = g_osc_perf.emptyDirtyRects.exchange(0, std::memory_order_relaxed);
    const auto skipped = g_osc_perf.skippedEmptyRects.exchange(0, std::memory_order_relaxed);
    const auto rateLimited = g_osc_perf.rateLimitedPaints.exchange(0, std::memory_order_relaxed);
    const auto processed = g_osc_perf.processedPaints.exchange(0, std::memory_order_relaxed);
    const auto callbackTotal = g_osc_perf.callbackTotalUs.exchange(0, std::memory_order_relaxed);
    const auto callbackMax = g_osc_perf.callbackMaxUs.exchange(0, std::memory_order_relaxed);
    const auto composites = g_osc_perf.compositeCalls.exchange(0, std::memory_order_relaxed);
    const auto compositeTotal = g_osc_perf.compositeTotalUs.exchange(0, std::memory_order_relaxed);
    const auto compositeMax = g_osc_perf.compositeMaxUs.exchange(0, std::memory_order_relaxed);
    const auto publishes = g_osc_perf.publishCalls.exchange(0, std::memory_order_relaxed);
    const auto publishTotal = g_osc_perf.publishTotalUs.exchange(0, std::memory_order_relaxed);
    const auto publishMax = g_osc_perf.publishMaxUs.exchange(0, std::memory_order_relaxed);
    const auto presents = g_osc_perf.presentCalls.exchange(0, std::memory_order_relaxed);
    const auto presentSuccesses = g_osc_perf.presentSuccesses.exchange(0, std::memory_order_relaxed);
    const auto presentBusyRetries = g_osc_perf.presentBusyRetries.exchange(0, std::memory_order_relaxed);
    const auto copyCalls = g_osc_perf.copyCalls.exchange(0, std::memory_order_relaxed);
    const auto copyTotal = g_osc_perf.copyCallTotalUs.exchange(0, std::memory_order_relaxed);
    const auto copyMax = g_osc_perf.copyCallMaxUs.exchange(0, std::memory_order_relaxed);
    const auto presentTotal = g_osc_perf.presentTotalUs.exchange(0, std::memory_order_relaxed);
    const auto presentMax = g_osc_perf.presentMaxUs.exchange(0, std::memory_order_relaxed);
    const auto presentBusyDrops = g_osc_perf.presentBusyDrops.exchange(0, std::memory_order_relaxed);
    const auto commitTotal = g_osc_perf.commitTotalUs.exchange(0, std::memory_order_relaxed);
    const auto commitMax = g_osc_perf.commitMaxUs.exchange(0, std::memory_order_relaxed);

    char line[900];
    sprintf_s(line, sizeof(line),
        "[oscperf] window_ms=%llu view_cb/s=%llu empty_dirty/s=%llu skipped_empty/s=%llu rate_limited/s=%llu processed/s=%llu "
        "callback_us(avg/max)=%llu/%llu composite_n=%llu composite_us(avg/max)=%llu/%llu "
        "publish_n=%llu publish_us(avg/max)=%llu/%llu present_n=%llu present_ok=%llu "
        "present_busy_retries=%llu present_busy_drops=%llu copy_n=%llu copy_call_us(avg/max)=%llu/%llu "
        "present_us(avg/max)=%llu/%llu commit_us(avg/max)=%llu/%llu mode=%s",
        static_cast<unsigned long long>(windowMs),
        callbacks * 1000ULL / windowMs, empty * 1000ULL / windowMs,
        skipped * 1000ULL / windowMs,
        rateLimited * 1000ULL / windowMs, processed * 1000ULL / windowMs,
        OscPerfAverage(callbackTotal, callbacks), callbackMax,
        composites, OscPerfAverage(compositeTotal, composites), compositeMax,
        publishes, OscPerfAverage(publishTotal, publishes), publishMax,
        presents, presentSuccesses, presentBusyRetries, presentBusyDrops,
        copyCalls, OscPerfAverage(copyTotal, copyCalls), copyMax,
        OscPerfAverage(presentTotal, presents), presentMax,
        OscPerfAverage(commitTotal, presents), commitMax,
        OscRenderStateName(g_render_state.load(std::memory_order_relaxed)));
    Log(line);
}

// ★ VEH: จับ backtrace ของ int3 (CHECK ใน libcef) ก่อน process ตาย — เขียน module+offset
//   ⚠ ห้ามจับ 0xE06D7363 (C++ exception) — d3d11 โยนเองเป็นปกติทุกเฟรม และการเดิน
//     stack+module ใน VEH ระหว่าง exception dispatch = กระตุกหนัก (บทเรียน 10:2x)
static LONG WINAPI CrashVecHandler(EXCEPTION_POINTERS* ep) {
    const DWORD c = ep->ExceptionRecord->ExceptionCode;
    if (c != 0x80000003 && c != 0xC0000409) return EXCEPTION_CONTINUE_SEARCH;
    void* frames[24];
    USHORT n = CaptureStackBackTrace(0, 24, frames, nullptr);
    std::string bt = "[crash] code=0x" + [](unsigned long h) { char b[12]; snprintf(b, sizeof(b), "%lX", h); return std::string(b); }(c) + " bt:";
    for (USHORT i = 0; i < n; i++) {
        HMODULE hm = nullptr;
        char mod[MAX_PATH] = "?";
        if (GetModuleHandleExA(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS |
            GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCSTR)frames[i], &hm)) {
            GetModuleFileNameA(hm, mod, MAX_PATH);
            const char* base = strrchr(mod, '\\'); base = base ? base + 1 : mod;
            char off[24]; snprintf(off, sizeof(off), "+%llX",
                (unsigned long long)((char*)frames[i] - (char*)hm));
            bt += std::string(" ") + base + off;
        }
    }
    Log(bt);
    // รายชื่อ module ทั้งหมด (จับ DLL ที่ถูก inject โดยโปรแกรมอื่น)
    HMODULE mods[512]; DWORD needed = 0;
    if (EnumProcessModules(GetCurrentProcess(), mods, sizeof(mods), &needed)) {
        int n = needed / sizeof(HMODULE); if (n > 512) n = 512;
        std::string list = "[crash] modules(" + std::to_string(n) + "):";
        for (int i = 0; i < n; i++) {
            char m[MAX_PATH]; if (GetModuleFileNameA(mods[i], m, MAX_PATH)) {
                const char* b = strrchr(m, '\\'); b = b ? b + 1 : m;
                list += std::string(" ") + b;
            }
        }
        Log(list);
    }
    return EXCEPTION_CONTINUE_SEARCH;
}

// ================= state =================
static HWND g_hwnd = nullptr;
static HWND g_clickthrough_hwnd = nullptr;
static CefRefPtr<CefBrowser> g_browser;
static std::atomic<bool> g_devtools_open{ false };
static std::atomic<ULONGLONG> g_devtools_close_grace_until{ 0 };
static std::atomic<unsigned> g_page_retry_attempts{ 0 };
static std::string g_page_url;          // URL หน้า OSC — กู้คืนเมื่อ renderer ตายกลางทาง
static int g_pending_page_action = -1;   // external show/hide received before browser creation
static std::atomic<bool> g_visible{ false };
static bool g_close_registered = false;
static CefRefPtr<CefMessageRouterBrowserSide::Callback> g_close_callback;
static HANDLE g_close_event = nullptr;   // สัญญาณปิด OSC สำหรับ long-poll /close-event ของ shim
static int g_view_w = 0, g_view_h = 0;     // ขนาด view = CSS px (สูตรแท้: 1920×1200 ที่จอ 1680×1050)
static double g_render_dsf = 1.0;          // device scale factor = fit ของจอ (เส้นทาง rasterize แบบแท้)

// ================= compositor: UpdateLayeredWindow per-pixel alpha =================
static HDC g_hdc_mem = nullptr;
static HBITMAP g_hbm = nullptr, g_hbm_old = nullptr;
static unsigned char* g_bgra_bits = nullptr;
static int g_bmp_w = 0, g_bmp_h = 0;

static void EnsureShownOnce();  // (นิยามอยู่หลัง show/hide เดิม) เปิดหน้าต่างครั้งเดียวหลัง composite แรก

// ══════════ GPU compositor (แบบ offscreen_renderer_d3d11 ของแท้) ══════════
// CEF OnAcceleratedPaint ส่ง shared D3D11 texture มา → เปิดใน device ของเรา
// → premultiply ด้วย pixel shader → วาดลง swapchain (alpha premultiplied)
// → DComp ผูกกับหน้าต่างเดิม → DWM จัดการต่อ — CPU ~0
static ID3D11Device* g_gpu_dev = nullptr;
static ID3D11DeviceContext* g_gpu_ctx = nullptr;
static IDXGISwapChain1* g_gpu_swap = nullptr;
static ID3D11RenderTargetView* g_gpu_rtv = nullptr;
static ID3D11PixelShader* g_gpu_ps = nullptr;        // premul (upload path)
static ID3D11PixelShader* g_gpu_ps_pass = nullptr;   // passthrough (shared path)
static ID3D11VertexShader* g_gpu_vs = nullptr;
static ID3D11VertexShader* g_gpu_vs_shared = nullptr;
static ID3D11SamplerState* g_gpu_samp = nullptr;
static bool g_gpu_ok = false;           // GPU path ใช้ได้
static std::atomic<bool> g_gpu_frame{ false };
static bool g_gpu_shared = false;       // ขอ shared texture จาก CEF (เต็ม GPU)
static ULONGLONG g_page_loaded_ms = 0;  // เฝ้า: shared ไม่ยิงใน 8 วิ → สลับโหมด upload
static ID3D11Texture2D* g_up_tex = nullptr;   // texture รับ upload (โหมดกึ่ง GPU)
static int g_up_w = 0, g_up_h = 0;
static bool g_up_initialized = false;
static IDCompositionDevice* g_dcomp_dev = nullptr;
static IDCompositionTarget* g_dcomp_target = nullptr;
static IDCompositionVisual* g_dcomp_visual = nullptr;
static IDXGIDevice2* g_dxgi_dev2 = nullptr;
static bool g_dcomp_bound = false;
static ID3D11Texture2D* g_own_rt = nullptr;   // RT ของเรา (back buffer ของ swapchain เครื่องนี้ bind=0 วาดไม่ได้)
static int g_own_w = 0, g_own_h = 0;
static std::atomic<bool> g_game_focused{ false };
static std::atomic<bool> g_ui_open_snapshot{ false };
static std::atomic<bool> g_notifier_overlay_visible{ false };
static bool OscFramePublisherOverlayVisible() {
    return g_ui_open_snapshot.load(std::memory_order_acquire) ||
        g_notifier_overlay_visible.load(std::memory_order_acquire);
}
static constexpr UINT WM_OSC_FOCUS_MODE = WM_APP + 0x41;
static constexpr UINT WM_OSC_GPU_FRAME_READY = WM_APP + 0x42;
// Desktop shared-texture rendering stays on the CEF callback; only Present is queued.
static std::mutex g_desktop_present_queue_mutex;
static HANDLE g_desktop_present_frame_event = nullptr; // auto-reset
static HANDLE g_desktop_present_stop_event = nullptr;  // manual-reset
static HANDLE g_desktop_present_thread = nullptr;
static std::atomic<bool> g_desktop_present_shutting_down{ false };

static ULONGLONG g_adapter_frame_burst_deadline = 0;
static bool g_adapter_frame_burst_force_until_deadline = false;
static constexpr UINT AdapterFrameBurstTimerId = 7;
static constexpr ULONGLONG AdapterFrameBurstDurationMs = 10000;
static constexpr UINT AdapterFrameBurstIntervalMs = 16;

static void StartAdapterFrameBurst(const char* reason,
    bool forceUntilDeadline = false) {
    if (!g_hwnd) return;
    const ULONGLONG now = GetTickCount64();
    const bool burstAlreadyForced =
        g_adapter_frame_burst_deadline > now &&
        g_adapter_frame_burst_force_until_deadline;
    const bool overlayVisible =
        g_ui_open_snapshot.load(std::memory_order_acquire) ||
        g_notifier_overlay_visible.load(std::memory_order_acquire);
    if (!forceUntilDeadline && !burstAlreadyForced && !overlayVisible)
        return;

    g_adapter_frame_burst_force_until_deadline =
        forceUntilDeadline || burstAlreadyForced;
    g_adapter_frame_burst_deadline = now + AdapterFrameBurstDurationMs;
    SetTimer(g_hwnd, AdapterFrameBurstTimerId,
        AdapterFrameBurstIntervalMs, nullptr);
    Log(std::string("[adapter-bridge] 60fps frame refresh burst started for 10s: ") +
        reason);
}

struct HookFullscreenWindowSearch {
    DWORD gamePid = 0;
    bool fullscreen = false;
};

static BOOL CALLBACK FindFullscreenHookGameWindow(HWND hwnd, LPARAM parameter) {
    auto* search = reinterpret_cast<HookFullscreenWindowSearch*>(parameter);
    if (!search || !IsWindowVisible(hwnd) || IsIconic(hwnd) ||
        GetWindow(hwnd, GW_OWNER))
        return TRUE;

    DWORD windowPid = 0;
    GetWindowThreadProcessId(hwnd, &windowPid);
    if (windowPid != search->gamePid)
        return TRUE;

    const LONG_PTR style = GetWindowLongPtrW(hwnd, GWL_STYLE);
    if ((style & (WS_CAPTION | WS_THICKFRAME | WS_BORDER)) != 0)
        return TRUE;

    RECT client{};
    if (!GetClientRect(hwnd, &client) || client.right <= client.left ||
        client.bottom <= client.top)
        return TRUE;

    POINT topLeft{ client.left, client.top };
    POINT bottomRight{ client.right, client.bottom };
    if (!ClientToScreen(hwnd, &topLeft) ||
        !ClientToScreen(hwnd, &bottomRight))
        return TRUE;

    const HMONITOR monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
    MONITORINFO monitorInfo{ sizeof(monitorInfo) };
    if (!monitor || !GetMonitorInfoW(monitor, &monitorInfo))
        return TRUE;

    // Hook Mode accepts borderless/exclusive-style fullscreen only:
    // the game's client area must cover the complete physical monitor.
    // A maximized ordinary window still has a caption/frame and is rejected above.
    constexpr LONG edgeTolerance = 1;
    const RECT& screen = monitorInfo.rcMonitor;
    if (topLeft.x > screen.left + edgeTolerance ||
        topLeft.y > screen.top + edgeTolerance ||
        bottomRight.x < screen.right - edgeTolerance ||
        bottomRight.y < screen.bottom - edgeTolerance)
        return TRUE;

    search->fullscreen = true;
    return FALSE;
}

static bool IsHookGameFullscreen(DWORD gamePid) {
    if (!gamePid)
        return false;

    HookFullscreenWindowSearch search{};
    search.gamePid = gamePid;
    EnumWindows(FindFullscreenHookGameWindow,
        reinterpret_cast<LPARAM>(&search));
    return search.fullscreen;
}

static bool IsHookGameForeground() {
    const auto* consumerAdapterInfo = osc::d3d11_frame_publisher::GetConsumerAdapterInfo();
    if (!consumerAdapterInfo ||
        consumerAdapterInfo->magic != osc_frame_bridge::ConsumerAdapterMagic ||
        consumerAdapterInfo->version != osc_frame_bridge::ConsumerAdapterVersion)
        return false;

    const LONG gamePid = consumerAdapterInfo->ownerProcessId;
    if (gamePid <= 0)
        return false;

    if (!IsHookGameFullscreen(static_cast<DWORD>(gamePid)))
        return false;

    return osc_frame_bridge::IsGameOrNvidiaOverlayForeground(
        static_cast<std::uint32_t>(gamePid));
}

static bool IsHookGameForegroundStable() {
    static std::mutex mutex;
    static bool initialized = false;
    static bool stableState = false;
    static bool candidateState = false;
    static ULONGLONG candidateSince = 0;
    constexpr ULONGLONG transitionDelayMs = 200;

    const bool foreground = IsHookGameForeground();
    const ULONGLONG now = GetTickCount64();
    std::lock_guard<std::mutex> lock(mutex);
    if (!initialized) {
        initialized = true;
        stableState = foreground;
        candidateState = foreground;
        candidateSince = now;
        return stableState;
    }
    if (foreground == stableState) {
        candidateState = foreground;
        candidateSince = now;
        return stableState;
    }
    if (foreground != candidateState) {
        candidateState = foreground;
        candidateSince = now;
        return stableState;
    }
    if (now - candidateSince >= transitionDelayMs)
        stableState = candidateState;
    return stableState;
}

// วาดลง RT ของเราเสมอ แล้ว copy ลง back buffer — บังคับทุกเส้นทางผ่านนี้
static bool EnsureOwnRT(int w, int h) {
    if (g_own_rt && g_own_w == w && g_own_h == h) return true;
    if (g_own_rt) { g_own_rt->Release(); g_own_rt = nullptr; g_gpu_rtv->Release(); g_gpu_rtv = nullptr; }
    D3D11_TEXTURE2D_DESC td{};
    td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
    // ★ TYPELESS (10:5x): shared texture ของ CEF เป็น TYPELESS — CopyResource
    //   ต้องการ format ตรงกัน ไม่งั้นเฟลเงียบ → ownRT ไม่เคยมีเนื้อหา = จอโปร่งตลอด
    td.Format = DXGI_FORMAT_B8G8R8A8_TYPELESS; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(g_gpu_dev->CreateTexture2D(&td, nullptr, &g_own_rt))) return false;
    // RTV บังคับ UNORM (typeless ต้องระบุ view format จริง)
    D3D11_RENDER_TARGET_VIEW_DESC rd{};
    rd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
    rd.ViewDimension = D3D11_RTV_DIMENSION_TEXTURE2D;
    if (FAILED(g_gpu_dev->CreateRenderTargetView(g_own_rt, &rd, &g_gpu_rtv))) return false;
    g_own_w = w; g_own_h = h;
    return true;
}
enum class OscPresentResult { Presented, Busy, Failed, Queued };
static bool OscQueueDesktopPresentFrame();
static OscPresentResult PresentCurrentBackBuffer(bool forceNonBlocking);

// The CEF callback copies the already-composited RT to the swap-chain buffer.
// In Desktop shared-texture mode it queues only Present(), never the shared handle.
static OscPresentResult PresentToSwap(int w, int h, bool deferDesktopPresent = false) {
    ID3D11Texture2D* back = nullptr;
    if (FAILED(g_gpu_swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back)) || !back)
        return OscPresentResult::Failed;

    const auto copyStartUs = OscPerfNowUs();
    g_gpu_ctx->CopyResource(back, g_own_rt);
    const auto copyCallUs = OscPerfNowUs() - copyStartUs;
    back->Release();
    g_osc_perf.copyCalls.fetch_add(1, std::memory_order_relaxed);
    OscPerfAccumulate(g_osc_perf.copyCallTotalUs, g_osc_perf.copyCallMaxUs, copyCallUs);

    if (deferDesktopPresent &&
        !g_game_focused.load(std::memory_order_acquire)) {
        if (OscQueueDesktopPresentFrame())
            return OscPresentResult::Queued;
        Log("[gpu] Desktop Present worker unavailable; falling back to synchronous Present");
    }
    return PresentCurrentBackBuffer(false);
}

static OscPresentResult PresentCurrentBackBuffer(bool forceNonBlocking) {
    const bool hookMode = g_game_focused.load(std::memory_order_acquire);
    if (forceNonBlocking && hookMode)
        return OscPresentResult::Failed;

    const bool nonBlockingPresent =
        forceNonBlocking || (hookMode && OscNonBlockingPresentEnabled());
    static std::atomic<int> lastPresentPolicy{ -1 };
    const int presentPolicy = forceNonBlocking ? 3 :
        (hookMode ? (nonBlockingPresent ? 2 : 1) : 0);
    const int previousPolicy = lastPresentPolicy.exchange(
        presentPolicy, std::memory_order_relaxed);
    if (presentPolicy != previousPolicy) {
        const char* policyName = forceNonBlocking ? "desktop/worker-nonblocking" :
            (hookMode ? (nonBlockingPresent ? "hook/nonblocking" : "hook/normal-present") :
                "desktop/normal-present");
        Log(std::string("[gpu] Present policy=") + policyName);
    }

    const auto presentStartUs = OscPerfNowUs();
    const HRESULT hp = g_gpu_swap->Present(0,
        nonBlockingPresent ? DXGI_PRESENT_DO_NOT_WAIT : 0);
    const auto presentUs = OscPerfNowUs() - presentStartUs;
    if (nonBlockingPresent && hp == DXGI_ERROR_WAS_STILL_DRAWING) {
        // Hook keeps the existing drop policy. Desktop worker retries the latest
        // back-buffer contents without asking CEF to render/copy the frame again.
        OscPerfRecordPresent(presentUs, 0, !forceNonBlocking,
            forceNonBlocking, false);
        return OscPresentResult::Busy;
    }

    static ULONGLONG last_hr_log = 0;
    const ULONGLONG now = GetTickCount64();
    if (FAILED(hp) && now - last_hr_log > 3000) {
        last_hr_log = now;
        char b[80];
        snprintf(b, sizeof(b), "[gpu] Present FAILED hr=0x%08lX", (unsigned long)hp);
        Log(b);
    }

    unsigned long long commitUs = 0;
    if (g_dcomp_dev) {
        const auto commitStartUs = OscPerfNowUs();
        g_dcomp_dev->Commit();
        commitUs = OscPerfNowUs() - commitStartUs;
    }
    OscPerfRecordPresent(presentUs, commitUs, false, false, SUCCEEDED(hp));
    static bool pres_log = false;
    if (!pres_log) {
        pres_log = true;
        Log("[gpu] composite PRESENT ok (own-rt + copy)");
    }
    if (SUCCEEDED(hp)) g_gpu_frame.store(true);
    return SUCCEEDED(hp) ? OscPresentResult::Presented : OscPresentResult::Failed;
}

static const char* kQuadVS =
    "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD; };"
    "VSOut VS(uint id : SV_VertexID) {"
    "  VSOut o;"
    "  float2 p = float2((id << 1) & 2, id & 2);"
    "  o.uv = p;"
    "  o.pos = float4(p.x * 2 - 1, 1 - p.y * 2, 0, 1);"
    "  return o; }";
static const char* kSharedQuadVS =
    "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD; };"
    "VSOut VS(uint id : SV_VertexID) {"
    "  VSOut o;"
    "  float2 p = float2((id << 1) & 2, id & 2);"
    "  o.uv = float2(p.x, 1 - p.y);"
    "  o.pos = float4(p.x * 2 - 1, 1 - p.y * 2, 0, 1);"
    "  return o; }";
static const char* kPremulPS =
    "Texture2D tex : register(t0); SamplerState samp : register(s0);"
    "float4 PS(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target {"
    "  float4 c = tex.Sample(samp, uv);"
    "  return float4(c.rgb * c.a, c.a); }";   // สำหรับ OnPaint (straight alpha)
static const char* kTestPS =
    "float4 PS(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target {"
    "  return float4(1, 0, 0, 1); }";   // วินิจฉัย: แดงล้วน ไม่แตะ texture
static const char* kPassPS =
    "Texture2D tex : register(t0); SamplerState samp : register(s0);"
    "float4 PS(float4 pos : SV_Position, float2 uv : TEXCOORD) : SV_Target {"
    "  return tex.Sample(samp, uv); }";       // shared texture ของ CEF = premultiplied มาแล้ว ห้ามซ้ำ

static OscPresentResult DrawSharedTexture(ID3D11ShaderResourceView* srv,
    int w, int h) {
    if (!srv || !g_gpu_vs_shared || !g_gpu_ps_pass || !EnsureOwnRT(w, h))
        return OscPresentResult::Failed;

    D3D11_VIEWPORT vp{ 0, 0, (float)w, (float)h, 0, 1 };
    g_gpu_ctx->OMSetRenderTargets(1, &g_gpu_rtv, nullptr);
    g_gpu_ctx->RSSetViewports(1, &vp);
    g_gpu_ctx->PSSetShaderResources(0, 1, &srv);
    g_gpu_ctx->PSSetSamplers(0, 1, &g_gpu_samp);
    g_gpu_ctx->VSSetShader(g_gpu_vs_shared, nullptr, 0);
    g_gpu_ctx->PSSetShader(g_gpu_ps_pass, nullptr, 0);
    g_gpu_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
    g_gpu_ctx->Draw(3, 0);
    ID3D11ShaderResourceView* null_srv = nullptr;
    g_gpu_ctx->PSSetShaderResources(0, 1, &null_srv);
    return PresentToSwap(w, h, true);
}

static bool GpuCompositorInit(HWND hwnd, int w, int h) {
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
        nullptr, 0, D3D11_SDK_VERSION, &g_gpu_dev, &fl, &g_gpu_ctx);
    if (FAILED(hr)) { Log("[gpu] device FAILED hr=" + std::to_string((long)hr)); return false; }
    {
        // [FIX-B] publish worker Maps staging textures off-thread while the
        // UI thread composites; driver-side locking must be enabled.
        ID3D11Multithread* mt = nullptr;
        if (SUCCEEDED(g_gpu_ctx->QueryInterface(
                __uuidof(ID3D11Multithread),
                reinterpret_cast<void**>(&mt)))) {
            mt->SetMultithreadProtected(TRUE);
            mt->Release();
        }
    }
    Log("[gpu] device OK");

    IDXGIDevice2* dxgiDev = nullptr; IDXGIFactory2* fac = nullptr;
    IDXGISwapChain1* swap = nullptr; ID3D11PixelShader* ps = nullptr;
    ID3D11VertexShader* vs = nullptr; ID3D11VertexShader* shared_vs = nullptr;
    ID3D11SamplerState* samp = nullptr;

    if (FAILED(hr = g_gpu_dev->QueryInterface(__uuidof(IDXGIDevice2), (void**)&dxgiDev)))
        { Log("[gpu] step QI dxgiDev hr=" + std::to_string((long)hr)); goto fail; }
    {
        // ไดรเวอร์บางตัวคืน factory รุ่น base — GetParent ทีละชั้น (device→adapter→factory) แล้ว QI
        IDXGIAdapter* ad = nullptr;
        if (FAILED(hr = dxgiDev->GetParent(__uuidof(IDXGIAdapter), (void**)&ad)))
            { Log("[gpu] step adapter hr=" + std::to_string((long)hr)); goto fail; }
        IDXGIFactory* f1 = nullptr;
        if (FAILED(hr = ad->GetParent(__uuidof(IDXGIFactory), (void**)&f1)))
            { ad->Release(); Log("[gpu] step factory1 hr=" + std::to_string((long)hr)); goto fail; }
        ad->Release();
        hr = f1->QueryInterface(__uuidof(IDXGIFactory2), (void**)&fac);
        f1->Release();
        if (FAILED(hr)) { Log("[gpu] step factory2 QI hr=" + std::to_string((long)hr)); goto fail; }
    }
    {
        DXGI_SWAP_CHAIN_DESC1 sc{}; sc.Width = w; sc.Height = h;
        sc.Format = DXGI_FORMAT_B8G8R8A8_UNORM; sc.SampleDesc.Count = 1;
        sc.BufferCount = 2; sc.SwapEffect = DXGI_SWAP_EFFECT_FLIP_SEQUENTIAL;
        sc.AlphaMode = DXGI_ALPHA_MODE_PREMULTIPLIED;
        if (FAILED(hr = fac->CreateSwapChainForComposition(g_gpu_dev, &sc, nullptr, &swap)))
            { Log("[gpu] step swapchain hr=" + std::to_string((long)hr)); goto fail; }
        Log("[gpu] swapchain OK (premultiplied)");
    }
    {   // DComp: ผูก swapchain เข้าหน้าต่าง
        IDCompositionDevice* dcompDev = nullptr;
        if (FAILED(hr = DCompositionCreateDevice(dxgiDev, __uuidof(IDCompositionDevice), (void**)&dcompDev)))
            { Log("[gpu] step dcomp dev hr=" + std::to_string((long)hr)); goto fail; }
        // ★ ห้ามสร้าง target ที่นี่ (บทเรียน 10:2x): CreateTargetForHwnd ต่อ hwnd ได้ "ครั้งเดียว"
        //   ตลอดชีวิตหน้าต่าง — สร้างที่ boot แล้ว BindDcompTarget ตอนโชว์จะ FAILED ตลอด
        //   → visual ไม่เคยถูกผูก = GPU path วาดไม่ลงจอ — ให้ BindDcompTarget สร้างครั้งเดียวตอนโชว์
        g_dcomp_dev = dcompDev;
        g_dxgi_dev2 = dxgiDev; dxgiDev->AddRef();
        Log("[gpu] dcomp device ready (target จะสร้างตอนโชว์)");
        dxgiDev->Release();
    }
    {   // shaders
        ID3DBlob* vb = nullptr; ID3DBlob* pb = nullptr; ID3DBlob* err = nullptr;
        if (FAILED(hr = D3DCompile(kQuadVS, strlen(kQuadVS), nullptr, nullptr, nullptr, "VS", "vs_5_0", 0, 0, &vb, &err)))
            { if (err) { Log(std::string("[gpu] VS compile: ") + (char*)err->GetBufferPointer()); err->Release(); } goto fail; }
        if (FAILED(hr = D3DCompile(kPremulPS, strlen(kPremulPS), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &pb, &err)))
            { if (vb) vb->Release(); if (err) { Log(std::string("[gpu] PS compile: ") + (char*)err->GetBufferPointer()); err->Release(); } goto fail; }
        hr = g_gpu_dev->CreateVertexShader(vb->GetBufferPointer(), vb->GetBufferSize(), nullptr, &vs);
        if (SUCCEEDED(hr)) hr = g_gpu_dev->CreatePixelShader(pb->GetBufferPointer(), pb->GetBufferSize(), nullptr, &ps);
        vb->Release(); pb->Release();
        if (FAILED(hr)) { Log("[gpu] create shaders hr=" + std::to_string((long)hr)); goto fail; }
        ID3DBlob* shared_vb = nullptr; ID3DBlob* shared_err = nullptr;
        if (FAILED(hr = D3DCompile(kSharedQuadVS, strlen(kSharedQuadVS), nullptr, nullptr, nullptr,
                "VS", "vs_5_0", 0, 0, &shared_vb, &shared_err))) {
            if (shared_err) {
                Log(std::string("[gpu] shared VS compile: ") + (char*)shared_err->GetBufferPointer());
                shared_err->Release();
            }
            goto fail;
        }
        hr = g_gpu_dev->CreateVertexShader(shared_vb->GetBufferPointer(),
            shared_vb->GetBufferSize(), nullptr, &shared_vs);
        shared_vb->Release();
        if (shared_err) shared_err->Release();
        if (FAILED(hr)) { Log("[gpu] create shared VS hr=" + std::to_string((long)hr)); goto fail; }
        if (GetEnvironmentVariableA("OSC_PSTEST", nullptr, 0) > 0) {
            ID3DBlob* pt = nullptr; ID3DBlob* et = nullptr;
            if (SUCCEEDED(D3DCompile(kTestPS, strlen(kTestPS), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &pt, &et))) {
                if (g_gpu_ps_pass) g_gpu_ps_pass->Release();
                g_gpu_dev->CreatePixelShader(pt->GetBufferPointer(), pt->GetBufferSize(), nullptr, &g_gpu_ps_pass);
                g_gpu_ps = g_gpu_ps_pass;   // ทับทั้งสองเส้นทาง
                Log("[diag] OSC_PSTEST=1 → solid red PS");
            }
            if (pt) pt->Release(); if (et) et->Release();
        }
        {   // passthrough PS สำหรับ shared texture (premultiplied แล้ว)
            ID3DBlob* pb2 = nullptr; ID3DBlob* err2 = nullptr;
            if (SUCCEEDED(D3DCompile(kPassPS, strlen(kPassPS), nullptr, nullptr, nullptr, "PS", "ps_5_0", 0, 0, &pb2, &err2)))
                g_gpu_dev->CreatePixelShader(pb2->GetBufferPointer(), pb2->GetBufferSize(), nullptr, &g_gpu_ps_pass);
            else if (err2) Log(std::string("[gpu] pass PS compile: ") + (char*)err2->GetBufferPointer());
            if (pb2) pb2->Release();
            if (err2) err2->Release();
        }
        D3D11_SAMPLER_DESC sd{}; sd.Filter = D3D11_FILTER_MIN_MAG_LINEAR_MIP_POINT;
        sd.AddressU = sd.AddressV = sd.AddressW = D3D11_TEXTURE_ADDRESS_CLAMP;
        sd.ComparisonFunc = D3D11_COMPARISON_NEVER; sd.MaxLOD = D3D11_FLOAT32_MAX;
        if (FAILED(hr = g_gpu_dev->CreateSamplerState(&sd, &samp))) { Log("[gpu] sampler hr=" + std::to_string((long)hr)); goto fail; }
    }
    g_gpu_swap = swap; g_gpu_vs = vs; g_gpu_vs_shared = shared_vs;
    g_gpu_ps = ps; g_gpu_samp = samp;
    dxgiDev->Release(); fac->Release();
    g_gpu_ok = true;
    Log("[gpu] D3D11 compositor ready (shared texture + DComp premultiplied)");
    return true;
fail:
    if (swap) swap->Release(); if (ps) ps->Release(); if (vs) vs->Release();
    if (shared_vs) shared_vs->Release(); if (samp) samp->Release();
    if (fac) fac->Release(); if (dxgiDev) dxgiDev->Release();
    Log("[gpu] init FAILED → fallback CPU path");
    return false;
}

// ★ สถานะ OSC (ย้ายขึ้นบน — ให้ GpuComposite/GPUDUMP ใช้ได้)
static std::vector<RECT> g_ui_rects;          // hit-test bounds in display coordinates
static std::mutex g_ui_rects_mutex;
static std::vector<RECT> g_notifier_rects;   // การ์ดที่กดได้ขณะหน้า base คลิกทะลุ
static std::mutex g_notifier_rects_mutex;
static double g_css_scale = 1.0;             // css px → จอ px (zoom แท้ fit)
static std::atomic<int> g_disp_w{ 0 }, g_disp_h{ 0 }, g_dst_x{ 0 }, g_dst_y{ 0 };
static bool g_ui_open = false;
static std::atomic<bool> g_overlay_keyboard_capture{ false };
static std::atomic<bool> g_route_clickthrough{ true };
static bool g_applied_clickthrough = false;
static bool g_input_mode_initialized = false;
static HWND g_prev_fore = nullptr;           // หน้าต่างที่ถือโฟกัสก่อนเปิด OSC (คืนตอนปิด)
static int g_foreground_restore_attempts = 0;
static int g_osc_activation_attempts = 0;

// The base route is click-through while the OSC is closed (notifier-only).
// Once the user opens the OSC, it must accept input even if the page returns
// to the base hash while Hook Mode is switching focus away from the HWND.
static bool IsOverlayInputClickThrough() {
    return g_route_clickthrough.load(std::memory_order_acquire) &&
        !g_ui_open_snapshot.load(std::memory_order_acquire);
}

// วาด shared texture ของ CEF ลง swapchain (เรียกจาก OnAcceleratedPaint — เธรด UI ของ CEF)
static ID3D11Texture2D* g_shared_tex = nullptr;   // ★ cache: handle เดิม = เปิด/SRV ใหม่ไม่ต้อง (12:1x)
static ID3D11ShaderResourceView* g_shared_srv = nullptr;
static uint64_t g_shared_handle_cached = 0;

static ID3D11Device* OscHostGpuDevice() { return g_gpu_dev; }
static ID3D11DeviceContext* OscHostGpuContext() { return g_gpu_ctx; }
static ID3D11Texture2D* OscHostSharedTexture() { return g_shared_tex; }
static HWND OscHostWindow() { return g_hwnd; }
static UINT OscHostViewWidth() { return g_view_w > 0 ? static_cast<UINT>(g_view_w) : 0; }
static UINT OscHostViewHeight() { return g_view_h > 0 ? static_cast<UINT>(g_view_h) : 0; }
static bool OscHostGameFocused() { return g_game_focused.load(std::memory_order_acquire); }
static bool OscHostOverlayVisible() { return OscFramePublisherOverlayVisible(); }
static void OscHostD3D11PublisherLog(const std::string& message) { Log(message); }

static OscPresentResult GpuComposite(uint64_t shared_handle, int w, int h,
    const std::vector<CefRect>& dirty_rects) {
    if (!g_gpu_ok) return OscPresentResult::Failed;
    // ★ วัดจังหวะเฟรม (12:1x): มาติด ๆ 16ms = scheduler ปกติ / ระเบิดชุด = begin-frame สะดุด
    {
        static ULONGLONG last = 0; static int b1 = 0, b2 = 0, b3 = 0, b4 = 0, n = 0;
        ULONGLONG now = GetTickCount64();
        ULONGLONG d = last ? now - last : 0; last = now;
        if (d) {
            n++;
            if (d <= 20) b1++; else if (d <= 40) b2++; else if (d <= 100) b3++; else b4++;
        }
        if (n >= 120) {
            char b[140]; snprintf(b, sizeof(b), "[rhythm] <=20ms:%d 21-40:%d 41-100:%d >100:%d (ของ 120 เฟรมล่าสุด)", b1, b2, b3, b4);
            Log(b); b1 = b2 = b3 = b4 = 0; n = 0;
        }
    }
    ID3D11Resource* tex = nullptr;
    ID3D11ShaderResourceView* srv = nullptr;
    if (g_shared_tex && g_shared_srv && g_shared_handle_cached == shared_handle) {
        // ★ cache hit: CEF ใช้ handle เดิมทุกเฟรม — ไม่ต้อง open+SRV ใหม่ (คอขวด 6-14fps)
        tex = g_shared_tex; srv = g_shared_srv;
    } else {
        if (g_shared_tex) { g_shared_tex->Release(); g_shared_tex = nullptr; }
        if (g_shared_srv) { g_shared_srv->Release(); g_shared_srv = nullptr; }
        {
            // CEF มอบ NT shared handle → เปิดผ่าน OpenSharedResource1 (แบบ legacy ล้มกับ NT)
            ID3D11Device1* dev1 = nullptr;
            bool opened = false;
            if (SUCCEEDED(g_gpu_dev->QueryInterface(__uuidof(ID3D11Device1), (void**)&dev1))) {
                opened = SUCCEEDED(dev1->OpenSharedResource1((HANDLE)(uintptr_t)shared_handle,
                    __uuidof(ID3D11Resource), (void**)&tex));
                if (!opened)   // บางแหล่งส่ง legacy handle — ลองแบบเก่าคือถูก
                    opened = SUCCEEDED(g_gpu_dev->OpenSharedResource((HANDLE)(uintptr_t)shared_handle,
                        __uuidof(ID3D11Resource), (void**)&tex));
                dev1->Release();
            }
            if (!opened) {
                static int fails = 0;
                if (fails++ < 5) Log("[gpu] open shared handle FAILED handle=0x" +
                    std::to_string((unsigned long long)shared_handle));
                return OscPresentResult::Failed;
            }
        }
        // shared texture ของ CEF อาจถูกสร้างเป็น TYPELESS — SRV desc ต้องบังคับรูปแบบจริง
        {
            D3D11_SHADER_RESOURCE_VIEW_DESC sd{};
            sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
            sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
            sd.Texture2D.MipLevels = 1;
            if (FAILED(g_gpu_dev->CreateShaderResourceView(tex, &sd, &srv)) &&
                FAILED(g_gpu_dev->CreateShaderResourceView(tex, nullptr, &srv))) {
                static int srf = 0;
                if (srf++ < 3) Log("[gpu] shared SRV create FAILED");
                tex->Release(); return OscPresentResult::Failed;
            }
        }
        g_shared_tex = static_cast<ID3D11Texture2D*>(tex);
        g_shared_srv = srv;
        g_shared_handle_cached = shared_handle;
        Log("[gpu] shared texture cached handle=0x" + std::to_string((unsigned long long)shared_handle));
    }
    // ★ วินิจฉัย: อ่านพิกเซลจริงจาก shared texture — เฉพาะ OSC_GPUDUMP=1
    //   [P2#7 Copilot] อ่านเป็น y*RowPitch + x*4 พร้อม bounds check (เดิมสลับแกน+อ่านเกิน)
    {
        static int dumped = 0;
        char pdg[4] = { 0 };
        bool pdOn = (GetEnvironmentVariableA("OSC_GPUDUMP", pdg, 4) > 0 && pdg[0] == '1');
        dumped++;
        if (pdOn && (dumped < 3 || dumped % 300 == 0)) {
            ID3D11Texture2D* t2 = nullptr;
            if (FAILED(tex->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&t2)) || !t2) { return OscPresentResult::Failed; }  // cache เป็นเจ้าของ — ห้าม release
            D3D11_TEXTURE2D_DESC sd2{};
            t2->GetDesc(&sd2);
            D3D11_TEXTURE2D_DESC st = sd2;
            st.Usage = D3D11_USAGE_STAGING; st.BindFlags = 0; st.CPUAccessFlags = D3D11_CPU_ACCESS_READ; st.MiscFlags = 0;
            ID3D11Texture2D* staging = nullptr;
            if (SUCCEEDED(g_gpu_dev->CreateTexture2D(&st, nullptr, &staging))) {
                g_gpu_ctx->CopyResource(staging, tex);
                D3D11_MAPPED_SUBRESOURCE mp{};
                if (SUCCEEDED(g_gpu_ctx->Map(staging, 0, D3D11_MAP_READ, 0, &mp))) {
                    unsigned char* px = (unsigned char*)mp.pData;
                    int midx = sd2.Width / 2, midy = sd2.Height / 2;
                    int px_x = 840, px_y = 959;   // จุด pill: x=840, y=959 (y*RowPitch + x*4)
                    if (px_y >= sd2.Height) px_y = sd2.Height - 1;
                    if (px_x >= sd2.Width) px_x = sd2.Width - 1;
                    size_t mid = (size_t)midy * mp.RowPitch + (size_t)midx * 4;
                    size_t pill = (size_t)px_y * mp.RowPitch + (size_t)px_x * 4;
                    char buf[220];
                    snprintf(buf, sizeof(buf), "[gpu] tex %ux%u fmt=%d px0=%02X%02X%02X%02X pxmid=%02X%02X%02X%02X pill=%02X%02X%02X%02X",
                        sd2.Width, sd2.Height, (int)sd2.Format,
                        px[0], px[1], px[2], px[3], px[mid], px[mid+1], px[mid+2], px[mid+3],
                        px[pill], px[pill+1], px[pill+2], px[pill+3]);
                    Log(buf);
                    g_gpu_ctx->Unmap(staging, 0);
                }
                staging->Release();
            }
            t2->Release();
        }
    }

    OscPresentResult result = OscPresentResult::Failed;
    if (EnsureOwnRT(w, h)) {
        ID3D11Texture2D* source = nullptr;
        if (SUCCEEDED(tex->QueryInterface(__uuidof(ID3D11Texture2D),
                (void**)&source)) && source) {
            D3D11_TEXTURE2D_DESC desc{};
            source->GetDesc(&desc);
            if ((int)desc.Width == w && (int)desc.Height == h) {
                result = DrawSharedTexture(srv, w, h);
            } else {
                Log("[gpu] shared texture size mismatch");
            }
            source->Release();
        }
    }
    // ★ tex/srv = cache ของ g_shared_tex/g_shared_srv — ห้าม release ต่อ frame (12:1x)
    return result;
}

// โหมดกึ่ง GPU (fallback): CEF ส่ง BGRA มาทาง CPU ครั้งเดียว → upload → shader premultiply
// ไม่มี SetDIBits/UpdateLayeredWindow/premultiply-loop เลย — GDI=0
static void GpuUploadComposite(const void* bgra, int w, int h,
    const std::vector<CefRect>& dirty_rects) {
    if (!g_gpu_ok) return;
    if (!g_up_tex || g_up_w != w || g_up_h != h) {
        if (g_up_tex) { g_up_tex->Release(); g_up_tex = nullptr; }
        D3D11_TEXTURE2D_DESC td{};
        td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DEFAULT; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        if (FAILED(g_gpu_dev->CreateTexture2D(&td, nullptr, &g_up_tex))) {
            static int ctf = 0; if (ctf++ < 3) Log("[gpu] upload CreateTexture2D FAILED"); return;
        }
        g_up_w = w; g_up_h = h;
        g_up_initialized = false;
    }
    if (!g_up_initialized) {
        g_gpu_ctx->UpdateSubresource(g_up_tex, 0, nullptr, bgra, w * 4, 0);
        g_up_initialized = true;
    } else {
        bool updated = false;
        for (const CefRect& rect : dirty_rects) {
            int left = (std::max)(0, rect.x);
            int top = (std::max)(0, rect.y);
            int right = (int)(std::min)((LONGLONG)w,
                (LONGLONG)rect.x + rect.width);
            int bottom = (int)(std::min)((LONGLONG)h,
                (LONGLONG)rect.y + rect.height);
            if (right <= left || bottom <= top) continue;
            D3D11_BOX box{
                (UINT)left, (UINT)top, 0,
                (UINT)right, (UINT)bottom, 1
            };
            const unsigned char* source = (const unsigned char*)bgra +
                ((size_t)top * w + left) * 4;
            g_gpu_ctx->UpdateSubresource(g_up_tex, 0, &box, source,
                w * 4, 0);
            updated = true;
        }
        if (!updated) return;
    }
    // ★ วินิจฉัย: พิกเซลแรกของ buffer ที่ CEF ส่งมา (ครั้งเดียว)
    {
        static int up_dumped = 0;
        up_dumped++;
        if (up_dumped < 3 || up_dumped == 300 || up_dumped == 1200 || up_dumped == 3000) {
            const unsigned char* b = (const unsigned char*)bgra;
            size_t mid = ((size_t)h / 2) * (size_t)w * 4 + (size_t)(w / 2) * 4;
            char buf[120];
            snprintf(buf, sizeof(buf), "[gpu] OnPaint buf %dx%d px0=%02X%02X%02X%02X pxmid=%02X%02X%02X%02X",
                w, h, b[0], b[1], b[2], b[3], b[mid], b[mid+1], b[mid+2], b[mid+3]);
            Log(buf);
        }
    }
    ID3D11ShaderResourceView* srv = nullptr;
    if (FAILED(g_gpu_dev->CreateShaderResourceView(g_up_tex, nullptr, &srv))) {
        static int usf = 0; if (usf++ < 3) Log("[gpu] upload SRV FAILED"); return;
    }

    if (EnsureOwnRT(w, h)) {
        D3D11_VIEWPORT vp{ 0, 0, (float)w, (float)h, 0, 1 };
        g_gpu_ctx->OMSetRenderTargets(1, &g_gpu_rtv, nullptr);
        g_gpu_ctx->RSSetViewports(1, &vp);
        g_gpu_ctx->PSSetShaderResources(0, 1, &srv);
        g_gpu_ctx->PSSetSamplers(0, 1, &g_gpu_samp);
        g_gpu_ctx->VSSetShader(g_gpu_vs, nullptr, 0);
        g_gpu_ctx->PSSetShader(g_gpu_ps, nullptr, 0);
        g_gpu_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
        g_gpu_ctx->Draw(3, 0);
        PresentToSwap(w, h);
    }
    srv->Release();
}

// Desktop Present worker ---------------------------------------------------------
// CEF reads its shared texture and copies the composited result into the swap-chain
// buffer during the callback. This worker only calls Present(), so CEF never holds
// its callback open while DWM/DXGI applies back-pressure.
static DWORD WINAPI OscDesktopPresentWorkerProc(LPVOID) {
    HANDLE waits[2] = { g_desktop_present_stop_event, g_desktop_present_frame_event };
    for (;;) {
        const DWORD wait = WaitForMultipleObjects(2, waits, FALSE, INFINITE);
        if (wait == WAIT_OBJECT_0 || wait == WAIT_FAILED) break;
        if (g_game_focused.load(std::memory_order_acquire)) continue;

        const ULONGLONG retryStartedMs = GetTickCount64();
        for (;;) {
            if (WaitForSingleObject(g_desktop_present_stop_event, 0) == WAIT_OBJECT_0)
                return 0;
            if (g_game_focused.load(std::memory_order_acquire))
                break;

            OscPresentResult result = OscPresentResult::Failed;
            {
                std::lock_guard<std::mutex> gpuLock(osc::d3d11_frame_publisher::Mutex());
                if (g_game_focused.load(std::memory_order_acquire))
                    break;
                result = PresentCurrentBackBuffer(true);
            }

            if (result == OscPresentResult::Presented) {
                if (g_hwnd) PostMessageW(g_hwnd, WM_OSC_GPU_FRAME_READY, 0, 0);
                break;
            }
            if (result == OscPresentResult::Failed)
                break;

            if (GetTickCount64() - retryStartedMs >= 1500) {
                g_osc_perf.presentBusyDrops.fetch_add(1, std::memory_order_relaxed);
                Log("[gpu] desktop Present retry timed out after 1500ms; latest frame dropped");
                break;
            }

            // New CEF frames signal the same auto-reset event after updating the
            // back buffer. Retry promptly on a new frame, otherwise poll gently.
            const DWORD retryWait = WaitForMultipleObjects(2, waits, FALSE, 2);
            if (retryWait == WAIT_OBJECT_0 || retryWait == WAIT_FAILED)
                return 0;
            // WAIT_TIMEOUT and a new-frame signal both retry the current back buffer.
        }
    }
    return 0;
}

static bool OscStartDesktopPresentWorker() {
    if (g_desktop_present_shutting_down.load(std::memory_order_acquire))
        return false;
    std::lock_guard<std::mutex> lock(g_desktop_present_queue_mutex);
    if (g_desktop_present_shutting_down.load(std::memory_order_acquire))
        return false;
    if (g_desktop_present_thread)
        return true;

    if (!g_desktop_present_frame_event)
        g_desktop_present_frame_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!g_desktop_present_stop_event)
        g_desktop_present_stop_event = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!g_desktop_present_frame_event || !g_desktop_present_stop_event) {
        Log("[gpu] desktop Present worker event creation failed");
        if (g_desktop_present_frame_event) {
            CloseHandle(g_desktop_present_frame_event);
            g_desktop_present_frame_event = nullptr;
        }
        if (g_desktop_present_stop_event) {
            CloseHandle(g_desktop_present_stop_event);
            g_desktop_present_stop_event = nullptr;
        }
        return false;
    }

    ResetEvent(g_desktop_present_stop_event);
    g_desktop_present_thread = CreateThread(
        nullptr, 0, OscDesktopPresentWorkerProc, nullptr, 0, nullptr);
    if (!g_desktop_present_thread) {
        Log("[gpu] desktop Present worker thread creation failed err=" +
            std::to_string(GetLastError()));
        CloseHandle(g_desktop_present_frame_event);
        CloseHandle(g_desktop_present_stop_event);
        g_desktop_present_frame_event = nullptr;
        g_desktop_present_stop_event = nullptr;
        return false;
    }
    Log("[gpu] desktop Present worker started (Present-only; nonblocking retry)");
    return true;
}

static bool OscQueueDesktopPresentFrame() {
    if (g_desktop_present_shutting_down.load(std::memory_order_acquire))
        return true;
    if (!OscStartDesktopPresentWorker())
        return false;

    std::lock_guard<std::mutex> lock(g_desktop_present_queue_mutex);
    if (g_desktop_present_shutting_down.load(std::memory_order_acquire))
        return true;
    if (!g_desktop_present_frame_event ||
        !SetEvent(g_desktop_present_frame_event)) {
        Log("[gpu] desktop Present worker signal failed err=" +
            std::to_string(GetLastError()));
        return false;
    }
    return true;
}

static void OscStopDesktopPresentWorker() {
    g_desktop_present_shutting_down.store(true, std::memory_order_release);
    HANDLE thread = nullptr;
    {
        std::lock_guard<std::mutex> lock(g_desktop_present_queue_mutex);
        thread = g_desktop_present_thread;
        if (g_desktop_present_stop_event)
            SetEvent(g_desktop_present_stop_event);
        if (!thread) {
            if (g_desktop_present_frame_event) {
                CloseHandle(g_desktop_present_frame_event);
                g_desktop_present_frame_event = nullptr;
            }
            if (g_desktop_present_stop_event) {
                CloseHandle(g_desktop_present_stop_event);
                g_desktop_present_stop_event = nullptr;
            }
            return;
        }
    }

    const DWORD wait = WaitForSingleObject(thread, 2000);
    if (wait != WAIT_OBJECT_0) {
        Log("[gpu] desktop Present worker did not stop within 2000ms; keeping handles valid");
        return;
    }

    std::lock_guard<std::mutex> lock(g_desktop_present_queue_mutex);
    if (g_desktop_present_thread == thread) {
        CloseHandle(g_desktop_present_thread);
        g_desktop_present_thread = nullptr;
    }
    if (g_desktop_present_frame_event) {
        CloseHandle(g_desktop_present_frame_event);
        g_desktop_present_frame_event = nullptr;
    }
    if (g_desktop_present_stop_event) {
        CloseHandle(g_desktop_present_stop_event);
        g_desktop_present_stop_event = nullptr;
    }
}

// ═══ display rects from QUERY_OSC_SET_DISPLAY_RECTS (page UI hit-test bounds) ═══
// หน้าบอก host ว่า UI อยู่ตรงไหน (พิกัด CSS) → host เก็บเป็น rect จอ
// Cross-process pass-through still requires a layered per-pixel-alpha surface.

// ★ สั่ง visibility จากเธรดอื่น (CEF/HTTP) ผ่าน message — หน้าต่างเป็นของเธรด CEF UI
static const UINT WM_OSC_VIS = WM_APP + 2;
static const UINT WM_OSC_ROUTE = WM_APP + 3;
static const UINT WM_OSC_PICKER_MODE = WM_APP + 4;

static int NotificationSafeOverlayWidth(int monitor_width) {
    // A monitor-sized top-level HWND puts Windows in QUNS_BUSY and suppresses notifications.
    return monitor_width > 1 ? monitor_width - 1 : monitor_width;
}

static bool SetForegroundWithInput(HWND target) {
    if (!target || !IsWindow(target)) return false;

    const DWORD current_thread = GetCurrentThreadId();
    const DWORD target_thread = GetWindowThreadProcessId(target, nullptr);
    HWND foreground = GetForegroundWindow();
    const DWORD foreground_thread = foreground
        ? GetWindowThreadProcessId(foreground, nullptr) : 0;
    bool attached_foreground = false;
    bool attached_target = false;
    if (foreground_thread && foreground_thread != current_thread)
        attached_foreground = AttachThreadInput(current_thread, foreground_thread, TRUE) != FALSE;
    if (target_thread && target_thread != current_thread &&
        target_thread != foreground_thread)
        attached_target = AttachThreadInput(current_thread, target_thread, TRUE) != FALSE;

    BringWindowToTop(target);
    const bool foreground_set = SetForegroundWindow(target) != FALSE;
    if (target == g_hwnd) {
        SetActiveWindow(target);
        if (g_browser) g_browser->GetHost()->SetFocus(true);
        HWND child = FindWindowExW(target, nullptr, L"Chrome_WidgetWin_1", nullptr);
        SetFocus(child ? child : target);
    }

    if (attached_target) AttachThreadInput(current_thread, target_thread, FALSE);
    if (attached_foreground) AttachThreadInput(current_thread, foreground_thread, FALSE);
    return foreground_set && GetForegroundWindow() == target;
}

static bool RestorePreviousForeground() {
    if (!g_prev_fore || !IsWindow(g_prev_fore) || g_prev_fore == g_hwnd) {
        g_prev_fore = nullptr;
        return true;
    }
    if (GetForegroundWindow() != g_hwnd && GetForegroundWindow() != nullptr) {
        g_prev_fore = nullptr;
        return true;
    }
    if (!SetForegroundWithInput(g_prev_fore)) return false;
    g_prev_fore = nullptr;
    return true;
}

// A DComp HWND needs WS_EX_LAYERED as well as WS_EX_TRANSPARENT for cross-process
// hit-test pass-through. Keep its region intact: SetWindowRgn also clips visible pixels.
static void SetClickThrough(bool on) {
    if (!g_hwnd) return;
    LONG_PTR ex = GetWindowLongPtrW(g_hwnd, GWL_EXSTYLE);
    LONG_PTR want = ex;
    if (on) {
        want |= WS_EX_TRANSPARENT | WS_EX_NOACTIVATE;
        if (g_gpu_ok) want |= WS_EX_LAYERED;
    } else {
        want &= ~(WS_EX_TRANSPARENT | WS_EX_NOACTIVATE);
        if (g_gpu_ok) want &= ~WS_EX_LAYERED;
    }
    if (want == ex) return;
    SetLastError(ERROR_SUCCESS);
    LONG_PTR previous = SetWindowLongPtrW(g_hwnd, GWL_EXSTYLE, want);
    DWORD error = GetLastError();
    if (previous == 0 && error != ERROR_SUCCESS) {
        Log("[window] SetWindowLongPtr(GWL_EXSTYLE) FAILED err=" + std::to_string(error));
        return;
    }
    if (!SetWindowPos(g_hwnd, nullptr, 0, 0, 0, 0,
        SWP_NOMOVE | SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE | SWP_FRAMECHANGED)) {
        Log("[window] SWP_FRAMECHANGED FAILED err=" + std::to_string(GetLastError()));
    }
    Log(std::string("[win] URL-route-selected input styles ") +
        (on ? "enabled (layered cross-process click-through)" : "disabled (interactive)"));
}

static void ApplyOpenShareInputMode() {
    const bool clickthrough = IsOverlayInputClickThrough();
    const bool changed = !g_input_mode_initialized || g_applied_clickthrough != clickthrough;
    g_input_mode_initialized = true;
    g_applied_clickthrough = clickthrough;
    if (changed) SetClickThrough(clickthrough);
    // Keep the CEF host visible in both routes. In GPU mode its layered style
    // provides click-through without replacing the DComp surface with a blank proxy.
    if (!g_game_focused.load(std::memory_order_acquire) &&
        !IsWindowVisible(g_hwnd))
        ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
    if (g_clickthrough_hwnd && IsWindowVisible(g_clickthrough_hwnd))
        ShowWindow(g_clickthrough_hwnd, SW_HIDE);
}

static bool SetFullOverlayRegion() {
    if (!g_hwnd) return false;
    RECT bounds{};
    if (!GetWindowRect(g_hwnd, &bounds)) {
        Log("[window] GetWindowRect for hit-test region FAILED err=" +
            std::to_string(GetLastError()));
        return false;
    }
    HRGN full = CreateRectRgn(0, 0, bounds.right - bounds.left, bounds.bottom - bounds.top);
    if (!full) {
        Log("[window] CreateRectRgn(full) FAILED err=" + std::to_string(GetLastError()));
        return false;
    }
    if (SetWindowRgn(g_hwnd, full, TRUE)) return true;
    DWORD error = GetLastError();
    DeleteObject(full);
    Log("[window] restore fullscreen hit-test region FAILED err=" + std::to_string(error));
    return false;
}

static void ParseDisplayRects(const std::string& req) {
    // ดึง array หลัง "displayRects": [ {...}, ... ]
    size_t p = req.find("\"displayRects\"");
    if (p == std::string::npos) return;
    size_t ab = req.find('[', p);
    if (ab == std::string::npos) return;
    size_t ae = req.find(']', ab);
    if (ae == std::string::npos) ae = req.size();
    std::string body = req.substr(ab, ae - ab);
    std::vector<RECT> parsedRects;
    bool open = false;
    size_t pos = 0;
    while ((pos = body.find('{', pos)) != std::string::npos) {
        size_t cb = body.find('}', pos);
        if (cb == std::string::npos) break;
        std::string obj = body.substr(pos, cb - pos);
        pos = cb + 1;
        auto num = [&](const char* k)->double {
            size_t q = obj.find(k); if (q == std::string::npos) return -1e9;
            return atof(obj.c_str() + q + strlen(k));
        };
        double x = num("\"x\":"), y = num("\"y\":"), w = num("\"width\":"), h = num("\"height\":");
        if (x <= -1e8 || y <= -1e8) continue;
        if (w <= -1e8 || h <= -1e8) { w = 1; h = 1; }
        RECT r{};
        const int displayWidth = g_disp_w.load(std::memory_order_acquire);
        const int displayHeight = g_disp_h.load(std::memory_order_acquire);
        const int destinationX = g_dst_x.load(std::memory_order_relaxed);
        const int destinationY = g_dst_y.load(std::memory_order_relaxed);
        if (displayWidth > 0 && displayHeight > 0 && g_view_w > 0 && g_view_h > 0) {
            r.left = destinationX + static_cast<LONG>((LONGLONG)x * displayWidth / g_view_w);
            r.top = destinationY + static_cast<LONG>((LONGLONG)y * displayHeight / g_view_h);
            r.right = destinationX + static_cast<LONG>((LONGLONG)(x + w) * displayWidth / g_view_w);
            r.bottom = destinationY + static_cast<LONG>((LONGLONG)(y + h) * displayHeight / g_view_h);
        } else {
            r.left = destinationX + (LONG)(x * g_css_scale);
            r.top = destinationY + (LONG)(y * g_css_scale);
            r.right = destinationX + (LONG)((x + w) * g_css_scale);
            r.bottom = destinationY + (LONG)((y + h) * g_css_scale);
        }
        if (r.right > r.left && r.bottom > r.top) {
            parsedRects.push_back(r);
            open = true;
        }
    }

    const size_t rectCount = parsedRects.size();
    {
        std::lock_guard<std::mutex> lock(g_ui_rects_mutex);
        g_ui_rects.swap(parsedRects);
    }
    if (g_ui_open != open)
        Log(std::string("[ui] displayRects: ") + (open ? (std::to_string(rectCount) + " rects") : "empty"));
    // ★ state machine (สเปก OWNER): rects = ขอบเขตคลิกเท่านั้น — ห้ามแตะสถานะเด็ดขาด!
    //   (บั๊ก 10:4x: rects ตอนบูตพาง g_ui_open=true → hook กลืนคลิกทั้งจอทันทีที่เปิดโปรแกรม
    //   ทั้งที่ overlay ยังไม่แสดง — สถานะเปิด/ปิด = WIN_OPEN_OSC / WIN_CLOSE_OSC เท่านั้น)
}

static void RequestNotifierFrameRefresh();

static void ParseNotifierRects(const std::string& req) {
    size_t p = req.find("\"displayRects\"");
    if (p == std::string::npos) return;
    size_t ab = req.find('[', p);
    if (ab == std::string::npos) return;
    size_t ae = req.find(']', ab);
    if (ae == std::string::npos) ae = req.size();
    std::string body = req.substr(ab, ae - ab);
    std::vector<RECT> rects;
    size_t pos = 0;
    while ((pos = body.find('{', pos)) != std::string::npos) {
        size_t cb = body.find('}', pos);
        if (cb == std::string::npos) break;
        std::string obj = body.substr(pos, cb - pos);
        pos = cb + 1;
        auto num = [&](const char* key)->double {
            size_t q = obj.find(key);
            return q == std::string::npos ? -1e9 : atof(obj.c_str() + q + strlen(key));
        };
        double x = num("\"x\":"), y = num("\"y\":");
        double w = num("\"width\":"), h = num("\"height\":");
        if (x <= -1e8 || y <= -1e8 || w <= 0 || h <= 0) continue;
        RECT r{};
        const int displayWidth = g_disp_w.load(std::memory_order_acquire);
        const int displayHeight = g_disp_h.load(std::memory_order_acquire);
        const int destinationX = g_dst_x.load(std::memory_order_relaxed);
        const int destinationY = g_dst_y.load(std::memory_order_relaxed);
        if (displayWidth > 0 && displayHeight > 0 && g_view_w > 0 && g_view_h > 0) {
            r.left = destinationX + (LONG)((LONGLONG)x * displayWidth / g_view_w);
            r.top = destinationY + (LONG)((LONGLONG)y * displayHeight / g_view_h);
            r.right = destinationX + (LONG)((LONGLONG)(x + w) * displayWidth / g_view_w);
            r.bottom = destinationY + (LONG)((LONGLONG)(y + h) * displayHeight / g_view_h);
        } else {
            r.left = (LONG)(x * g_css_scale);
            r.top = (LONG)(y * g_css_scale);
            r.right = (LONG)((x + w) * g_css_scale);
            r.bottom = (LONG)((y + h) * g_css_scale);
        }
        if (r.right > r.left && r.bottom > r.top) rects.push_back(r);
    }
    const bool hasVisibleCards = !rects.empty();
    {
        std::lock_guard<std::mutex> lock(g_notifier_rects_mutex);
        g_notifier_rects = std::move(rects);
    }
    const bool wasVisible = g_notifier_overlay_visible.exchange(
        hasVisibleCards, std::memory_order_acq_rel);
    if (wasVisible != hasVisibleCards) {
        Log(std::string("[adapter-bridge] notifier visibility -> ") +
            (hasVisibleCards ? "visible" : "hidden") +
            (g_game_focused.load(std::memory_order_acquire)
                ? " (game focused)" : " (game unfocused)"));
    }
    if (hasVisibleCards && !wasVisible)
        StartAdapterFrameBurst("notifier shown");
    if (hasVisibleCards && !wasVisible &&
        g_game_focused.load(std::memory_order_acquire)) {
        RequestNotifierFrameRefresh();
    } else if (!hasVisibleCards &&
        !g_ui_open_snapshot.load(std::memory_order_acquire) &&
        g_game_focused.load(std::memory_order_acquire)) {
        osc::d3d11_frame_publisher::PublishMetadata(true);
    }
}

static void ClearNotifierRects() {
    std::lock_guard<std::mutex> lock(g_notifier_rects_mutex);
    g_notifier_rects.clear();
    g_notifier_overlay_visible.store(false, std::memory_order_release);
}

// view-scale state: ตำแหน่ง/ขนาดภาพที่ถูกวางลงจอ (ใช้แมพเมาส์จอ↔view)
static std::atomic<double> g_ui_adjust{ 1.0 };   // ปรับสดด้วย Ctrl+ลูกเมาส์

static void UpdateDisplayGeometry(int rasterWidth, int rasterHeight) {
    const int screenWidth = GetSystemMetrics(SM_CXSCREEN);
    const int screenHeight = GetSystemMetrics(SM_CYSCREEN);
    if (rasterWidth <= 0 || rasterHeight <= 0 || screenWidth <= 0 ||
        screenHeight <= 0)
        return;

    const double scale = (static_cast<double>(screenWidth) / rasterWidth) *
        g_cfg.ui_scale * g_ui_adjust.load(std::memory_order_relaxed);
    const int displayWidth = static_cast<int>(rasterWidth * scale);
    const int displayHeight = static_cast<int>(rasterHeight * scale);
    const int destinationX = (screenWidth - displayWidth) / 2;
    const int destinationY = (screenHeight - displayHeight) / 2;
    g_dst_x.store(destinationX, std::memory_order_relaxed);
    g_dst_y.store(destinationY, std::memory_order_relaxed);
    g_disp_w.store(displayWidth, std::memory_order_release);
    g_disp_h.store(displayHeight, std::memory_order_release);
}

// ★ สูตร zoom ของ Share.exe แท้ (วัดสดผ่าน CDP :9222 ของ Share วันที่ 2026-10-07 —
//   ตรง log แท้ [offscreen_window.cpp(1081)] setting zoom to: -0.732395):
//   fit = min(W/1920.0, H/1080.0) · level = log(fit)/log(1.2)
// ★ สรุปการทดลอง (2026-10-07 ลึก): zoom path = เส้นทางเดียวกับแท้ — ring 3px แบบแท้
//   (DSF path ทดลองแล้ว: outline คำนวณ 2.28571 → วาด 2px = บางกว่าแท้ จึงยกเลิก)
//   ส่วน "เหลื่อม 1px ตอน hover" แก้ที่ CSS โดยฉีด outline แทน border-swap (ดู injection)
static void ApplyGenuineZoom(bool force_log = false) {
    if (!g_browser) return;
    static double last = 999.0;
    int w = GetSystemMetrics(SM_CXSCREEN), h = GetSystemMetrics(SM_CYSCREEN);
    double fit = (double)w / 1920.0;
    double fh = (double)h / 1080.0;
    if (fh < fit) fit = fh;
    g_render_dsf = 1.0;
    double level = log(fit) / log(1.2);
    g_browser->GetHost()->SetZoomLevel(level);
    if (force_log || level != last) {
        last = level;
        Log("[zoom] zoom path: fit=" + std::to_string(fit) +
            " level=" + std::to_string(level));
    }
}

// buffer premultiplied ถาวร — เฟรมที่แล้วค้างไว้ อัปเดตเฉพาะ dirty rects ของเฟรมนี้
// ⚠ เคารพขอบเขต x ให้เป๊ะ: บัฟเฟอร์ของ CEF ใช้ได้เฉพาะใน dirty rect —
// อ่านเกินออกไป = ดึงพิกเซลเก่ามาทับ = UI สองสถานะซ้อนกัน (บทเรียน 06:2x)
static void PremultRect(const unsigned char* src, unsigned char* dst, int w,
    int x0, int x1, int y0, int y1) {
    for (int y = y0; y < y1; y++) {
        unsigned char* d = dst + ((size_t)w * y + x0) * 4;
        const unsigned char* s = src + ((size_t)w * y + x0) * 4;
        for (int i = 0; i < x1 - x0; i++) {
            unsigned char a = s[i * 4 + 3];
            d[i * 4 + 0] = (unsigned char)(s[i * 4 + 0] * a / 255);
            d[i * 4 + 1] = (unsigned char)(s[i * 4 + 1] * a / 255);
            d[i * 4 + 2] = (unsigned char)(s[i * 4 + 2] * a / 255);
            d[i * 4 + 3] = a;
        }
    }
}

static void CompositeFrame(const void* bgra, int w, int h, const std::vector<CefRect>& dirty) {
    if (!g_hwnd) return;
    // ★ ลำดับความเร็ว: (1) shared texture เต็ม GPU มาทาง OnAcceleratedPaint (ไม่ผ่านฟังก์ชันนี้)
    //   (2) GPU-assisted upload — memcpy เดียว + shader (ที่นี่) (3) GDI เดิม (รองสุดท้าย)
    if (g_gpu_ok) {
        {
            // CPU-paint upload shares the immediate context with other paint/timer paths.
            std::lock_guard<std::mutex> lock(osc::d3d11_frame_publisher::Mutex());
            GpuUploadComposite(bgra, w, h, dirty);
        }
        if (g_gpu_frame.load()) {
            if (g_render_state.load(std::memory_order_relaxed) != OscRmGpuShared)
                SetRenderState(OscRmGpuUpload, "GPU upload composite healthy");
            EnsureShownOnce(); return;
        }
        SetRenderState(OscRmCpuGdi, "GPU upload failed -> GDI composite this frame");
    }
    if (!g_hdc_mem || g_bmp_w != w || g_bmp_h != h) {
        if (g_hdc_mem) {
            SelectObject(g_hdc_mem, g_hbm_old);
            DeleteObject(g_hbm);
            DeleteDC(g_hdc_mem);
            g_hdc_mem = nullptr;
            g_hbm = nullptr;
            g_hbm_old = nullptr;
            g_bgra_bits = nullptr;
        }
        g_hdc_mem = CreateCompatibleDC(nullptr);
        if (!g_hdc_mem) {
            Log("[composite] CreateCompatibleDC FAILED err=" + std::to_string(GetLastError()));
            return;
        }
        BITMAPINFO bi{};
        bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
        bi.bmiHeader.biWidth = w;
        bi.bmiHeader.biHeight = -h;
        bi.bmiHeader.biPlanes = 1;
        bi.bmiHeader.biBitCount = 32;
        bi.bmiHeader.biCompression = BI_RGB;
        HBITMAP dib = CreateDIBSection(g_hdc_mem, &bi, DIB_RGB_COLORS,
            reinterpret_cast<void**>(&g_bgra_bits), nullptr, 0);
        if (!dib || !g_bgra_bits) {
            DWORD error = GetLastError();
            DeleteDC(g_hdc_mem);
            g_hdc_mem = nullptr;
            g_bgra_bits = nullptr;
            Log("[composite] CreateDIBSection FAILED err=" + std::to_string(error));
            return;
        }
        g_hbm = dib;
        g_hbm_old = (HBITMAP)SelectObject(g_hdc_mem, g_hbm);
        if (!g_hbm_old || g_hbm_old == HGDI_ERROR) {
            DWORD error = GetLastError();
            DeleteObject(g_hbm);
            DeleteDC(g_hdc_mem);
            g_hdc_mem = nullptr;
            g_hbm = nullptr;
            g_hbm_old = nullptr;
            g_bgra_bits = nullptr;
            Log("[composite] SelectObject(DIB) FAILED err=" + std::to_string(error));
            return;
        }
        g_bmp_w = w; g_bmp_h = h;
    }
    // CEF ให้ BGRA (ไม่ premultiply) — layered window ต้องการ premultiplied
    // ★ เต็มเฟรมทุกครั้ง: ในโหมด GPU+Canvas compositing, dirty rects ที่ CEF รายงาน
    //   ไม่ครอบคลุมบริเวณที่ถูกลบ (ผี UI สองสถานะซ้อนกัน — บทเรียน 06:29)
    //   ความถูกต้องมาก่อนความเร็ว: 7MB/premultiply-ต่อเฟรม ยังไหวที่ 60fps
    const unsigned char* src = (const unsigned char*)bgra;
    PremultRect(src, g_bgra_bits, w, 0, w, 0, h);
    (void)dirty;

    // ★ view-scale: หน้าเพจล็อกที่ design (เช่น 1920) zoom 100% เสมอ — layout ไม่ขยับ
    //   host ขยาย/ย่อ "ทั้งหน้า" เป็นภาพเดียว: S = (จอ/view) × uiScale × g_ui_adjust
    //   uiScale > 1 = ใหญ่กว่า fit → ภาพล้นจอ ตัดขอบกลางจอ (ขอบ = ผ้าม่านเปล่า ไม่มีองค์ประกอบ)
    //   g_ui_adjust หมุนได้สด ๆ ด้วย Ctrl+ลูกเมาส์ — เห็นผลทันทีไม่ต้องรีสตาร์ท
    int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
    UpdateDisplayGeometry(w, h);
    const int displayWidth = g_disp_w.load(std::memory_order_acquire);
    const int displayHeight = g_disp_h.load(std::memory_order_acquire);
    const int destinationX = g_dst_x.load(std::memory_order_relaxed);
    const int destinationY = g_dst_y.load(std::memory_order_relaxed);
    POINT dstpt{ destinationX, destinationY };
    // Leave a one-pixel gap at the right edge. UpdateLayeredWindow otherwise resets
    // the HWND to exact monitor width, which makes Windows classify it as full-screen
    // and suppress notifications even while the OSC is closed.
    SIZE winsz{ (std::max)(1, displayWidth - 1), displayHeight };
    POINT zerop{ 0, 0 };
    BLENDFUNCTION bf{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
    HDC scr2 = GetDC(nullptr);
    BOOL ulw = UpdateLayeredWindow(g_hwnd, scr2, &dstpt, &winsz, g_hdc_mem, &zerop, 0, &bf, ULW_ALPHA);
    ReleaseDC(nullptr, scr2);
    static bool last_ok = false;
    bool ok = ulw != 0;
    if (ok != last_ok) {
        last_ok = ok;
        Log(std::string("[composite] UpdateLayeredWindow ") + (ok ? "OK" : ("FAIL err=" + std::to_string(GetLastError()))));
    }
    // แบบของแท้: composite ได้เฟรมแรก (แม้โปร่งใสทั้งใบ) = เปิดหน้าต่างค้างตลอด
    if (ok) EnsureShownOnce();
}

// ================= cefQuery: ชุดคำสั่ง OSC ครบ 10 ตาม OSC-DATA-CONTRACT.md =================
// ทางเข้าสองทาง: (1) MessageRouter (สำรอง) (2) HTTP POST /cefquery ผ่าน page server
// (shim JS ฉีดเข้าหน้าเพจ — ไม่แตะ render process = ไม่ฆ่า compositor แบบ router เดิม)
static std::string ExtractStringTable(const std::string& req) {
    // ดึง object หลัง "stringTable": แบบ balanced-brace (รองรับ \" escape ใน HTML)
    size_t p = req.find("\"stringTable\"");
    if (p == std::string::npos) return "{}";
    size_t ob = req.find('{', p);
    if (ob == std::string::npos) return "{}";
    int depth = 0; bool in_str = false;
    for (size_t i = ob; i < req.size(); i++) {
        char ch = req[i];
        if (in_str) {
            if (ch == '\\') { i++; continue; }
            if (ch == '"') in_str = false;
            continue;
        }
        if (ch == '"') in_str = true;
        else if (ch == '{') depth++;
        else if (ch == '}') { depth--; if (depth == 0) return req.substr(ob, i - ob + 1); }
    }
    return "{}";
}

// ---- shared storage จริง: key=path join '/' value=JSON ดิบ (ไฟล์ .kv คู่กันแก้ไข) ----
static std::map<std::string, std::string> g_store;
static bool g_store_dirty = false;

// ★ auto-fallback: เกม exclusive fullscreen → GPU process ของ CEF CHECK ตาย
//   boot-pending.flag ค้าง = บูตก่อนพังตอนสร้าง browser → บูตนี้ใช้ --disable-gpu
//   ล้าง sw-render.flag เมื่อ UI เปิดได้จริง (WIN_OPEN_OSC) = บูตหน้าลอง GPU ใหม่
static std::wstring AppFlagPath(const wchar_t* name) {
    wchar_t exe[MAX_PATH]; GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring dir(exe); dir.resize(dir.find_last_of(L'\\'));
    return dir + L"\\appdata\\\\" + name;
}
static void TouchFlag(const wchar_t* name) {
    std::wstring p = AppFlagPath(name);
    CreateDirectoryW(p.substr(0, p.find_last_of(L'\\')).c_str(), nullptr);
    HANDLE f = CreateFileW(p.c_str(), GENERIC_WRITE, 0, nullptr,
        CREATE_ALWAYS, 0, nullptr);
    if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
}
static void ClearFlag(const wchar_t* name) { DeleteFileW(AppFlagPath(name).c_str()); }
static bool HasFlag(const wchar_t* name) {
    return GetFileAttributesW(AppFlagPath(name).c_str()) != INVALID_FILE_ATTRIBUTES;
}

static std::wstring StorePath() {
    // appdata คู่กับตัว exe (build tree — runtime data ไม่ไปกองใน Project)
    wchar_t exe[MAX_PATH]; GetModuleFileNameW(nullptr, exe, MAX_PATH);
    std::wstring dir(exe); dir.resize(dir.find_last_of(L'\\'));
    return dir + L"\\appdata\\sharedstorage.kv";
}
static void StoreLoad() {
    if (!g_store.empty()) return;
    HANDLE f = CreateFileW(StorePath().c_str(), GENERIC_READ, FILE_SHARE_READ,
        nullptr, OPEN_ALWAYS, 0, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        LARGE_INTEGER sz{}; GetFileSizeEx(f, &sz);
        std::string raw((size_t)sz.QuadPart, 0);
        DWORD rd = 0; ReadFile(f, raw.data(), (DWORD)raw.size(), &rd, nullptr);
        CloseHandle(f);
        size_t pos = 0;
        while (pos < raw.size()) {
            size_t eol = raw.find('\n', pos);
            if (eol == std::string::npos) eol = raw.size();
            std::string line = raw.substr(pos, eol - pos);
            pos = eol + 1;
            size_t tab = line.find('\t');
            if (tab != std::string::npos)
                g_store[line.substr(0, tab)] = line.substr(tab + 1);
        }
    }
}
static void StoreSave() {
    std::wstring dir = StorePath(); dir.resize(dir.find_last_of(L'\\'));
    CreateDirectoryW(dir.c_str(), nullptr);   // idempotent
    HANDLE f = CreateFileW(StorePath().c_str(), GENERIC_WRITE, FILE_SHARE_READ,
        nullptr, CREATE_ALWAYS, 0, nullptr);
    if (f != INVALID_HANDLE_VALUE) {
        std::string out;
        for (auto& kv : g_store) out += kv.first + "\t" + kv.second + "\n";
        DWORD w = 0; WriteFile(f, out.data(), (DWORD)out.size(), &w, nullptr);
        CloseHandle(f);
    }
    g_store_dirty = false;
}

// ดึง array ของ string หลัง "path":[ ... ] → join '/'
static std::string ExtractPathKey(const std::string& req) {
    size_t p = req.find("\"path\"");
    if (p == std::string::npos) return "";
    size_t ab = req.find('[', p);
    if (ab == std::string::npos) return "";
    std::string key;
    bool in_str = false; std::string cur;
    for (size_t i = ab; i < req.size(); i++) {
        char ch = req[i];
        if (in_str) {
            if (ch == '\\') { cur += ch; cur += req[++i]; continue; }
            if (ch == '"') { in_str = false; continue; }
            cur += ch;
            continue;
        }
        if (ch == '"') { in_str = true; continue; }
        if (ch == ']') break;
    }
    return cur;   // path ของหน้าเป็นชั้นเดียวในทางปฏิบัติ (ModsEnableStatus / osd-storage)
}

// ดึง object JSON หลัง "data": (balanced brace)
static std::string ExtractDataValue(const std::string& req) {
    size_t p = req.find("\"data\"");
    if (p == std::string::npos) return "{}";
    size_t ob = req.find_first_of("{[", p);
    if (ob == std::string::npos) return "{}";
    int depth = 0; bool in_str = false;
    for (size_t i = ob; i < req.size(); i++) {
        char ch = req[i];
        if (in_str) {
            if (ch == '\\') { i++; continue; }
            if (ch == '"') in_str = false;
            continue;
        }
        if (ch == '"') in_str = true;
        else if (ch == '{' || ch == '[') depth++;
        else if (ch == '}' || ch == ']') { depth--; if (depth == 0) return req.substr(ob, i - ob + 1); }
    }
    return "{}";
}

static void HandleOscQuery(const std::string& req, bool persistent,
    const std::function<void(const std::string&)>& reply) {
    std::string cmd;
    size_t p = req.find("\"command\"");
    if (p != std::string::npos) {
        size_t q1 = req.find('"', p + 9), q2 = req.find('"', q1 + 1);
        if (q1 != std::string::npos && q2 != std::string::npos)
            cmd = req.substr(q1 + 1, q2 - q1 - 1);
    }
    static int qcount = 0;
    if (qcount++ < 25 || cmd != "QUERY_OSC_SET_PAINTING")
        Log(std::string("[cefQuery] ") + cmd + (persistent ? " (persistent)" : ""));

    if (cmd == "QUERY_OSC_REGISTER_CLOSE_EVENT") {
        g_close_registered = true;
        reply("{}");
        return;
    }
    if (cmd == "QUERY_FULLSCREEN_STATE") {
        reply("{\"fullscreen\":false,\"hdractive\":false,\"borderlessMode\":false}");
        return;
    }
    if (cmd == "QUERY_WIN_NODE_INFO") {
        // ★ แบบแท้: Share.exe ส่ง node config ให้หน้าผ่าน NODE_INFO (route resolve "base"
        //   รอ localNodeInfo() ก่อนวาดเมนู — ขาด = หน้าค้าง #/base + socket ไปต่อ default
        //   :59001 ของแท้) — ของเรา: config จริงของ backend เรา (พอร์ตเดียว :59011)
        //   ★ jarvis.server ต้องเป็น URL จริง (ค่าแท้จาก piplConfig.json) — ใส่ "" แล้ว
        //     getClientTelemetryConsent สร้าง URL undefined → openOSC ตาย indexOf (09:3x)
        //   ★ ต้องมี field "secret" (NODE_INFO แท้ = {port, secret} — index.js:740) —
        //     หน้า localSdk.updateNodeInfo อ่าน t.secret → X_LOCAL_SECURITY_COOKIE header —
        //     ถ้าไม่มี = header undefined = ทุก XHR ตาย indexOf ใน NvEndpoint.i() (09:4x)
        reply("{\"port\":59011,\"disableSecurity\":true,"
              "\"secret\":\"\",\"securityCookie\":\"\","
              "\"gfwsl\":{\"server\":\"https://gfwsl.geforce.com/\"},"
              "\"jarvis\":{\"server\":\"https://accounts.nvgs.nvidia.com\"},"
              "\"gxtarget\":{\"server\":\"gx-target-experiments-frontend-api.gx.nvidia.com\","
              "\"cvEndpoint\":\"cloudvariables\",\"version\":\"v3\",\"clientId\":\"135333107684344109\"}}");
        return;
    }
    if (cmd == "QUERY_OSC_DISPLAY_IS_DESKTOP_MODE") {
        reply("true");   // หน้า JSON.parse("true") → boolean ตรงการใช้ (isInDesktopMode)
        return;
    }
    if (cmd == "QUERY_WIN_NODE_INFO") {
        reply("{\"port\":59001,\"secret\":\"0CA906D2784F0D14E399874F5C5ED4A1\"}");
        return;
    }
    if (cmd == "QUERY_LOAD_STRING_TABLE") {
        reply(ExtractStringTable(req));   // แปล identity (ค่า default อังกฤษ)
        return;
    }
    if (cmd == "QUERY_WRITE_SHARED_STORAGE") {
        StoreLoad();
        g_store[ExtractPathKey(req)] = ExtractDataValue(req);
        StoreSave();
        reply("{}");
        return;
    }
    if (cmd == "QUERY_READ_SHARED_STORAGE") {
        StoreLoad();
        auto it = g_store.find(ExtractPathKey(req));
        reply(it != g_store.end() ? it->second : "null");
        return;
    }

    if (cmd == "QUERY_OSC_GET_OPEN_STATE") {
        reply(g_ui_open_snapshot.load() ? "true" : "false");
        return;
    }

    if (cmd == "QUERY_OSC_GET_RENDER_MODE") {
        // สเปก 4.3: หน้า/UI ตรวจสอบโหมดเรนเดอร์จริงได้ทุกเมื่อ
        const char* cfgName = g_cfg.render_mode == 1 ? "gpu" :
            g_cfg.render_mode == 2 ? "cpu" : "auto";
        std::string json = std::string("{\"mode\":\"") +
            OscRenderStateName(g_render_state.load(std::memory_order_relaxed)) +
            "\",\"config\":\"" + cfgName +
            "\",\"gpuOk\":" + std::string(g_gpu_ok ? "true" : "false") +
            ",\"sharedRequested\":" + std::string(g_gpu_shared ? "true" : "false") + "}";
        reply(json);
        return;
    }

    if (cmd == "QUERY_OSC_SET_KEYBOARD_CAPTURE") {
        bool capture;
        if (req.find("\"capture\":true") != std::string::npos) {
            capture = true;
        } else if (req.find("\"capture\":false") != std::string::npos) {
            capture = false;
        } else {
            reply("{\"error\":\"capture must be a boolean\"}");
            return;
        }
        const bool wasCapturing = g_overlay_keyboard_capture.exchange(
            capture, std::memory_order_acq_rel);
        if (wasCapturing != capture)
            Log(std::string("[input] keyboard capture ") +
                (capture ? "enabled (focused text control)" :
                    "released (game hotkeys pass through)"));
        reply("{}");
        return;
    }

    if (cmd == "QUERY_OSC_SET_PICKER_MODE") {
        bool active;
        if (req.find("\"active\":true") != std::string::npos) {
            active = true;
        } else if (req.find("\"active\":false") != std::string::npos) {
            active = false;
        } else {
            reply("{\"error\":\"active must be a boolean\"}");
            return;
        }
        DWORD_PTR applied = FALSE;
        if (!g_hwnd || !SendMessageTimeoutW(g_hwnd, WM_OSC_PICKER_MODE,
                active ? 1 : 0, 0, SMTO_ABORTIFHUNG, 2000, &applied) || !applied) {
            reply("{\"error\":\"Could not change OSC picker z-order\"}");
            return;
        }
        reply("{}");
        return;
    }

    if (cmd == "QUERY_WIN_CLOSE_OSC") {
        // แบบของแท้: หน้าเพจเป็นเจ้าของ UI — ปิดเองทาง socket แล้ว
        g_ui_rects.clear();
        ClearNotifierRects();
        // [P1#1 Copilot] ส่งแค่ intent — สถานะให้ UI thread (WM_OSC_VIS) เป็นผู้เขียน
        if (g_hwnd) PostMessageW(g_hwnd, WM_OSC_VIS, 0, 0);
        reply("{}");
        return;
    }
    if (cmd == "QUERY_OSC_SET_DISPLAY_RECTS") {
        static bool shape_log = false;
        if (!shape_log) {
            shape_log = true;
            Log("[ui] SET_DISPLAY_RECTS payload: " + req.substr(0, 300));
        }
        ParseDisplayRects(req);
        reply("{}");
        return;
    }
    if (cmd == "QUERY_OSC_SET_NOTIFIER_RECTS") {
        ParseNotifierRects(req);
        reply("{}");
        return;
    }
    if (cmd == "QUERY_WIN_OPEN_OSC") {
        // UI เปิดได้ = render ใช้ได้ → ให้บูตหน้าลอง GPU เต็มรูปแบบอีกครั้ง
        ClearFlag(L"sw-render.flag");
        // [FIX-ROUTE-SYNC] The page also fires this from its boot path while
        // sitting on #/base. Honoring it there re-opens the UI right after
        // the page auto-closed itself — the other half of the base <->
        // main-menu ping-pong. Only honor it when the page actually left
        // the base route (a real open), or when we are already open (no-op
        // anyway). A boot ask on #/base while closed is just an ack.
        if (!g_ui_open && g_route_clickthrough.load(std::memory_order_acquire)) {
            reply("{}");
            return;
        }
        // [P1#1 Copilot] ส่งแค่ intent — สถานะให้ UI thread เป็นผู้เขียน
        if (g_hwnd) PostMessageW(g_hwnd, WM_OSC_VIS, 1, 0);
        reply("{}");
        return;
    }
    reply("{}");   // SET_PAINTING / WIN_OPEN_OSC = ack ล้วน
}

// ★ reply ต้องวิ่งบน CEF UI thread — HTTP thread เรียน callback->Success ตรง ๆ
//   = renderer ตาย (Crashpad TERMINATED ตลอดวัน) — post กลับเสมอ
class QueryReplyTask : public CefTask {
public:
    CefRefPtr<CefMessageRouterBrowserSide::Callback> cb;
    std::string msg;
    void Execute() override { if (cb) cb->Success(msg); }
    IMPLEMENT_REFCOUNTING(QueryReplyTask);
};

class RetryPageLoadTask : public CefTask {
public:
    RetryPageLoadTask(CefRefPtr<CefBrowser> browser, std::string url, unsigned attempt)
        : browser_(browser), url_(std::move(url)), attempt_(attempt) {}
    void Execute() override {
        if (!browser_ || url_ != g_page_url) return;
        CefRefPtr<CefFrame> frame = browser_->GetMainFrame();
        if (!frame || !frame->IsValid()) return;
        Log("[load] retrying OSC page (attempt " + std::to_string(attempt_) + ")");
        frame->LoadURL(url_);
    }
private:
    CefRefPtr<CefBrowser> browser_;
    std::string url_;
    unsigned attempt_;
    IMPLEMENT_REFCOUNTING(RetryPageLoadTask);
};

// ★ รัน JS ในหน้าจากเธรด HTTP (toggle route) — ต้อง post ไป TID_UI เสมอ
class JsEvalTask : public CefTask {
public:
    CefRefPtr<CefBrowser> browser;
    std::string js;
    JsEvalTask(CefRefPtr<CefBrowser> b, const std::string& j) : browser(b), js(j) {}
    void Execute() override {
        if (browser && browser->GetMainFrame()) {
            CefString url = browser->GetMainFrame()->GetURL();
            browser->GetMainFrame()->ExecuteJavaScript(js, url, 0);
        }
    }
    IMPLEMENT_REFCOUNTING(JsEvalTask);
};

class InvalidatePageTask : public CefTask {
public:
    explicit InvalidatePageTask(CefRefPtr<CefBrowser> browser)
        : browser_(browser) {}
    void Execute() override {
        if (browser_ && browser_->GetHost())
            browser_->GetHost()->Invalidate(PET_VIEW);
    }
private:
    CefRefPtr<CefBrowser> browser_;
    IMPLEMENT_REFCOUNTING(InvalidatePageTask);
};

static void RequestNotifierFrameRefresh() {
    if (g_browser &&
        !CefPostTask(TID_UI, new InvalidatePageTask(g_browser))) {
        Log("[adapter-bridge] failed to schedule notifier frame refresh");
    }
}

static void QueueOscPageAction(bool open) {
    if (!g_browser) {
        g_pending_page_action = open ? 1 : 0;
        Log(std::string("[toggle] page action pending: ") + (open ? "openOSC" : "closeOSC"));
        return;
    }

    std::string method = open ? "openOSC" : "closeOSC";
    std::string js =
        "(function(){var tries=0;function run(){try{"
        "var i=window.angular&&angular.element(document).injector();"
        "if(!i){if(++tries<50)return setTimeout(run,100);"
        "console.error('OSC display service injector unavailable');return;}"
        "var d=i.get('oscDisplayService'),r=i.get('$rootScope');"
        "if(r.$$phase)d." + method + "();"
        "else r.$apply(function(){d." + method + "();});"
        "}catch(e){console.error('OSC page action failed: '+e.message);}}run();})();";
    CefPostTask(TID_UI, new JsEvalTask(g_browser, js));
    Log(std::string("[toggle] page action dispatched: ") + method);
}

class OscQueryHandler : public CefMessageRouterBrowserSide::Handler {
public:
    bool OnQuery(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
        int64_t query_id, const CefString& request, bool persistent,
        CefRefPtr<Callback> callback) override {
        std::string req = request.ToString();
        HandleOscQuery(req, persistent, [callback](const std::string& r) {
            auto t = new QueryReplyTask();
            t->cb = callback;
            t->msg = r;
            CefPostTask(TID_UI, t);
        });
        return true;
    }
};

static void FireCloseEvent() {
    if (g_close_event) { SetEvent(g_close_event); }   // ปลุก long-poll /close-event ของ shim
    if (g_close_registered && g_close_callback) {
        g_close_callback->Success("close message");
        g_close_callback = nullptr;
        g_close_registered = false;
    }
}

// ================= หน้าต่าง: อยู่ตลอด แต่ topmost เฉพาะตอน OSC เปิด =================
// หน้า CEF ไม่ถูกทำลายหรือซ่อน; ตอนปิด HWND ถอยหลังหน้าต่างปกติเพื่อให้ input
// ไปถึงโปรเซสอื่นได้จริง (HTTRANSPARENT อย่างเดียวไม่ forward ข้ามเธรด/โปรเซส)
static void EnsureShownOnce() {
    static bool done = false;
    if (done || !g_hwnd) return;
    done = true;
    Log("[window] first composite — CEF surface is visible");
}

class OscDevToolsClient : public CefClient,
                          public CefLifeSpanHandler {
public:
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    void OnBeforeClose(CefRefPtr<CefBrowser>) override {
        g_devtools_open = false;
        g_devtools_close_grace_until = GetTickCount64() + 10000;
        Log("[devtools] window closed");
    }
    IMPLEMENT_REFCOUNTING(OscDevToolsClient);
};

static void OpenDevTools(CefRefPtr<CefBrowser> browser);

class OpenDevToolsTask : public CefTask {
public:
    explicit OpenDevToolsTask(CefRefPtr<CefBrowser> browser) : browser_(browser) {}
    void Execute() override { OpenDevTools(browser_); }
private:
    CefRefPtr<CefBrowser> browser_;
    IMPLEMENT_REFCOUNTING(OpenDevToolsTask);
};

static void OpenDevTools(CefRefPtr<CefBrowser> browser) {
    if (!browser) return;
    CefRefPtr<CefBrowserHost> host = browser->GetHost();
    g_devtools_open = true;
    if (g_hwnd) SetTimer(g_hwnd, 4, 500, nullptr);
    CefWindowInfo window_info;
    window_info.SetAsPopup(nullptr, "NVIDIA OSC DevTools");
    CefBrowserSettings settings;
    host->ShowDevTools(window_info, new OscDevToolsClient(),
        settings, CefPoint());
    Log(host->HasDevTools()
        ? "[devtools] shown from keyboard shortcut"
        : "[devtools] requested from keyboard shortcut");
}

// ================= client + OSR render handler =================
// ★ สร้าง browser หลัง init เสร็จสนิท (เรียกผ่าน CefPostDelayedTask)
class OscClient : public CefClient,
                  public CefLifeSpanHandler,
                  public CefLoadHandler,
                  public CefRenderHandler,
                  public CefDisplayHandler,
                  public CefRequestHandler,
                  public CefContextMenuHandler,
                  public CefKeyboardHandler {
public:
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
    CefRefPtr<CefRenderHandler> GetRenderHandler() override { return this; }
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    CefRefPtr<CefRequestHandler> GetRequestHandler() override { return this; }
    CefRefPtr<CefKeyboardHandler> GetKeyboardHandler() override { return this; }
    // Overlay: ไม่มี context menu (คลิกขวา = เมนู browser ไม่จำเป็น)
    CefRefPtr<CefContextMenuHandler> GetContextMenuHandler() override { return this; }
    void OnBeforeContextMenu(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefContextMenuParams>,
        CefRefPtr<CefMenuModel> model) override { model->Clear(); }

    bool OnPreKeyEvent(CefRefPtr<CefBrowser> browser, const CefKeyEvent& event,
        CefEventHandle, bool* is_keyboard_shortcut) override {
        if (!browser || !g_browser ||
            browser->GetIdentifier() != g_browser->GetIdentifier() ||
            event.type != KEYEVENT_RAWKEYDOWN) return false;
        if (event.windows_key_code != 'I' ||
            (event.modifiers & EVENTFLAG_CONTROL_DOWN) == 0 ||
            (event.modifiers & EVENTFLAG_SHIFT_DOWN) == 0) return false;
        if (is_keyboard_shortcut) *is_keyboard_shortcut = true;
        CefPostTask(TID_UI, new OpenDevToolsTask(browser));
        return true;
    }

    void OnAddressChange(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame,
        const CefString& url) override {
        if (!frame || !frame->IsMain()) return;
        std::string current_url = url.ToString();
        size_t hash = current_url.find('#');
        if (hash == std::string::npos) {
            Log("[route] " + current_url + " (waiting for hash route; keep current input mode)");
            return;
        }
        const bool clickthrough = current_url.substr(hash) == "#/base";
        if (g_hwnd) PostMessageW(g_hwnd, WM_OSC_ROUTE, clickthrough ? 1 : 0, 0);
        Log("[route] " + current_url + (clickthrough
            ? " (exact #/base = click-through)"
            : " (route != #/base = interactive)"));
    }

    void OnLoadingStateChange(CefRefPtr<CefBrowser> browser, bool isLoading, bool, bool) override {
        if (!isLoading) {
            Log("[cef] frame loaded");
            g_page_loaded_ms = GetTickCount64();
            // ล็อก zoom 100% ทุก load — กัน zoom เก่าที่ Chromium จำใน cache มายุ่ง layout
            ApplyGenuineZoom(true);
            // ★ FPS meter ถาวร (สเปก OWNER 12:5x): นับเฟรมที่แสดงจริง (compositor transform)
            if (g_cfg.show_fps) {
                browser->GetMainFrame()->ExecuteJavaScript(
                    "(function(){if(document.getElementById('nvfps'))return;"
                    "var d=document.createElement('div');d.id='nvfps';"
                    "d.style.cssText='position:fixed;top:0;left:0;width:600px;height:2px;background:#0f0;z-index:999999;will-change:transform;animation:nvfps 1s linear infinite;';"
                    "var st=document.createElement('style');st.textContent='@keyframes nvfps{from{transform:translateX(0)}to{transform:translateX(4000px)}}';"
                    "document.head.appendChild(st);document.body.appendChild(d);"
                    "var lb=document.createElement('div');lb.id='nvfpslb';"
                    "lb.style.cssText='position:fixed;top:0;left:0;z-index:999999;background:#000;color:#0f0;font:bold 30px monospace;padding:4px 12px;';"
                    "document.body.appendChild(lb);"
                    "var last=-1,f=0,t0=performance.now(),tl=t0;"
                    "function smp(){var m=new WebKitCSSMatrix(window.getComputedStyle(d).transform);var x=Math.round(m.m41);"
                    "if(x!==last){f++;last=x;}tl=performance.now();"
                    "if(tl-t0>=1000){lb.textContent='FPS '+Math.round(f*1000/(tl-t0));f=0;t0=tl;}"
                    "requestAnimationFrame(smp);}requestAnimationFrame(smp);})();",
                    browser->GetMainFrame()->GetURL(), 0);
            }
            // Inject CSS mods only when their file version changes.
            {
                browser->GetMainFrame()->ExecuteJavaScript(
                    "(function(){if(window.__nvMods)return;window.__nvMods=1;"
                    "function rt(){var h=(location.hash||'#/base').split('/');"
                    "var r=h[2]||'base';"
                    "if(h[3])r=r==='preferences'?(h[3]==='privacy-control'?'preferences/privacy-control':'preferences-'+h[3]):r+'-'+h[3];"
                    "document.body.setAttribute('data-nv-route', r);}"
                    "rt();addEventListener('hashchange',rt);"
                    "function find(u){var a=document.querySelectorAll('[data-nvmod]');"
                    "for(var i=0;i<a.length;i++)if(a[i].dataset.nvmod===u)return a[i];"
                    "return null;}"
                    "function load(entry){var u='/mods/'+entry.name,old=find(u);"
                    "if(old&&old.dataset.nvversion===entry.version)return;"
                    "if(entry.name.slice(-3)==='.js'){"
                    "var sc=document.createElement('script');sc.src=u+'?v='+encodeURIComponent(entry.version);"
                    "sc.dataset.nvmod=u;sc.dataset.nvversion=entry.version;"
                    "sc.onload=function(){if(old&&old.parentNode)old.parentNode.removeChild(old);};"
                    "sc.onerror=function(){if(sc.parentNode)sc.parentNode.removeChild(sc);};"
                    "document.head.appendChild(sc);return;}"
                    "var link=document.createElement('link');link.rel='stylesheet';"
                    "link.href=u+'?v='+encodeURIComponent(entry.version);"
                    "link.dataset.nvmod=u;link.dataset.nvversion=entry.version;"
                    "link.onload=function(){if(old&&old.parentNode)old.parentNode.removeChild(old);};"
                    "link.onerror=function(){if(link.parentNode)link.parentNode.removeChild(link);};"
                    "document.head.appendChild(link);}"
                    "var busy=false;"
                    "function sync(){if(busy)return;busy=true;"
                    "fetch('/mods/list',{cache:'no-store'})"
                    ".then(function(r){if(!r.ok)throw new Error('mods list '+r.status);return r.json();})"
                    ".then(function(entries){return Promise.all(entries.map(function(item){"
                    "if(typeof item!=='string')return item;"
                    "return fetch('/mods/'+item+'?__nvcheck='+Date.now(),{cache:'no-store'})"
                    ".then(function(r){if(!r.ok)throw new Error('mod '+item+' '+r.status);return r.text();})"
                    ".then(function(text){var h=2166136261;for(var i=0;i<text.length;i++)"
                    "h=Math.imul(h^text.charCodeAt(i),16777619);"
                    "return{name:item,version:'legacy-'+(h>>>0).toString(16)};});"
                    "}));}).then(function(entries){var keep={};"
                    "entries.forEach(function(e){"
                    "if(!e||typeof e.name!=='string'||typeof e.version!=='string')return;"
                    "var u='/mods/'+e.name;keep[u]=1;var old=find(u);"
                    "if(!old||old.dataset.nvversion!==e.version)load(e);});"
                    "document.querySelectorAll('[data-nvmod]').forEach(function(m){"
                    "if(!keep[m.dataset.nvmod]&&m.parentNode)m.parentNode.removeChild(m);});})"
                    ".catch(function(e){console.warn('[osc-mods] sync failed',e);})"
                    ".then(function(){busy=false;});}"
                    "sync();setInterval(sync,2000);"
                    "})();",
                    browser->GetMainFrame()->GetURL(), 0);
            }
            if (g_pending_page_action >= 0) {
                bool open = g_pending_page_action != 0;
                g_pending_page_action = -1;
                QueueOscPageAction(open);
            }
            // ★ same-origin แบบแท้ (10:5x): โหลดหน้าจาก node :59011 + ใช้ cefQuery
            //   แท้ผ่าน CEF message router (RouterEnabled=true) — ไม่ฉีด HTTP shim แล้ว
            //   (shim เก่า override window.cefQuery ทับ bridge แท้ = ต้องง้อ HTTP)
        }
    }
    void OnLoadEnd(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, int) override {
        if (frame && frame->IsMain()) {
            unsigned attempts = g_page_retry_attempts.exchange(0);
            if (attempts > 0)
                Log("[load] OSC page recovered after " + std::to_string(attempts) + " retries");
            frame->ExecuteJavaScript(
                "(function(){"
                "if(window.__nvOscKeyboardCaptureInstalled)return;"
                "window.__nvOscKeyboardCaptureInstalled=true;"
                "var last=null;"
                "function sync(target){"
                "var el=arguments.length?target:document.activeElement;"
                "var tag=el&&el.tagName?el.tagName.toUpperCase():'';"
                "var type=el&&el.type?String(el.type).toLowerCase():'text';"
                "var textInput=tag==='TEXTAREA'||tag==='SELECT'||"
                "(tag==='INPUT'&&['button','submit','reset','checkbox','radio','file','color','range','image','hidden'].indexOf(type)<0);"
                "var capture=!!(textInput||(el&&el.isContentEditable));"
                "if(typeof window.cefQuery!=='function'){last=null;return;}"
                "if(capture===last)return;last=capture;"
                "window.cefQuery({"
                "request:JSON.stringify({command:'QUERY_OSC_SET_KEYBOARD_CAPTURE',capture:capture}),"
                "onFailure:function(code,message){last=null;console.warn('[osc] keyboard capture query failed',code,message);}});"
                "}"
                "document.addEventListener('focusin',function(e){sync(e.target);},true);"
                "document.addEventListener('focusout',function(){setTimeout(function(){sync(document.activeElement);},0);},true);"
                "document.addEventListener('click',function(){sync();},true);"
                "window.addEventListener('blur',function(){sync(null);});"
                "window.addEventListener('focus',function(){sync();});"
                "sync();"
                "})();",
                frame->GetURL(), 0);
        }
    }
    void OnLoadError(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, ErrorCode code,
        const CefString& text, const CefString& url) override {
        std::string failed_url = url.ToString();
        Log("[loaderror] code=" + std::to_string((int)code) + " " + text.ToString() + " " + failed_url);
        if (!frame || !frame->IsMain() || failed_url != g_page_url) return;

        unsigned attempt = ++g_page_retry_attempts;
        int delay_ms = attempt >= 4 ? 5000 : (1000 << (attempt - 1));
        if (!CefPostDelayedTask(TID_UI,
            new RetryPageLoadTask(frame->GetBrowser(), g_page_url, attempt), delay_ms)) {
            Log("[load] failed to schedule OSC page retry");
            return;
        }
        Log("[load] OSC page retry scheduled in " + std::to_string(delay_ms) +
            "ms (attempt " + std::to_string(attempt) + ")");
    }
    void OnRenderProcessTerminated(CefRefPtr<CefBrowser> browser, CefRequestHandler::TerminationStatus status) override {
        Log("[renderer] TERMINATED status=" + std::to_string((int)status) +
            " → LoadURL page");
        // ★ state machine: renderer ตาย = หน้าไม่มีทางส่ง WIN_CLOSE เอง → บังคับปิด
        //   (กันค้าง "เปิดล่องหน": กินคลิก/โฟกัสทั้งจอทั้งที่จอไม่มีอะไร)
        g_ui_rects.clear();
        ClearNotifierRects();
        // [P1#1 Copilot] ส่ง intent — UI thread เป็นผู้เขียนสถานะ
        if (g_hwnd) PostMessageW(g_hwnd, WM_OSC_VIS, 0, 0);
        // renderer ตัวใหม่เกิดแต่ไม่โหลดซ้ำเอง — ReloadIgnoreCache ตอนยังไม่มี navigation
        // ที่ commit แล้วจะตก about:blank (เหตุการณ์ 05:31:40) → นำทางกลับ URL หน้าจริงเสมอ
        browser->GetMainFrame()->LoadURL(g_page_url);
    }
    void OnAfterCreated(CefRefPtr<CefBrowser> browser) override {
        g_browser = browser;
        Log("[browser] OnAfterCreated (renderer on the way)");
    }
    bool OnConsoleMessage(CefRefPtr<CefBrowser>, cef_log_severity_t level, const CefString& message,
        const CefString& source, int line) override {
        if (level >= LOGSEVERITY_WARNING)
            Log("[page] " + message.ToString() + " @" + source.ToString() + ":" + std::to_string(line));
        return false;
    }

    // ---- OSR ----
    void GetViewRect(CefRefPtr<CefBrowser>, CefRect& rect) override {
        static bool once = true;
        if (once) { once = false; Log("[osr] GetViewRect called"); }
        rect = CefRect(0, 0, g_view_w, g_view_h);
    }
    // ★ DSF ของแท้: rasterize ที่ device_scale_factor = fit
    //   view (CSS 1920×1200) × dsf (0.875) = buffer 1680×1050 = ขนาดจอเป๊ะ
    bool GetScreenInfo(CefRefPtr<CefBrowser>, CefScreenInfo& screen_info) override {
        screen_info.device_scale_factor = g_render_dsf;
        screen_info.rect = CefRect(0, 0, g_view_w, g_view_h);
        screen_info.available_rect = screen_info.rect;
        return true;
    }
    void OnPaint(CefRefPtr<CefBrowser>, PaintElementType type, const RectList& dirtyRects,
        const void* buffer, int width, int height) override {
        static int n = 0;
        if (n < 3) Log("[paint] type=" + std::to_string((int)type) + " " + std::to_string(width) + "x" + std::to_string(height));
        n++;
        if (type != PET_VIEW) return;
        static bool first = true;
        if (first) { first = false; Log("[paint] first VIEW frame " + std::to_string(width) + "x" + std::to_string(height)); }
        // fps meter: นับเฟรมเป็นหน้าต่าง 2 วิ (เงียบเมื่อหน้านิ่ง = ปกติของ OSR)
        {
            static int cnt = 0;
            static ULONGLONG t0 = 0;
            ULONGLONG now = GetTickCount64();
            if (!t0) t0 = now;
            cnt++;
            if (now - t0 >= 2000) {
                Log("[fps] " + std::to_string(cnt * 1000 / (int)(now - t0)) + " fps (cpu path)");
                cnt = 0; t0 = now;
            }
        }
        // แบบของแท้: composite ทุกเฟรมเสมอ — หน้าเพจวาดโปร่งใสเองเมื่อปิด OSC
        CompositeFrame(buffer, width, height, dirtyRects);
    }
    // ★ GPU path ของแท้: shared texture บน D3D11 — ไม่มีพิกเซลผ่าน CPU เลย
    //   (CEF 73: signature เก่า = handle + width/height แยกพารามิเตอร์)
    void OnAcceleratedPaint(CefRefPtr<CefBrowser>, PaintElementType type,
        const RectList& dirtyRects,
        void* shared_handle) override {
        if (type != PET_VIEW) return;
        const auto perfCallbackStartUs = OscPerfNowUs();
        g_osc_perf.viewCallbacks.fetch_add(1, std::memory_order_relaxed);
        if (dirtyRects.empty())
            g_osc_perf.emptyDirtyRects.fetch_add(1, std::memory_order_relaxed);
        const bool hookMode = g_game_focused.load(std::memory_order_acquire);
        const bool adapterOverlayVisible =
            g_ui_open_snapshot.load(std::memory_order_acquire) ||
            g_notifier_overlay_visible.load(std::memory_order_acquire);
        const ULONGLONG now = GetTickCount64();
        bool forceFullReadback = false;
        if (hookMode && adapterOverlayVisible) {
            std::lock_guard<std::mutex> lock(osc::d3d11_frame_publisher::Mutex());
            forceFullReadback = osc::d3d11_frame_publisher::ForceFullReadbackLocked();
        }
        // CEF 73 can deliver compositor-driven frames (impl-side CSS
        // opacity/transform animation) with EMPTY dirty rects. The shared
        // texture may contain a new visual frame even though no rect is dirty.
        // Desktop UI also needs compositor-driven empty-dirty frames; otherwise
        // animations can stall until the next ordinary dirty paint. Only do a
        // full-surface composite while the desktop or hook overlay is visible.
        const bool needFullFrame = adapterOverlayVisible &&
            (forceFullReadback || dirtyRects.empty());
        if (dirtyRects.empty() && !needFullFrame) {
            g_osc_perf.skippedEmptyRects.fetch_add(1, std::memory_order_relaxed);
            OscPerfFinishCallback(perfCallbackStartUs);
            return;
        }
        // Desktop callbacks can arrive faster than the configured display budget
        // (observed >120/s with maxFps=75). Keep the GPU-only path, but composite
        // only the newest available CEF texture at the requested desktop rate.
        // Hook Mode is intentionally excluded: its frame bridge has separate timing.
        if (!hookMode) {
            const auto targetFps = static_cast<unsigned long long>(
                (std::max)(1, g_cfg.max_fps));
            const auto minimumFrameIntervalUs = 1000000ULL / targetFps;
            const auto callbackNowUs = OscPerfNowUs();
            static unsigned long long lastDesktopCompositeUs = 0;
            if (lastDesktopCompositeUs != 0 &&
                callbackNowUs - lastDesktopCompositeUs < minimumFrameIntervalUs) {
                g_osc_perf.rateLimitedPaints.fetch_add(1, std::memory_order_relaxed);
                OscPerfFinishCallback(perfCallbackStartUs);
                return;
            }
            lastDesktopCompositeUs = callbackNowUs;
        }
        g_osc_perf.processedPaints.fetch_add(1, std::memory_order_relaxed);
        // เฟรม GPU วิ่งปกติ → กลับเป็น GPU_SHARED (รายงานเฉพาะตอนเปลี่ยนจริง)
        if (g_render_state.load(std::memory_order_relaxed) != OscRmGpuShared)
            SetRenderState(OscRmGpuShared, "accelerated paint frame processed");
        // [FIX-B] always-on production counters (1 Hz) to prove the overlay
        // frame rate from real logs: empty = impl-anim frames rescued.
        {
            static ULONGLONG fpsDiagT0 = 0;
            static int fpsDiagTotal = 0, fpsDiagEmpty = 0, fpsDiagDirty = 0;
            if (!fpsDiagT0) fpsDiagT0 = now;
            fpsDiagTotal++;
            if (dirtyRects.empty()) fpsDiagEmpty++; else fpsDiagDirty++;
            if (now - fpsDiagT0 >= 1000) {
                char fpsDiag[160];
                sprintf_s(fpsDiag, sizeof(fpsDiag),
                    "[fpsdiag] paints/s total=%d empty-rescued=%d dirty=%d",
                    fpsDiagTotal, fpsDiagEmpty, fpsDiagDirty);
                Log(fpsDiag);
                fpsDiagT0 = now;
                fpsDiagTotal = 0; fpsDiagEmpty = 0; fpsDiagDirty = 0;
            }
        }
        RectList effectiveDirtyRects = dirtyRects;
        if (effectiveDirtyRects.empty())
            effectiveDirtyRects.emplace_back(0, 0, g_view_w, g_view_h);
        static int calls = 0;
        if (calls++ < 3) Log("[gpu] OnAcceleratedPaint #" + std::to_string(calls) +
            " handle=0x" + std::to_string((unsigned long long)(uintptr_t)shared_handle));
        static int cnt = 0;
        static ULONGLONG t0 = 0;
        if (!t0) t0 = now;
        cnt++;
        if (now - t0 >= 2000) {
            Log("[cef-frame] callback rate " + std::to_string(cnt * 1000 / (int)(now - t0)) + " fps (GPU path; not display FPS)");
            cnt = 0; t0 = now;
        }
        UpdateDisplayGeometry(g_view_w, g_view_h);
        // Keep shared-texture access and draw submission inside CEF's callback;
        // PresentToSwap queues only the already-copied back buffer for Desktop.
        {
            std::lock_guard<std::mutex> lock(osc::d3d11_frame_publisher::Mutex());
            const auto compositeStartUs = OscPerfNowUs();
            GpuComposite((uint64_t)(uintptr_t)shared_handle, g_view_w, g_view_h,
                effectiveDirtyRects);
            OscPerfRecordComposite(OscPerfNowUs() - compositeStartUs);
            if (hookMode && adapterOverlayVisible) {
                const auto publishStartUs = OscPerfNowUs();
                osc::d3d11_frame_publisher::PublishFrameLocked(g_shared_tex, effectiveDirtyRects);
                OscPerfRecordPublish(OscPerfNowUs() - publishStartUs);
            } else if (hookMode) {
                osc::d3d11_frame_publisher::SetForceFullReadbackLocked(true);
            }
        }
        if (g_gpu_frame.load()) EnsureShownOnce();
        OscPerfFinishCallback(perfCallbackStartUs);
    }
    bool GetScreenPoint(CefRefPtr<CefBrowser>, int viewX, int viewY, int& screenX, int& screenY) override {
        // view → จอ (ผ่าผ่านตำแหน่ง/ขนาดภาพบนจอ)
        const int displayWidth = g_disp_w.load(std::memory_order_acquire);
        const int displayHeight = g_disp_h.load(std::memory_order_acquire);
        if (displayWidth > 0 && displayHeight > 0) {
            screenX = g_dst_x.load(std::memory_order_relaxed) +
                (int)((LONGLONG)viewX * displayWidth / g_view_w);
            screenY = g_dst_y.load(std::memory_order_relaxed) +
                (int)((LONGLONG)viewY * displayHeight / g_view_h);
        } else {
            screenX = viewX; screenY = viewY;
        }
        return true;
    }
    // cursor เป็นของ DisplayHandler (คืน true = เราจัดการเอง)
    void OnCursorChange(CefRefPtr<CefBrowser>, CefCursorHandle cursor, cef_cursor_type_t,
        const CefCursorInfo&) override {
        SetCursor((HCURSOR)cursor);
    }

    bool OnProcessMessageReceived(CefRefPtr<CefBrowser> browser,
        CefProcessId source_process, CefRefPtr<CefProcessMessage> message) override {
        return router_->OnProcessMessageReceived(browser, source_process, message);
    }

    static void CreateRouter() {
        CefMessageRouterConfig cfg;
        cfg.js_query_function = "cefQuery";
        cfg.js_cancel_function = "cefQueryCancel";
        router_ = CefMessageRouterBrowserSide::Create(cfg);
        router_->AddHandler(new OscQueryHandler(), false);
    }
    static CefRefPtr<CefMessageRouterBrowserSide> router_;
    IMPLEMENT_REFCOUNTING(OscClient);
};

static void CreateBrowserNow();   // นิยามด้านล่าง (หลัง class OscClient)

class CreateBrowserTask : public CefTask {
public:
    void Execute() override { CreateBrowserNow(); }
    IMPLEMENT_REFCOUNTING(CreateBrowserTask);
};

static void CreateBrowserNow() {
    int w = GetSystemMetrics(SM_CXSCREEN), h = GetSystemMetrics(SM_CYSCREEN);
    CefWindowInfo wi;
    char ow[4] = { 0 };
    if (g_cfg.windowed) {
        // ★ สเปก OWNER: CEF จัดการ render เอง (windowed child = GPU native 60fps)
        //   หน้าต่าง host คงขนาดเต็มจอตลอด — OSC เปลี่ยนเฉพาะ input/focus
        RECT rcChild = { 0, 0, w, h };
        wi.SetAsChild(g_hwnd, rcChild);
        Log("[cfg] windowed GPU render (CEF native)");
    } else {
        wi.SetAsWindowless(g_hwnd);
    }
    char nsh[4] = { 0 };
    DWORD nshlen = GetEnvironmentVariableA("OSC_NOSHARED", nsh, 4);
    const bool envNoShared = (nshlen > 0 && nsh[0] == '1');
    // สเปก 2 โหมด (GPU เป็นหลัก): renderMode ใน nvidia-osc.json = auto|gpu|cpu
    //  cpu → ปิด shared texture ขึ้น CPU path ทั้งที่ GPU พร้อม (ทดสอบ/เครื่องปัญหา)
    //  gpu → ยึด GPU แม้ gpu-off.flag เก่าจากรอบก่อน (watchdog ไม่รีสตาร์ทวนลูป)
    if (g_cfg.render_mode == 2) g_gpu_shared = false;
    else if (g_cfg.render_mode == 1 && !g_gpu_shared) {
        g_gpu_shared = true;
        Log("[mode] renderMode=gpu overrides gpu-off.flag (old fallback)");
    }
    wi.shared_texture_enabled = (g_gpu_ok && g_gpu_shared && !envNoShared) ? 1 : 0;
    Log("[cfg] shared_texture_enabled=" + std::to_string(wi.shared_texture_enabled) +
        " gpu_ok=" + std::to_string(g_gpu_ok) +
        " gpu_shared=" + std::to_string(g_gpu_shared) +
        " OSC_NOSHARED=" + std::string(nshlen > 0 ? nsh : "<unset>"));
    // รายงานโหมดตอนบูต (4.3) — state นี้คือโหมดที่ใช้จริง
    if (!g_gpu_ok) {
        SetRenderState(OscRmCpuGdi, "gpu device unavailable at boot");
    } else if (!wi.shared_texture_enabled) {
        SetRenderState(OscRmGpuUpload,
            g_cfg.render_mode == 2 ? "renderMode=cpu" :
            envNoShared ? "OSC_NOSHARED=1" : "gpu-off.flag present");
    } else {
        SetRenderState(OscRmGpuShared, "default (GPU primary)");
    }
    LogRenderMode("BOOT",
        std::string("cfg=") +
        (g_cfg.render_mode == 1 ? "gpu" : g_cfg.render_mode == 2 ? "cpu" : "auto") +
        " gpu_ok=" + std::to_string(g_gpu_ok ? 1 : 0) +
        " sharedTexture=" + std::to_string(wi.shared_texture_enabled));
    CefBrowserSettings bs;
    bs.background_color = CefColorSetARGB(0, 0, 0, 0);
    bs.windowless_frame_rate = g_cfg.max_fps;
    Log("[cfg] requested OSR frame rate=" + std::to_string(g_cfg.max_fps) +
        " fps; shared_texture=" + std::to_string(wi.shared_texture_enabled));
    // CEF 73 passes external_begin_frame_enabled as force_software_compositor
    // to ui::Compositor. Keep its internal scheduler so shared-texture OSR stays on GPU.
    wi.external_begin_frame_enabled = false;
    g_page_url = "http://localhost:59011/index.html";   // ★ same origin กับ node API/socket (แบบแท้)
    TouchFlag(L"boot-pending.flag");   // ค้าง = พังตรงนี้ → บูตหน้า sw mode
    auto created = CefBrowserHost::CreateBrowserSync(wi, new OscClient(),
        g_page_url, bs, nullptr);
    if (created) {
        ClearFlag(L"boot-pending.flag");
        created->GetHost()->WasHidden(false);
    }
    Log("=== NVIDIA OSC boot (OSR โปร่งใสค้างบนจอ, CEF 73 = engine เดียวกับ Share.exe) === CreateBrowserSync=" +
        std::string(created ? "OK" : "NULL"));
    Log("[cfg] renderScale=" + std::to_string(g_cfg.render_scale) +
        " view=" + std::to_string(g_view_w) + "x" + std::to_string(g_view_h));
}

CefRefPtr<CefMessageRouterBrowserSide> OscClient::router_ = nullptr;

// ================= input forwarding (หน้าที่ของ offscreen_window แท้) =================
static int last_click_x = 0, last_click_y = 0;

static CefMouseEvent MkMouse(LPARAM l) {
    CefMouseEvent e{};
    POINT pt{ GET_X_LPARAM(l), GET_Y_LPARAM(l) };
    // พิกัดจอ → พิกัด view ผ่านตำแหน่ง/ขนาดภาพที่วางลงจอ (รองรับ crop กลางจอ)
    const int displayWidth = g_disp_w.load(std::memory_order_acquire);
    const int displayHeight = g_disp_h.load(std::memory_order_acquire);
    if (displayWidth > 0 && displayHeight > 0) {
        const int destinationX = g_dst_x.load(std::memory_order_relaxed);
        const int destinationY = g_dst_y.load(std::memory_order_relaxed);
        int x = (int)((LONGLONG)(pt.x - destinationX) * g_view_w / displayWidth);
        int y = (int)((LONGLONG)(pt.y - destinationY) * g_view_h / displayHeight);
        e.x = x < 0 ? 0 : (x >= g_view_w ? g_view_w - 1 : x);
        e.y = y < 0 ? 0 : (y >= g_view_h ? g_view_h - 1 : y);
    } else {
        e.x = pt.x; e.y = pt.y;
    }
    if (GetKeyState(VK_SHIFT) < 0)   e.modifiers |= EVENTFLAG_SHIFT_DOWN;
    if (GetKeyState(VK_CONTROL) < 0) e.modifiers |= EVENTFLAG_CONTROL_DOWN;
    if (GetKeyState(VK_MENU) < 0)    e.modifiers |= EVENTFLAG_ALT_DOWN;
    last_click_x = pt.x; last_click_y = pt.y;
    return e;
}

// The exact #/base route passes input through; all other routes capture input.
static bool GetHookGameClientBounds(RECT& bounds);
static CefMouseEvent MkMousePt(POINT s) {
    CefMouseEvent e{};
    RECT gameClient{};
    if (GetHookGameClientBounds(gameClient)) {
        const int clientWidth = gameClient.right - gameClient.left;
        const int clientHeight = gameClient.bottom - gameClient.top;
        const int x = static_cast<int>(
            static_cast<LONGLONG>(s.x - gameClient.left) * g_view_w / clientWidth);
        const int y = static_cast<int>(
            static_cast<LONGLONG>(s.y - gameClient.top) * g_view_h / clientHeight);
        e.x = x < 0 ? 0 : (x >= g_view_w ? g_view_w - 1 : x);
        e.y = y < 0 ? 0 : (y >= g_view_h ? g_view_h - 1 : y);
    } else {
        const int displayWidth = g_disp_w.load(std::memory_order_acquire);
        const int displayHeight = g_disp_h.load(std::memory_order_acquire);
        if (displayWidth > 0 && displayHeight > 0) {
            const int destinationX = g_dst_x.load(std::memory_order_relaxed);
            const int destinationY = g_dst_y.load(std::memory_order_relaxed);
            int x = (int)((LONGLONG)(s.x - destinationX) * g_view_w / displayWidth);
            int y = (int)((LONGLONG)(s.y - destinationY) * g_view_h / displayHeight);
            e.x = x < 0 ? 0 : (x >= g_view_w ? g_view_w - 1 : x);
            e.y = y < 0 ? 0 : (y >= g_view_h ? g_view_h - 1 : y);
        } else { e.x = s.x; e.y = s.y; }
    }
    if (GetKeyState(VK_SHIFT) < 0)   e.modifiers |= EVENTFLAG_SHIFT_DOWN;
    if (GetKeyState(VK_CONTROL) < 0) e.modifiers |= EVENTFLAG_CONTROL_DOWN;
    if (GetKeyState(VK_MENU) < 0)    e.modifiers |= EVENTFLAG_ALT_DOWN;
    return e;
}
struct HookGameWindowSearch {
    DWORD gamePid = 0;
    HWND bestWindow = nullptr;
    RECT clientBounds{};
    LONGLONG bestArea = 0;
};

static BOOL CALLBACK FindHookGameWindow(HWND hwnd, LPARAM parameter) {
    auto* search = reinterpret_cast<HookGameWindowSearch*>(parameter);
    if (!search || !IsWindowVisible(hwnd) || IsIconic(hwnd) || GetWindow(hwnd, GW_OWNER))
        return TRUE;

    DWORD windowPid = 0;
    GetWindowThreadProcessId(hwnd, &windowPid);
    if (windowPid != search->gamePid)
        return TRUE;

    RECT client{};
    if (!GetClientRect(hwnd, &client) || client.right <= client.left ||
        client.bottom <= client.top)
        return TRUE;

    POINT topLeft{ client.left, client.top };
    POINT bottomRight{ client.right, client.bottom };
    if (!ClientToScreen(hwnd, &topLeft) || !ClientToScreen(hwnd, &bottomRight))
        return TRUE;

    const LONGLONG width = bottomRight.x - topLeft.x;
    const LONGLONG height = bottomRight.y - topLeft.y;
    const LONGLONG area = width * height;
    if (width <= 0 || height <= 0 || area <= search->bestArea)
        return TRUE;

    search->bestWindow = hwnd;
    search->clientBounds = RECT{ topLeft.x, topLeft.y, bottomRight.x, bottomRight.y };
    search->bestArea = area;
    return TRUE;
}

// In hooked-game mode, the global mouse hook must use the game's client area,
// not the monitor-sized desktop OSC HWND. The published adapter PID identifies
// the actual D3D consumer, so this also follows the active game window in windowed mode.
static bool GetHookGameClientBounds(RECT& bounds) {
    if (!g_game_focused.load(std::memory_order_acquire))
        return false;

    const auto* info = osc::d3d11_frame_publisher::GetConsumerAdapterInfo();
    if (!info || info->magic != osc_frame_bridge::ConsumerAdapterMagic ||
        info->version != osc_frame_bridge::ConsumerAdapterVersion ||
        info->ownerProcessId <= 0)
        return false;

    const DWORD gamePid = static_cast<DWORD>(info->ownerProcessId);
    static std::atomic<DWORD> cachedPid{ 0 };
    static std::atomic<HWND> cachedWindow{ nullptr };

    HWND cached = cachedPid.load(std::memory_order_acquire) == gamePid
        ? cachedWindow.load(std::memory_order_acquire) : nullptr;
    if (cached && IsWindow(cached) && IsWindowVisible(cached) && !IsIconic(cached)) {
        DWORD windowPid = 0;
        GetWindowThreadProcessId(cached, &windowPid);
        RECT client{};
        if (windowPid == gamePid && GetClientRect(cached, &client) &&
            client.right > client.left && client.bottom > client.top) {
            POINT topLeft{ client.left, client.top };
            POINT bottomRight{ client.right, client.bottom };
            if (ClientToScreen(cached, &topLeft) && ClientToScreen(cached, &bottomRight)) {
                bounds = RECT{ topLeft.x, topLeft.y, bottomRight.x, bottomRight.y };
                if (bounds.right > bounds.left && bounds.bottom > bounds.top)
                    return true;
            }
        }
    }

    HookGameWindowSearch search{};
    search.gamePid = gamePid;
    EnumWindows(FindHookGameWindow, reinterpret_cast<LPARAM>(&search));
    if (!search.bestWindow)
        return false;

    cachedPid.store(gamePid, std::memory_order_release);
    cachedWindow.store(search.bestWindow, std::memory_order_release);
    bounds = search.clientBounds;
    return bounds.right > bounds.left && bounds.bottom > bounds.top;
}

static bool MapDisplayRectToGameClient(const RECT& displayRect,
    const RECT& gameClient, RECT& mapped) {
    const int displayWidth = g_disp_w.load(std::memory_order_acquire);
    const int displayHeight = g_disp_h.load(std::memory_order_acquire);
    if (displayWidth <= 0 || displayHeight <= 0)
        return false;

    const int destinationX = g_dst_x.load(std::memory_order_relaxed);
    const int destinationY = g_dst_y.load(std::memory_order_relaxed);
    const LONGLONG clientWidth = gameClient.right - gameClient.left;
    const LONGLONG clientHeight = gameClient.bottom - gameClient.top;

    mapped.left = gameClient.left + static_cast<LONG>(
        static_cast<LONGLONG>(displayRect.left - destinationX) * clientWidth / displayWidth);
    mapped.top = gameClient.top + static_cast<LONG>(
        static_cast<LONGLONG>(displayRect.top - destinationY) * clientHeight / displayHeight);
    mapped.right = gameClient.left + static_cast<LONG>(
        static_cast<LONGLONG>(displayRect.right - destinationX) * clientWidth / displayWidth);
    mapped.bottom = gameClient.top + static_cast<LONG>(
        static_cast<LONGLONG>(displayRect.bottom - destinationY) * clientHeight / displayHeight);
    return mapped.right > mapped.left && mapped.bottom > mapped.top;
}

static bool ScreenInUiRects(POINT s) {
    const bool clickthrough = IsOverlayInputClickThrough();
    if (g_game_focused.load(std::memory_order_acquire)) {
        RECT gameClient{};
        if (!GetHookGameClientBounds(gameClient) || !PtInRect(&gameClient, s))
            return false;

        if (!clickthrough) {
            if (!g_ui_open_snapshot.load(std::memory_order_acquire))
                return false;
            std::lock_guard<std::mutex> lock(g_ui_rects_mutex);
            // Main OSC routes currently publish an empty rect list: capture is
            // still limited to the actual game client, never the whole monitor.
            if (g_ui_rects.empty())
                return true;
            return std::any_of(g_ui_rects.begin(), g_ui_rects.end(),
                [&](const RECT& rect) {
                    RECT mapped{};
                    return MapDisplayRectToGameClient(rect, gameClient, mapped) &&
                        PtInRect(&mapped, s) != FALSE;
                });
        }

        std::lock_guard<std::mutex> lock(g_notifier_rects_mutex);
        return std::any_of(g_notifier_rects.begin(), g_notifier_rects.end(),
            [&](const RECT& rect) {
                RECT mapped{};
                return MapDisplayRectToGameClient(rect, gameClient, mapped) &&
                    PtInRect(&mapped, s) != FALSE;
            });
    }

    if (!clickthrough) {
        if (!g_ui_open_snapshot.load(std::memory_order_acquire) || !g_hwnd)
            return false;
        RECT windowRect{};
        return GetWindowRect(g_hwnd, &windowRect) &&
            PtInRect(&windowRect, s) != FALSE;
    }

    const int displayWidth = g_disp_w.load(std::memory_order_acquire);
    const int displayHeight = g_disp_h.load(std::memory_order_acquire);
    if (displayWidth <= 0 || displayHeight <= 0) return false;
    const int destinationX = g_dst_x.load(std::memory_order_relaxed);
    const int destinationY = g_dst_y.load(std::memory_order_relaxed);
    RECT overlay_rect{
        destinationX - 1,
        destinationY - 1,
        destinationX + (std::max)(1, displayWidth) + 1,
        destinationY + displayHeight + 1
    };
    if (!PtInRect(&overlay_rect, s)) return false;

    std::lock_guard<std::mutex> lock(g_notifier_rects_mutex);
    return std::any_of(g_notifier_rects.begin(), g_notifier_rects.end(),
        [&](const RECT& rect) { return PtInRect(&rect, s) != FALSE; });
}
static HHOOK g_mouse_hook = nullptr;
static HHOOK g_keyboard_hook = nullptr;
static bool g_swallowed_keyboard_keys[256]{};
// ★ บทเรียน 06:36 — hook ต้องอยู่เธรดเฉพาะ (มี pump ของตัวเอง) เพราะ LL hook
//   อุดตันทั้งระบบเมื่อเธรดเจ้าของไม่ปั๊มข้อความ (CEF UI thread ตันตอน CreateBrowserSync)
//   event ที่โดน UI จะส่งเข้า CEF ผ่าน post-task ไม่เรียกตรง
struct HookEv { int kind; int data; int x; int y; };  // kind = WM_*
// dispatch บน UI thread (ผ่าน CefTask — CefPostTask รุ่นนี้ไม่รับ lambda)
class HookEventTask : public CefTask {
public:
    HookEv ev{};
    void Execute() override {
        if (!g_browser || !g_browser->GetHost()) return;
        POINT pt{ ev.x, ev.y };
        CefMouseEvent e = MkMousePt(pt);
        auto host = g_browser->GetHost();
        switch (ev.kind) {
        case WM_MOUSEMOVE:    host->SendMouseMoveEvent(e, false); break;
        case WM_LBUTTONDOWN:
            host->SetFocus(true);
            host->SendMouseClickEvent(e, MBT_LEFT, false, 1);
            break;
        case WM_LBUTTONUP:    host->SendMouseClickEvent(e, MBT_LEFT, true, 1); break;
        case WM_RBUTTONDOWN:  host->SendMouseClickEvent(e, MBT_RIGHT, false, 1); break;
        case WM_RBUTTONUP:    host->SendMouseClickEvent(e, MBT_RIGHT, true, 1); break;
        case WM_MBUTTONDOWN:  host->SendMouseClickEvent(e, MBT_MIDDLE, false, 1); break;
        case WM_MBUTTONUP:    host->SendMouseClickEvent(e, MBT_MIDDLE, true, 1); break;
        case WM_MOUSEWHEEL:   host->SendMouseWheelEvent(e, 0, ev.data); break;
        }
    }
    IMPLEMENT_REFCOUNTING(HookEventTask);
};
static LRESULT CALLBACK LowLevelMouseProc(int code, WPARAM w, LPARAM l) {
    static bool capturedLeft = false;
    static bool capturedRight = false;
    static bool capturedMiddle = false;
    if (code == HC_ACTION && g_browser) {
        MSLLHOOKSTRUCT* m = (MSLLHOOKSTRUCT*)l;
        const bool overUi = ScreenInUiRects(m->pt);
        if (!overUi &&
            (w == WM_LBUTTONDOWN || w == WM_RBUTTONDOWN || w == WM_MBUTTONDOWN))
            g_overlay_keyboard_capture.store(false, std::memory_order_release);
        // ★ เมาส์ต้องขยับได้เสมอ: WM_MOUSEMOVE ห้ามกืน (return 1 = เคอร์เซอร์แข็ง)
        if (w == WM_MOUSEMOVE) {
            if (overUi) {
                static ULONGLONG last_mv = 0;
                ULONGLONG now = GetTickCount64();
                if (now - last_mv >= 16) {           // throttle 60/s
                    last_mv = now;
                    HookEv ev{ (int)w, 0, m->pt.x, m->pt.y };
                    auto t = new HookEventTask();
                    t->ev = ev;
                    CefPostTask(TID_UI, t);
                }
            }
            return CallNextHookEx(nullptr, code, w, l);   // ผ่านเสมอ
        }
        bool* capturedButton = nullptr;
        bool isButtonDown = false;
        switch (w) {
        case WM_LBUTTONDOWN: capturedButton = &capturedLeft; isButtonDown = true; break;
        case WM_LBUTTONUP: capturedButton = &capturedLeft; break;
        case WM_RBUTTONDOWN: capturedButton = &capturedRight; isButtonDown = true; break;
        case WM_RBUTTONUP: capturedButton = &capturedRight; break;
        case WM_MBUTTONDOWN: capturedButton = &capturedMiddle; isButtonDown = true; break;
        case WM_MBUTTONUP: capturedButton = &capturedMiddle; break;
        }
        const bool shouldCapture = overUi ||
            (capturedButton && !isButtonDown && *capturedButton);
        // Keep the matching button-up paired when a drag leaves the overlay.
        if (shouldCapture) {
            HookEv ev{ (int)w, 0, m->pt.x, m->pt.y };
            if (w == WM_MOUSEWHEEL) ev.data = GET_WHEEL_DELTA_WPARAM(m->mouseData);
            auto t = new HookEventTask();
            t->ev = ev;
            if (!CefPostTask(TID_UI, t)) {
                if (capturedButton && isButtonDown) *capturedButton = false;
                Log("[input] failed to queue captured mouse event to CEF");
                return CallNextHookEx(nullptr, code, w, l);
            }
            if (capturedButton)
                *capturedButton = isButtonDown;
            static bool logged_game_capture = false;
            if (!logged_game_capture &&
                g_game_focused.load(std::memory_order_acquire)) {
                logged_game_capture = true;
                Log("[input] first in-game mouse event forwarded to CEF");
            }
            return 1;   // กลืนคลิก — ไม่ให้ตกลงเกม
        }
    }
    return CallNextHookEx(nullptr, code, w, l);
}
static LRESULT CALLBACK LowLevelKeyboardProc(int code, WPARAM w, LPARAM l);
static DWORD WINAPI HookThreadProc(LPVOID) {
    g_mouse_hook = SetWindowsHookExW(WH_MOUSE_LL, LowLevelMouseProc, GetModuleHandleW(nullptr), 0);
    if (!g_mouse_hook) {
        Log("[ui] ERROR: low-level mouse hook installation failed err=" + std::to_string(GetLastError()));
        return 1;
    }
    g_keyboard_hook = SetWindowsHookExW(
        WH_KEYBOARD_LL, LowLevelKeyboardProc, GetModuleHandleW(nullptr), 0);
    if (!g_keyboard_hook)
        Log("[input] keyboard capture unavailable err=" + std::to_string(GetLastError()));
    else
        Log("[input] low-level keyboard hook installed (overlay-click scoped)");
    Log("[ui] low-level mouse hook installed (dedicated thread)");
    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0) > 0) { TranslateMessage(&m); DispatchMessageW(&m); }
    if (g_keyboard_hook) {
        UnhookWindowsHookEx(g_keyboard_hook);
        g_keyboard_hook = nullptr;
    }
    if (g_mouse_hook) {
        UnhookWindowsHookEx(g_mouse_hook);
        g_mouse_hook = nullptr;
    }
    return 0;
}

static void ForwardKey(UINT msg, WPARAM w, LPARAM l) {
    if (!g_browser) return;
    CefKeyEvent e{};
    e.windows_key_code = (int)w;
    e.native_key_code = (int)l;
    e.is_system_key = (GetKeyState(VK_MENU) < 0);
    if (GetKeyState(VK_SHIFT) < 0)   e.modifiers |= EVENTFLAG_SHIFT_DOWN;
    if (GetKeyState(VK_CONTROL) < 0) e.modifiers |= EVENTFLAG_CONTROL_DOWN;
    if (GetKeyState(VK_MENU) < 0)    e.modifiers |= EVENTFLAG_ALT_DOWN;
    if (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) {
        e.type = KEYEVENT_RAWKEYDOWN;
        g_browser->GetHost()->SendKeyEvent(e);
        // ตัวอักษร (ข้อความ) ส่ง WM_CHAR แยก
    } else if (msg == WM_KEYUP || msg == WM_SYSKEYUP) {
        e.type = KEYEVENT_KEYUP;
        g_browser->GetHost()->SendKeyEvent(e);
    } else if (msg == WM_CHAR) {
        e.type = KEYEVENT_CHAR;
        e.character = (char16_t)w;
        e.unmodified_character = (char16_t)w;
        g_browser->GetHost()->SendKeyEvent(e);
    }
}

class HookKeyboardEventTask : public CefTask {
public:
    UINT message = 0;
    WPARAM key = 0;
    LPARAM keyData = 0;
    void Execute() override {
        if (g_browser && g_browser->GetHost())
            ForwardKey(message, key, keyData);
    }
    IMPLEMENT_REFCOUNTING(HookKeyboardEventTask);
};

static LRESULT CALLBACK LowLevelKeyboardProc(int code, WPARAM w, LPARAM l) {
    if (code == HC_ACTION && g_browser) {
        const auto* key = reinterpret_cast<const KBDLLHOOKSTRUCT*>(l);
        const bool keyDown = w == WM_KEYDOWN || w == WM_SYSKEYDOWN;
        const bool keyUp = w == WM_KEYUP || w == WM_SYSKEYUP;
        if ((keyDown || keyUp) && key->vkCode < std::size(g_swallowed_keyboard_keys)) {
            const bool hookActive =
                g_game_focused.load(std::memory_order_acquire) &&
                g_ui_open_snapshot.load(std::memory_order_acquire);
            const bool wasSwallowed = keyUp && g_swallowed_keyboard_keys[key->vkCode];
            if (!hookActive && !wasSwallowed)
                return CallNextHookEx(nullptr, code, w, l);

            const bool capture =
                g_overlay_keyboard_capture.load(std::memory_order_acquire);
            const bool swallow = keyDown ? hookActive && capture : wasSwallowed;
            if (!swallow) {
                if (keyDown)
                    g_swallowed_keyboard_keys[key->vkCode] = false;
                return CallNextHookEx(nullptr, code, w, l);
            }

            const bool altDown = (GetAsyncKeyState(VK_MENU) & 0x8000) != 0;
            const bool protectedShortcut =
                key->vkCode == VK_LWIN || key->vkCode == VK_RWIN ||
                (altDown && (key->vkCode == VK_TAB || key->vkCode == VK_F4));
            if (protectedShortcut) {
                if (keyDown) g_swallowed_keyboard_keys[key->vkCode] = false;
                return CallNextHookEx(nullptr, code, w, l);
            }

            LPARAM keyData = static_cast<LPARAM>(key->scanCode << 16);
            if (key->flags & LLKHF_EXTENDED) keyData |= (1LL << 24);
            if (keyUp) keyData |= (1LL << 30) | (1LL << 31);
            auto* task = new HookKeyboardEventTask();
            task->message = static_cast<UINT>(w);
            task->key = key->vkCode;
            task->keyData = keyData;
            CefPostTask(TID_UI, task);

            g_swallowed_keyboard_keys[key->vkCode] = keyDown && swallow;
            if (keyDown && key->vkCode != VK_SHIFT &&
                key->vkCode != VK_CONTROL && key->vkCode != VK_MENU) {
                BYTE keyboardState[256]{};
                if (GetKeyboardState(keyboardState)) {
                    WCHAR characters[4]{};
                    const int count = ToUnicodeEx(
                        key->vkCode, key->scanCode, keyboardState,
                        characters, static_cast<int>(std::size(characters)), 0,
                        GetKeyboardLayout(0));
                    for (int i = 0; i < count; ++i) {
                        auto* characterTask = new HookKeyboardEventTask();
                        characterTask->message = WM_CHAR;
                        characterTask->key = characters[i];
                        characterTask->keyData = keyData;
                        CefPostTask(TID_UI, characterTask);
                    }
                }
            }
            if (swallow)
                return 1;
        }
    }
    return CallNextHookEx(nullptr, code, w, l);
}

// ★ ผูก DComp target/visual กับหน้าต่าง "หลังโชว์" — DWM จะจับ visual tree ถูกต้อง
static void BindDcompTarget() {
    // ★ [P1#2 Copilot] target ต้องมีเจ้าของตลอดอายุที่แสดงผล — เก็บใน g_dcomp_target
    //   (เดิม Release หลัง Commit = อ้างอิงค้าง และเรียกซ้ำจะ CreateTargetForHwnd FAILED)
    if (g_dcomp_bound || !g_dcomp_dev || !g_dxgi_dev2 || !g_hwnd) return;
    IDCompositionTarget* target = nullptr;
    IDCompositionVisual* vis = nullptr;
    if (FAILED(g_dcomp_dev->CreateTargetForHwnd(g_hwnd, TRUE, &target))) { Log("[gpu] BindDcompTarget: target FAILED"); return; }
    if (FAILED(g_dcomp_dev->CreateVisual(&vis))) { target->Release(); Log("[gpu] BindDcompTarget: visual FAILED"); return; }
    if (FAILED(vis->SetContent(g_gpu_swap))) { vis->Release(); target->Release(); Log("[gpu] BindDcompTarget: SetContent FAILED"); return; }
    if (FAILED(target->SetRoot(vis))) { vis->Release(); target->Release(); Log("[gpu] BindDcompTarget: SetRoot FAILED"); return; }
    if (FAILED(g_dcomp_dev->Commit())) { vis->Release(); target->Release(); Log("[gpu] BindDcompTarget: Commit FAILED"); return; }
    vis->Release();
    g_dcomp_target = target;          // เจ้าของตลอดอายุ — teardown จริงค่อยปล่อย
    g_dcomp_bound = true;
    Log("[gpu] dcomp target BOUND after show");
}

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    if (msg == WM_OSC_GPU_FRAME_READY) {
        if (g_gpu_frame.load(std::memory_order_acquire)) EnsureShownOnce();
        return 0;
    }
    if (msg == WM_OSC_FOCUS_MODE) {
        const bool hookMode = w != FALSE;
        g_game_focused.store(hookMode, std::memory_order_release);
        g_overlay_keyboard_capture.store(false, std::memory_order_release);
        ApplyOpenShareInputMode();
        if (hookMode) {
            const bool overlayVisible =
                g_ui_open_snapshot.load(std::memory_order_acquire) ||
                g_notifier_overlay_visible.load(std::memory_order_acquire);
            bool retainedFrameQueued = false;
            {
                std::lock_guard<std::mutex> lock(osc::d3d11_frame_publisher::Mutex());
                osc::d3d11_frame_publisher::SetForceFullReadbackLocked(true);
                if (overlayVisible && g_shared_tex) {
                    const std::vector<CefRect> fullFrame{
                        CefRect(0, 0, g_view_w, g_view_h) };
                    osc::d3d11_frame_publisher::PublishFrameLocked(g_shared_tex, fullFrame);
                    retainedFrameQueued = true;
                }
            }
            if (retainedFrameQueued) {
                Log("[adapter-bridge] queued retained CEF frame on game focus");
            } else {
                Log(std::string("[adapter-bridge] no visible page overlay on game focus; menu=") +
                    (g_ui_open_snapshot.load(std::memory_order_acquire) ? "1" : "0") +
                    " notifier=" +
                    (g_notifier_overlay_visible.load(std::memory_order_acquire)
                        ? "1" : "0"));
            }
        } else {
            // Desktop OSC must never leave an old in-game frame visible.
            // Publish an explicit hidden frame so every consumer drops its cache.
            std::lock_guard<std::mutex> lock(osc::d3d11_frame_publisher::Mutex());
            osc::d3d11_frame_publisher::PublishMetadataLocked(true);
        }
        StartAdapterFrameBurst("OSC focus mode changed", true);
        if (g_browser) {
            g_browser->GetHost()->WasHidden(false);
            g_browser->GetHost()->Invalidate(PET_VIEW);
        }
        if (hookMode) {
            ShowWindow(h, SW_HIDE);
            if (g_browser) g_browser->GetHost()->SetFocus(false);
        } else if (g_ui_open) {
            ShowWindow(h, SW_SHOWNOACTIVATE);
        }
        Log(std::string("[focus-mode] ") +
            (hookMode ? "hook active; desktop OSC hidden" :
                "desktop OSC active; hook frame publishing paused"));
        return 0;
    }
    if ((msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN) &&
        w == VK_F12 && (l & (1LL << 30)) == 0 && g_browser) {
        if (!CefPostTask(TID_UI, new OpenDevToolsTask(g_browser)))
            Log("[devtools] failed to post F12 toggle to CEF UI thread");
        return 0;
    }
    if (g_browser && !IsOverlayInputClickThrough()) {
        auto host = g_browser->GetHost();
        switch (msg) {
        case WM_MOUSEMOVE: {
            static bool logged_mouse = false;
            if (!logged_mouse) { logged_mouse = true; Log("[input] first WM_MOUSEMOVE forwarded to CEF"); }
            auto e = MkMouse(l); host->SendMouseMoveEvent(e, false); return 0;
        }
        case WM_MOUSELEAVE: { CefMouseEvent e{}; e.x = -1; e.y = -1; host->SendMouseMoveEvent(e, true); return 0; }
        case WM_LBUTTONDOWN: {
            static bool logged_click = false;
            if (!logged_click) { logged_click = true; Log("[input] first WM_LBUTTONDOWN forwarded to CEF"); }
            auto e = MkMouse(l); g_browser->GetHost()->SetFocus(true); host->SendMouseClickEvent(e, MBT_LEFT, false, 1); SetCapture(h); return 0;
        }
        case WM_LBUTTONUP:   { auto e = MkMouse(l); host->SendMouseClickEvent(e, MBT_LEFT, true, 1); ReleaseCapture(); return 0; }
        case WM_RBUTTONDOWN: { auto e = MkMouse(l); host->SendMouseClickEvent(e, MBT_RIGHT, false, 1); return 0; }
        case WM_RBUTTONUP:   { auto e = MkMouse(l); host->SendMouseClickEvent(e, MBT_RIGHT, true, 1); return 0; }
        case WM_MBUTTONDOWN: { auto e = MkMouse(l); host->SendMouseClickEvent(e, MBT_MIDDLE, false, 1); return 0; }
        case WM_MBUTTONUP:   { auto e = MkMouse(l); host->SendMouseClickEvent(e, MBT_MIDDLE, true, 1); return 0; }
        case WM_MOUSEWHEEL: {
            // Overlay แท้: ปิด Ctrl+ลูกเมาส์ (zoom = ของ host จัดการเองด้วยสูตรแท้ ห้ามใครขยับ)
            if (LOWORD(w) & MK_CONTROL) return 0;
            auto e = MkMouse(l);
            int d = GET_WHEEL_DELTA_WPARAM(w);
            host->SendMouseWheelEvent(e, 0, d);
            return 0;
        }
        case WM_KEYDOWN: case WM_SYSKEYDOWN: {
            static bool logged_key = false;
            if (!logged_key) { logged_key = true; Log("[input] first WM_KEYDOWN forwarded to CEF"); }
            ForwardKey(msg, w, l); return 0;
        }
        case WM_KEYUP: case WM_SYSKEYUP:
            ForwardKey(msg, w, l); return 0;
        case WM_CHAR:
            ForwardKey(msg, w, l); return 0;
        }
    }
    switch (msg) {
    case WM_OSC_ROUTE: {
        const bool clickthrough = w != 0;
        if (!clickthrough) ClearNotifierRects();
        if (g_route_clickthrough != clickthrough) {
            g_route_clickthrough = clickthrough;
            Log(std::string("[route] input mode -> ") +
                (clickthrough ? "click-through (#/base)" : "interactive"));
        }
        // [FIX-ROUTE-SYNC] The page owns its visibility: the genuine UI
        // auto-closes on inactivity (Hook Mode) by navigating back to
        // #/base, and boots by resolving to a real route when it opens.
        // If g_ui_open disagrees with the route, every page boot sees the
        // stale "open" state and re-opens — base <-> main-menu ping-pong
        // with input styles flapping (clicks fall through mid-interaction).
        // Follow the page: #/base closes, a real route opens.
        if (clickthrough && g_ui_open) {
            Log("[route] page navigated to #/base while open -> following close");
            PostMessageW(g_hwnd, WM_OSC_VIS, 0, 0);
            return 0;
        }
        if (!clickthrough && !g_ui_open) {
            Log("[route] page navigated off #/base while closed -> following open");
            PostMessageW(g_hwnd, WM_OSC_VIS, 1, 0);
            return 0;
        }
        ApplyOpenShareInputMode();
        const bool inputClickthrough = IsOverlayInputClickThrough();
        if (g_ui_open && !inputClickthrough &&
            !g_game_focused.load(std::memory_order_acquire)) {
            SetTimer(g_hwnd, 2, 500, nullptr);
            ShowWindow(g_hwnd, SW_SHOW);
            g_osc_activation_attempts = 10;
            if (SetForegroundWithInput(g_hwnd)) {
                g_osc_activation_attempts = 0;
            } else {
                Log("[focus] activation pending after interactive route change");
            }
        } else if (inputClickthrough) {
            KillTimer(g_hwnd, 2);
            g_osc_activation_attempts = 0;
        }
        return 0;
    }
    case WM_OSC_PICKER_MODE: {
        if (g_ui_open) return TRUE;
        HWND insert_after = w ? HWND_NOTOPMOST : HWND_TOPMOST;
        return SetWindowPos(g_hwnd, insert_after, 0, 0, 0, 0,
            SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE) ? TRUE : FALSE;
    }
    case WM_OSC_VIS: {
        // ★ [P1#1 Copilot] UI thread เป็นเจ้าของ g_ui_open ผู้เดียว — HTTP/หน้า/ผู้ตาย
        //   ส่ง "intent" มาเท่านั้น: w=0 close · w=1 open · w=2 toggle
        int intent = (int)w;
        bool was = g_ui_open;
        bool ns = (intent == 1) ? true : (intent == 0) ? false : !was;
        bool external_request = (l != 0);
        if (ns == was) {
            if (external_request) QueueOscPageAction(ns);
            if (!ns && g_browser &&
                !CefPostTask(TID_UI, new InvalidatePageTask(g_browser)))
                Log("[gpu] failed to schedule CEF repaint after OSC close");
            Log("[state] no-op (already " + std::string(ns ? "OPEN" : "CLOSED") + ") — ข้าม stale intent");
            return 0;
        }
        g_ui_open = ns;
        g_ui_open_snapshot.store(ns);
        StartAdapterFrameBurst("OSC visibility changed");
        if (!ns)
            g_overlay_keyboard_capture.store(false, std::memory_order_release);
        if (!ns && g_game_focused.load(std::memory_order_acquire))
            osc::d3d11_frame_publisher::PublishMetadata();

        // When the game is already fullscreen and foreground, do not let an
        // opening OSC request steal focus during the stable-gate transition.
        // The normal timer will enter Hook Mode after its 200 ms confirmation.
        const bool fullscreenHookCandidate = ns &&
            !g_game_focused.load(std::memory_order_acquire) &&
            IsHookGameForeground();
        if (fullscreenHookCandidate)
            Log("[focus] preserving fullscreen game foreground while Hook Mode stabilizes");

        if (ns) {
            if (g_browser) {
                auto host = g_browser->GetHost();
                host->WasHidden(false);
                host->Invalidate(PET_VIEW);
                Log("[gpu] CEF view awakened and invalidated on OSC open");
            }
            if (!was) {
                KillTimer(g_hwnd, 3);
                g_prev_fore = GetForegroundWindow();
                if (g_prev_fore == g_hwnd) g_prev_fore = nullptr;
                g_foreground_restore_attempts = 0;
                g_osc_activation_attempts = 0;
            }
            if (!IsOverlayInputClickThrough() && !SetFullOverlayRegion())
                Log("[window] OSC opened but full hit-test region could not be restored");
            // OSC ขึ้นจอหลักเสมอ (primary) + ขยายเต็ม
            POINT zero{ 0, 0 };
            HMONITOR mon = MonitorFromPoint(zero, MONITOR_DEFAULTTOPRIMARY);
            MONITORINFO mi{ sizeof(mi) };
            if (mon && GetMonitorInfoW(mon, &mi)) {
                SetWindowPos(g_hwnd, HWND_TOPMOST,
                    mi.rcMonitor.left, mi.rcMonitor.top,
                    NotificationSafeOverlayWidth(mi.rcMonitor.right - mi.rcMonitor.left),
                    mi.rcMonitor.bottom - mi.rcMonitor.top,
                    SWP_NOACTIVATE);
            }
            if (!g_game_focused.load(std::memory_order_acquire) &&
                !fullscreenHookCandidate)
                ShowWindow(g_hwnd, IsOverlayInputClickThrough()
                    ? SW_SHOWNOACTIVATE : SW_SHOW);
            ApplyOpenShareInputMode();
            if (!IsOverlayInputClickThrough() &&
                !g_game_focused.load(std::memory_order_acquire) &&
                !fullscreenHookCandidate) {
                SetTimer(g_hwnd, 2, 500, nullptr);
                g_osc_activation_attempts = 10;
                if (SetForegroundWithInput(g_hwnd)) {
                    g_osc_activation_attempts = 0;
                } else {
                    Log("[focus] activation pending after OSC open");
                }
            } else {
                KillTimer(g_hwnd, 2);
                g_osc_activation_attempts = 0;
            }
            // ★ [P1#2 Copilot] OSR เท่านั้น — windowed ห้ามแตะ DComp · bind ครั้งเดียว
            //   (target เก็บใน g_dcomp_target ตลอดอายุ) — เปิดซ้ำแค่ Commit ให้ DWM รับซ้ำ
            if (!g_cfg.windowed && g_dcomp_dev) {
                if (!g_dcomp_bound) BindDcompTarget();
                else g_dcomp_dev->Commit();
            }
            if (g_clickthrough_hwnd && g_browser)
                g_browser->GetHost()->Invalidate(PET_VIEW);
        } else {
            ApplyOpenShareInputMode();
            g_osc_activation_attempts = 0;
            // Close CEF focus first, then return activation to the window captured at open.
            if (g_browser) g_browser->GetHost()->SetFocus(false);
            if (!RestorePreviousForeground()) {
                g_foreground_restore_attempts = 0;
                SetTimer(g_hwnd, 3, 50, nullptr);
                Log("[focus] previous foreground restore pending");
            }
            KillTimer(g_hwnd, 2);
        }
        if (external_request) QueueOscPageAction(ns);
        if (!ns && g_browser &&
            !CefPostTask(TID_UI, new InvalidatePageTask(g_browser)))
            Log("[gpu] failed to schedule CEF repaint after OSC close");
        Log("[state] " + std::string(ns ? "OPEN" : "CLOSED") + " (was " + std::string(was ? "OPEN" : "CLOSED") + ", intent " + std::to_string(intent) + ")");
        return 0;
    }
    case WM_NCHITTEST: {
        if (g_gpu_ok)
            return IsOverlayInputClickThrough() ? HTTRANSPARENT : HTCLIENT;
        break;   // CPU/layered path: alpha hit-test ของระบบทำให้อยู่แล้ว
    }
    case WM_TIMER: {
        // WM_TIMER runs on the HWND thread; CEF MTML callbacks use a separate thread.
        if (w == 5) {
            const bool hookMode = IsHookGameForegroundStable();
            const bool wasHookMode = g_game_focused.exchange(
                hookMode, std::memory_order_acq_rel);
            if (wasHookMode != hookMode)
                SendMessageW(g_hwnd, WM_OSC_FOCUS_MODE,
                    hookMode ? TRUE : FALSE, 0);
            return 0;
        }
        if (w == 6) {
            // [FIX-B] the publish worker owns readback completion + requeue;
            // this timer is kept only as a no-op placeholder.
            KillTimer(g_hwnd, 6);
            return 0;
        }
        if (w == AdapterFrameBurstTimerId) {
            const ULONGLONG now = GetTickCount64();
            if (now >= g_adapter_frame_burst_deadline) {
                // [FIX-C] While the game is focused and the hook overlay is
                //   visible, keep the refresh loop alive instead of letting the
                //   10s deadline put CEF to sleep (the desktop window is hidden
                //   in hook mode, so nothing else wakes OnAcceleratedPaint and
                //   the in-game overlay froze at 0-9 fps after every burst).
                static bool s_continuousLogged = false;
                const bool hookStillActive =
                    g_game_focused.load(std::memory_order_acquire);
                const bool overlayStillVisible =
                    g_ui_open_snapshot.load(std::memory_order_acquire) ||
                    g_notifier_overlay_visible.load(std::memory_order_acquire);
                if (hookStillActive && overlayStillVisible) {
                    g_adapter_frame_burst_deadline =
                        now + AdapterFrameBurstDurationMs;
                    if (!s_continuousLogged) {
                        s_continuousLogged = true;
                        Log("[adapter-bridge] continuous frame refresh while hook overlay visible");
                    }
                } else {
                    KillTimer(g_hwnd, AdapterFrameBurstTimerId);
                    g_adapter_frame_burst_deadline = 0;
                    g_adapter_frame_burst_force_until_deadline = false;
                    s_continuousLogged = false;
                    Log("[adapter-bridge] 10s frame refresh burst stopped: deadline reached");
                }
                return 0;
            }

            const bool hookMode =
                g_game_focused.load(std::memory_order_acquire);
            const bool overlayVisible =
                g_ui_open_snapshot.load(std::memory_order_acquire) ||
                g_notifier_overlay_visible.load(std::memory_order_acquire);

            // A metadata-only frame counter can make a frozen texture look alive.
            // During a mode transition, force fresh CEF paints and full-frame uploads
            // for the entire 10-second recovery window instead.
            if (g_browser) {
                if (hookMode && overlayVisible) {
                    bool queuedFullReadback = false;
                    {
                        // [FIX-A] burst tick no longer forces full-frame uploads:
                        //   it queued up to 60 full 8MB readbacks/s and the
                        //   completed ones measured 22-38/s (pixel proof) while
                        //   [fps] collapsed to 0-28 - the stutter. One full
                        //   refresh per second is enough for transition
                        //   recovery; steady-state uses the dirty-rect path.
                        std::lock_guard<std::mutex> lock(osc::d3d11_frame_publisher::Mutex());
                        static ULONGLONG lastBurstFull = 0;
                        const bool readbackPending = osc::d3d11_frame_publisher::HasPendingReadbackLocked();
                        if (!readbackPending &&
                            now - lastBurstFull >= 1000 &&
                            osc::d3d11_frame_publisher::HasCrossAdapterDeviceLocked() && g_gpu_dev && g_gpu_ctx &&
                            g_shared_tex && g_view_w > 0 && g_view_h > 0) {
                            lastBurstFull = now;
                            const std::vector<CefRect> fullFrame{
                                CefRect(0, 0, g_view_w, g_view_h) };
                            queuedFullReadback = osc::d3d11_frame_publisher::QueueFrameReadbackLocked(
                                g_shared_tex, fullFrame);
                        }
                    }

                    static ULONGLONG lastDirectReadbackLog = 0;
                    if (queuedFullReadback &&
                        now - lastDirectReadbackLog >= 1000) {
                        lastDirectReadbackLog = now;
                        Log("[adapter-bridge] burst queued a direct full-frame readback");
                    }
                }

                auto host = g_browser->GetHost();
                if (host) {
                    host->WasHidden(false);
                    // [FIX-A] Invalidate at 10Hz max - the old 16ms tick forced
                    //   a full-page CEF repaint 60x/s even with a static UI.
                    static ULONGLONG lastBurstInvalidate = 0;
                    if (now - lastBurstInvalidate >= 100) {
                        lastBurstInvalidate = now;
                        host->Invalidate(PET_VIEW);
                    }
                }
            }

            // Do not advance the shared frame number from the refresh timer.
            // The consumer must observe a new frame only after readback pixels are
            // successfully copied into the shared texture and its mutex is released.
            return 0;
        }
        if (w == 4) {
            if (g_browser && !g_browser->GetHost()->HasDevTools()) {
                g_devtools_open = false;
                g_devtools_close_grace_until = GetTickCount64() + 10000;
                KillTimer(g_hwnd, 4);
                Log("[devtools] closed");
            }
            return 0;
        }
        if (w == 3) {
            if (g_ui_open) {
                KillTimer(g_hwnd, 3);
                g_foreground_restore_attempts = 0;
            } else if (RestorePreviousForeground()) {
                KillTimer(g_hwnd, 3);
                g_foreground_restore_attempts = 0;
            } else if (++g_foreground_restore_attempts >= 10) {
                KillTimer(g_hwnd, 3);
                g_prev_fore = nullptr;
                Log("[focus] previous foreground restore failed after retries");
            }
            return 0;
        }
        // timer 2 = เฝ้า topmost + watchdog
        if (w == 2 && g_browser) {
            // CEF's internal scheduler handles OSR frames. This timer only keeps the
            // open overlay above games without polling at the monitor refresh rate.
            if (g_ui_open && !IsOverlayInputClickThrough()) {
                SetWindowPos(g_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
                if (g_osc_activation_attempts > 0) {
                    HWND foreground = GetForegroundWindow();
                    if (foreground == g_hwnd) {
                        g_osc_activation_attempts = 0;
                    } else if (!foreground || foreground == g_prev_fore) {
                        if (SetForegroundWithInput(g_hwnd)) {
                            g_osc_activation_attempts = 0;
                        } else if (--g_osc_activation_attempts == 0) {
                            Log("[focus] OSC activation failed after retries");
                        }
                    } else {
                        g_osc_activation_attempts = 0;
                    }
                }
            }
            return 0;
        }
        // 1) กันหน้าตายกลางทาง: renderer ตายก่อน commit → หลุดไป about:blank แล้วนิ่งเฉย
        if (g_browser) {
            std::string u = g_browser->GetMainFrame()->GetURL().ToString();
            if (u.empty() || u == "about:blank") {
                static time_t last_fix = 0;
                time_t now = time(nullptr);
                if (now - last_fix > 60) {
                    last_fix = now;
                    Log("[watchdog] page stuck at '" + u + "' → LoadURL page");
                    g_browser->GetMainFrame()->LoadURL(g_page_url);
                }
            }
            // 2) ซ้ำ zoom สูตรแท้ — บาง boot "frame loaded" ไม่ยิง (renderer ตายซ้ำ) ค่าเลยไม่ถูกตั้ง
            ApplyGenuineZoom();
            // 3) shared-texture watchdog: ขอแล้วแต่ไม่มีเฟรมใน 10 วิ → จำไว้ แล้วรีสตาร์ทตัวเอง
            //    ครั้งถัดไปใช้ GPU-assisted upload (แม่นยำ 100% แต่ยังไม่แตะ GDI)
            //    ★ ตรวจเฉพาะโหมด GPU compositor (g_gpu_ok) — โหมด gdi ไม่มี shared texture
            //      ตามธรรมชาติ เดิมเงื่อนไขหลุด → ฆ่าตัวเองทุก 10 วิแม้ ULW วาดปกติ
            if (g_gpu_ok && g_gpu_shared && !g_gpu_frame.load() && g_page_loaded_ms &&
                GetTickCount64() - g_page_loaded_ms > 10000) {
                if (g_cfg.render_mode == 1) {
                    // renderMode=gpu: ห้ามรีสตาร์ทวนลูป (config ทับ flag ทุก boot) — รายงานค้างไว้ (4.3)
                    static ULONGLONG last_stay_ms = 0;
                    const ULONGLONG now_ms = GetTickCount64();
                    if (now_ms - last_stay_ms > 60000) {
                        last_stay_ms = now_ms;
                        Log("[mode] STAY GPU_SHARED reason=no-shared-frame-10s (renderMode=gpu, no auto downgrade)");
                    }
                } else {
                    Log("[gpu] shared texture ไม่ส่งเฟรม 10 วิ → สลับโหมด upload (รีสตาร์ท)");
                    LogRenderMode("SWITCH-RESTART", "reason=no-shared-frame-10s; next boot = GPU_UPLOAD");
                    HANDLE f = CreateFileW((StorePath().substr(0, StorePath().find_last_of(L'\\')) +
                        L"\\gpu-off.flag").c_str(), GENERIC_WRITE, 0, nullptr,
                        CREATE_ALWAYS, 0, nullptr);
                    if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
                    ExitProcess(0);   // แม่เลี้ยงใหม่ → boot ถัดไปอ่าน flag
                }
            }
        }
        return 0;
    }
    case WM_USER + 1:   // (เดิม: show) แบบของแท้หน้าต่างเปิดค้าง — ไม่มี show/hide แล้ว
    case WM_USER + 2:
        Log("[window] WM_USER show/hide request ignored (genuine model)");
        return 0;
    case WM_CLOSE:
        // ★ สเปก: แอปไม่มีการปิดหรือซ่อน — ไม่แตะ browser ด้วย (ปิดได้ทางเดียว = kill process
        //   จาก supervisor/build — เดิม CloseBrowser(true) ทิ้ง window เป็นซองจดหมายไม่มีเนื้อหา)
        Log("[window] WM_CLOSE ignored (never close — spec 2026-10-07)");
        return 0;
    case WM_DESTROY:
        osc::d3d11_frame_publisher::Stop();
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(h, msg, w, l);
}

static HWND CreateClickThroughWindow(int width, int height) {
    WNDCLASSW wc{};
    wc.lpfnWndProc = DefWindowProcW;
    wc.hInstance = GetModuleHandleW(nullptr);
    wc.lpszClassName = L"DulukaOscClickThrough";
    if (!RegisterClassW(&wc) && GetLastError() != ERROR_CLASS_ALREADY_EXISTS) {
        Log("[click-through] RegisterClass FAILED err=" + std::to_string(GetLastError()));
        return nullptr;
    }

    const int window_width = NotificationSafeOverlayWidth(width);
    HWND hwnd = CreateWindowExW(
        WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
        wc.lpszClassName, L"NVIDIA OSC Click Through", WS_POPUP,
        0, 0, window_width, height, nullptr, nullptr, wc.hInstance, nullptr);
    if (!hwnd) {
        Log("[click-through] CreateWindowEx FAILED err=" + std::to_string(GetLastError()));
        return nullptr;
    }

    HDC mem = CreateCompatibleDC(nullptr);
    if (!mem) {
        Log("[click-through] CreateCompatibleDC FAILED err=" + std::to_string(GetLastError()));
        DestroyWindow(hwnd);
        return nullptr;
    }

    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = window_width;
    bi.bmiHeader.biHeight = -height;
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    void* pixels = nullptr;
    HBITMAP bitmap = CreateDIBSection(mem, &bi, DIB_RGB_COLORS, &pixels, nullptr, 0);
    if (!bitmap || !pixels) {
        DWORD error = GetLastError();
        if (bitmap) DeleteObject(bitmap);
        DeleteDC(mem);
        DestroyWindow(hwnd);
        Log("[click-through] CreateDIBSection FAILED err=" + std::to_string(error));
        return nullptr;
    }
    memset(pixels, 0, (size_t)window_width * height * 4);
    HGDIOBJ old = SelectObject(mem, bitmap);
    if (!old || old == HGDI_ERROR) {
        DWORD error = GetLastError();
        DeleteObject(bitmap);
        DeleteDC(mem);
        DestroyWindow(hwnd);
        Log("[click-through] SelectObject FAILED err=" + std::to_string(error));
        return nullptr;
    }

    POINT dst{ 0, 0 };
    POINT src{ 0, 0 };
    SIZE size{ window_width, height };
    BLENDFUNCTION blend{ AC_SRC_OVER, 0, 255, AC_SRC_ALPHA };
    BOOL updated = UpdateLayeredWindow(hwnd, nullptr, &dst, &size, mem, &src,
        0, &blend, ULW_ALPHA);
    DWORD error = updated ? ERROR_SUCCESS : GetLastError();
    SelectObject(mem, old);
    DeleteObject(bitmap);
    DeleteDC(mem);
    if (!updated) {
        DestroyWindow(hwnd);
        Log("[click-through] UpdateLayeredWindow FAILED err=" + std::to_string(error));
        return nullptr;
    }

    SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, window_width, height,
        SWP_NOACTIVATE | SWP_SHOWWINDOW);
    Log("[click-through] transparent layered proxy ready");
    return hwnd;
}

// ================= page server :<page_port> (เสิร์ฟ osc\ แบบ Share แท้) =================
static std::wstring MimeOf(const std::wstring& path) {
    if (path.ends_with(L".html")) return L"text/html";
    if (path.ends_with(L".js"))   return L"application/javascript";
    if (path.ends_with(L".css"))  return L"text/css";
    if (path.ends_with(L".json")) return L"application/json";
    if (path.ends_with(L".png"))  return L"image/png";
    if (path.ends_with(L".svg"))  return L"image/svg+xml";
    if (path.ends_with(L".woff2"))return L"font/woff2";
    if (path.ends_with(L".ico"))  return L"image/x-icon";
    return L"application/octet-stream";
}

static void ServeClient(SOCKET c) {
    // รับหัว + body ให้ครบตาม Content-Length (query บางตัว payload หลัก KB)
    std::string req;
    char buf[4096];
    for (;;) {
        int n = recv(c, buf, sizeof(buf), 0);
        if (n <= 0) break;
        req.append(buf, (size_t)n);
        size_t hdrend = req.find("\r\n\r\n");
        if (hdrend == std::string::npos) { if (req.size() > 65536) break; continue; }
        size_t clen = 0;
        size_t cl = req.find("Content-Length:");
        if (cl == std::string::npos) cl = req.find("content-length:");
        if (cl != std::string::npos) clen = (size_t)atoll(req.c_str() + cl + 15);
        if (req.size() >= hdrend + 4 + clen) break;
        if (req.size() > 512 * 1024) break;   // กันล้น
    }
    if (req.empty()) { closesocket(c); return; }
    size_t sp1 = req.find(' '), sp2 = req.find(' ', sp1 + 1);
    if (sp1 == std::string::npos || sp2 == std::string::npos) { closesocket(c); return; }
    bool is_post = req.rfind("POST", 0) == 0;
    std::string path = req.substr(sp1 + 1, sp2 - sp1 - 1);
    size_t qm = path.find('?'); if (qm != std::string::npos) path = path.substr(0, qm);

    if (is_post && path == "/cefquery") {
        // ทางเข้า cefQuery ของ shim — body = request JSON ดิบ, header บอก persistent
        size_t hdrend = req.find("\r\n\r\n");
        std::string body = (hdrend != std::string::npos) ? req.substr(hdrend + 4) : "";
        bool persistent = req.find("X-OSC-PERSISTENT: 1") != std::string::npos;
        std::string resp;
        HandleOscQuery(body, persistent, [&resp](const std::string& r) { resp = r; });
        std::string out = "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n"
            "Content-Length: " + std::to_string(resp.size()) + "\r\n\r\n" + resp;
        send(c, out.c_str(), (int)out.size(), 0); closesocket(c);
        return;
    }

    if (path == "/close-event") {
        // long-poll: ค้างจนกว่า host จะยิง close event (หรือ timeout = ตอบ 204 ให้วนใหม่)
        DWORD w = g_close_event ? WaitForSingleObject(g_close_event, 25000) : WAIT_TIMEOUT;
        if (w == WAIT_OBJECT_0) {
            ResetEvent(g_close_event);
            const char* r = "HTTP/1.1 200 OK\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n"
                "Content-Length: 13\r\n\r\nclose message";
            send(c, r, (int)strlen(r), 0);
        } else {
            const char* r = "HTTP/1.1 204 No Content\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\n"
                "Content-Length: 0\r\n\r\n";
            send(c, r, (int)strlen(r), 0);
        }
        closesocket(c);
        return;
    }

    if (path == "/toggle" || path == "/show" || path == "/hide") {
        // ★ [P1#1 Copilot] ส่ง intent เข้า UI thread เท่านั้น — สถานะ/JS/ขยายย่อ
        //   ตัดสินใจที่ WM_OSC_VIS handler (เจ้าของสถานะคนเดียว)
        //   repeats กันที่ helper ด้วย MOD_NOREPEAT (ไม่ใช้ time debounce แล้ว)
        int intent = (path == "/show") ? 1 : (path == "/hide") ? 0 : 2;
        PostMessageW(g_hwnd, WM_OSC_VIS, intent, 1);
        const char* r = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok";
        send(c, r, (int)strlen(r), 0); closesocket(c);
        Log("[toggle] " + path + " intent=" + std::to_string(intent));
        return;
    }

    if (path == "/") path = "/index.html";
    std::wstring rel(path.begin(), path.end());
    std::replace(rel.begin(), rel.end(), '/', '\\');
    wchar_t full[MAX_PATH];
    PathCombineW(full, g_cfg.page_dir.c_str(), rel.c_str() + 1);
    // กัน traversal: ต้องอยู่ใต้ page_dir
    if (_wcsnicmp(full, g_cfg.page_dir.c_str(), g_cfg.page_dir.size()) != 0) {
        const char* r = "HTTP/1.1 403\r\nContent-Length: 0\r\n\r\n";
        send(c, r, (int)strlen(r), 0); closesocket(c); return;
    }
    HANDLE f = CreateFileW(full, GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) {
        const char* r = "HTTP/1.1 404\r\nContent-Length: 0\r\n\r\n";
        send(c, r, (int)strlen(r), 0); closesocket(c); return;
    }
    LARGE_INTEGER sz{}; GetFileSizeEx(f, &sz);
    std::vector<char> data((size_t)sz.QuadPart);
    DWORD rd = 0; ReadFile(f, data.data(), (DWORD)data.size(), &rd, nullptr);
    CloseHandle(f);

    std::wstring mime = MimeOf(full);
    std::string head = "HTTP/1.1 200 OK\r\nContent-Length: " + std::to_string(rd) +
        "\r\nCache-Control: no-cache, no-store, must-revalidate"
        "\r\nAccess-Control-Allow-Origin: *\r\nConnection: close\r\nContent-Type: " +
        std::string(mime.begin(), mime.end()) + "\r\n\r\n";
    send(c, head.c_str(), (int)head.size(), 0);
    send(c, data.data(), rd, 0);
    closesocket(c);
}

static DWORD WINAPI ServerThread(LPVOID) {
    WSADATA wd; WSAStartup(MAKEWORD(2, 2), &wd);
    SOCKET s = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1; setsockopt(s, SOL_SOCKET, SO_REUSEADDR, (char*)&one, sizeof(one));
    sockaddr_in a{}; a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = htons((u_short)g_cfg.page_port);
    if (bind(s, (sockaddr*)&a, sizeof(a)) != 0 || listen(s, 16) != 0) {
        Log("[pages] bind FAILED err=" + std::to_string(WSAGetLastError()));
        return 0;
    }
    Log("[pages] serving osc at http://localhost:" + std::to_string(g_cfg.page_port));
    for (;;) {
        SOCKET c = accept(s, nullptr, nullptr);
        if (c == INVALID_SOCKET) continue;
        // ★ หนึ่ง connection ต่อ thread: /close-event เป็น long-poll 25 วิ
        //   ถ้า serve แบบ serial ใน accept loop ทุก resource (js/css/font) ต่อคิว
        //   หลังมัน = หน้าโหลดช้า/ฟอนต์ timeout (อาการที่เจอ)
        CloseHandle(CreateThread(nullptr, 0, [](LPVOID p) -> DWORD {
            SOCKET cs = (SOCKET)(intptr_t)p;
            ServeClient(cs);
            return 0;
        }, (LPVOID)(intptr_t)c, 0, nullptr));
    }
}

// แยก thread สำหรับ toggle (อีก port) — reuse ServeClient ผ่าน listener ที่สอง
static DWORD WINAPI ToggleListener(LPVOID) {
    SOCKET s = socket(AF_INET, SOCK_STREAM, 0);
    int one = 1; setsockopt(s, SOL_SOCKET, SO_REUSEADDR, (char*)&one, sizeof(one));
    sockaddr_in a{}; a.sin_family = AF_INET;
    a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
    a.sin_port = htons((u_short)g_cfg.toggle_port);
    if (bind(s, (sockaddr*)&a, sizeof(a)) != 0 || listen(s, 8) != 0) {
        Log("[toggle] bind FAILED err=" + std::to_string(WSAGetLastError()));
        return 0;
    }
    Log("[toggle] listening on 127.0.0.1:" + std::to_string(g_cfg.toggle_port));
    for (;;) {
        SOCKET c = accept(s, nullptr, nullptr);
        if (c == INVALID_SOCKET) continue;
        ServeClient(c);   // จัดการ /toggle|/show|/hide ให้
    }
}

// ================= app =================
class OscApp : public CefApp, public CefBrowserProcessHandler, public CefRenderProcessHandler {
public:
    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }
    void OnBeforeChildProcessLaunch(CefRefPtr<CefCommandLine> cmd) override {
        // ★ anti-backgrounding: renderer ต้องไม่ถูก throttle (OSR overlay วาด/นับเวลาตลอด)
        cmd->AppendSwitch("disable-background-timer-throttling");
        cmd->AppendSwitch("disable-renderer-backgrounding");
        cmd->AppendSwitch("disable-backgrounding-occluded-windows");
    }
    CefRefPtr<CefRenderProcessHandler> GetRenderProcessHandler() override { return this; }

    // ---- render side ของ message router ----
    // ⚠ ชั่วคราว: ปิด (bisect — รอบที่ paint วิ่งยังไม่มี router ฝั่งนี้) เปิดคืนเมื่อรู้สาเหตุ
    static bool RouterEnabled() { static bool v = true; return v; }  // ★ เปิดแล้ว (10:5x — สาเหตุ crash รู้แล้ว = v8 snapshot)
    static CefRefPtr<CefMessageRouterRendererSide>& Router() {
        static CefRefPtr<CefMessageRouterRendererSide> r;
        if (!r) {
            CefMessageRouterConfig cfg;
            cfg.js_query_function = "cefQuery";
            cfg.js_cancel_function = "cefQueryCancel";
            r = CefMessageRouterRendererSide::Create(cfg);
        }
        return r;
    }
    void OnContextCreated(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
        CefRefPtr<CefV8Context> context) override {
        if (RouterEnabled()) Router()->OnContextCreated(browser, frame, context);
    }
    void OnContextReleased(CefRefPtr<CefBrowser> browser, CefRefPtr<CefFrame> frame,
        CefRefPtr<CefV8Context> context) override {
        if (RouterEnabled()) Router()->OnContextReleased(browser, frame, context);
    }
    // [FIX-B] the page never sends QUERY_OSC_SET_KEYBOARD_CAPTURE (0 events in
    //   the whole log), so the low-level keyboard hook never swallowed keys and
    //   every keystroke typed while the overlay was open in-game leaked into
    //   the game. CEF 73 has no CefFocusHandler::OnFocusedNodeEditable, so the
    //   renderer reports focused-node editability over IPC instead.
    void OnFocusedNodeChanged(CefRefPtr<CefBrowser> browser,
        CefRefPtr<CefFrame> frame, CefRefPtr<CefDOMNode> node) override {
        const bool editable = node && node->IsEditable();
        static bool lastEditable = false;
        if (editable == lastEditable) return;
        lastEditable = editable;
        if (!browser || !frame) return;
        CefRefPtr<CefProcessMessage> msg =
            CefProcessMessage::Create("osc:keyboard-capture");
        if (msg && msg->GetArgumentList()->SetBool(0, editable))
            browser->SendProcessMessage(PID_BROWSER, msg);
    }
    bool OnProcessMessageReceived(CefRefPtr<CefBrowser> browser,
        CefProcessId source, CefRefPtr<CefProcessMessage> message) override {
        if (message && message->GetName() == "osc:keyboard-capture") {
            const bool capture = message->GetArgumentList() &&
                message->GetArgumentList()->GetBool(0);
            const bool was = g_overlay_keyboard_capture.exchange(
                capture, std::memory_order_acq_rel);
            if (was != capture)
                Log(std::string("[input] keyboard capture ") +
                    (capture ? "enabled (focused text control)" :
                        "released (game hotkeys pass through)"));
            return true;
        }
        if (RouterEnabled()) return Router()->OnProcessMessageReceived(browser, source, message);
        return false;
    }
    void OnBeforeCommandLineProcessing(const CefString&, CefRefPtr<CefCommandLine> cmd) override {
    // ★ bisect 2026-10-07 (renderer TERMINATED status=2 วน): รันใน child ด้วย —
    //   dumb host (ไม่ append สวิตช์ใด ๆ) เสถียร → gate ทีละก้อนหาตัวการ
    //   OSC_NOSWITCH=1 ข้ามทั้งหมด · OSC_NOGPU_SW=1 ตัด enable-gpu/use-angle ·
    //   OSC_NOHR=1 ตัด host-resolver-rules · OSC_NOPREREAD=1 ตัด no-pre-read-main-dll
    char nosw[4] = { 0 };
    if (GetEnvironmentVariableA("OSC_NOSWITCH", nosw, 4) > 0 && nosw[0] == '1') return;
    // ★ anti-backgrounding (2026-10-07): OSR หน้าถูก Chromium มองเป็น background —
    //   timers/rAF โดน throttle → open sequence ของหน้า timeout → หน้าปิดเองใน ~2 วิ
    //   (พิสูจน์: เปิดผ่าน CDP ที่แนบอยู่ = อยู่ได้ยาว ไม่มี CDP = ปิดเอง 2 วิ)
    cmd->AppendSwitch("disable-background-timer-throttling");
    cmd->AppendSwitch("disable-renderer-backgrounding");
    cmd->AppendSwitch("disable-backgrounding-occluded-windows");
        // ★ ชุดที่พิสูจน์แล้วว่า "วาดได้ + renderer เสถียร" บนเครื่องนี้:
        //   no-pre-read + host-resolver-rules — ส่วน GPU เลื่อนขั้นเป็น ANGLE→D3D11
        //   เพื่อ 60fps (จากเดิม disable-gpu = raster ด้วย CPU อย่างเดียว = กระตุก)
        char npr[4] = { 0 };
        if (!(GetEnvironmentVariableA("OSC_NOPREREAD", npr, 4) > 0 && npr[0] == '1'))
            cmd->AppendSwitch("no-pre-read-main-dll");
        // ★ เกม exclusive fullscreen ทำ CEF GPU process CHECK ตาย → software render
        bool sw = HasFlag(L"sw-render.flag");
        if (sw) {
            static bool swlog = false;
            if (!swlog) { swlog = true; Log("[gpu] sw-render.flag → disable-gpu (เกม fullscreen?)"); }
        }
        // กันเหนียว: child ต้องมี --lang เสมอ (CHECK ใน chrome_main_delegate.cc:1382)
        if (!cmd->HasSwitch("lang")) cmd->AppendSwitchWithValue("lang", "en-US");
        // ★ V8 sandbox abort (mov r15d,reason; ud2) ตอน CefInitialize: มี DLL อื่น
        //   แย่ง address space จนจอง sandbox 1TB ไม่ได้ → ปิด sandbox ของ V8
        char ns[4] = { 0 };
        DWORD nslen = GetEnvironmentVariableA("OSC_NOV8SB", ns, 4);
        if (nslen > 0 && ns[0] == '1')
            cmd->AppendSwitchWithValue("js-flags", "--no-sandbox");
        // วินิจฉัย: env OSC_NOGPU=1 → ตัด GPU ออก (จับชนกับ CEF ของแท้)
        char ng[4] = { 0 };
        DWORD nglen = GetEnvironmentVariableA("OSC_NOGPU", ng, 4);
        char sp[4] = { 0 };
        DWORD splen = GetEnvironmentVariableA("OSC_SINGLE", sp, 4);
        if (splen > 0 && sp[0] == '1') {
            cmd->AppendSwitch("single-process");   // วินิจฉัย: ข้ามการ spawn child
            Log("[cmd] OSC_SINGLE=1 → single-process");
        }
        if (sw || (nglen > 0 && ng[0] == '1')) {
            // ปิด GPU ทั้งเชิงม้า + display compositor (เกม exclusive จะ CHECK ตาย)
            cmd->AppendSwitch("disable-gpu");
            cmd->AppendSwitch("disable-gpu-compositing");
        } else {
            char gsw[4] = { 0 };
            if (!(GetEnvironmentVariableA("OSC_NOGPU_SW", gsw, 4) > 0 && gsw[0] == '1')) {
                cmd->AppendSwitch("enable-gpu");   // OSR shared texture ต้องการ GPU compositing จริง
                cmd->AppendSwitchWithValue("use-angle", "d3d11");

                // CEF can GPU-composite a shared texture while still CPU-rasterizing
                // page tiles. Force Chromium 73's Skia GPU raster path when GPU is
                // enabled; set OSC_NO_GPU_RASTER=1 to roll back if a driver/page regresses.
                char noGpuRaster[4] = { 0 };
                DWORD noGpuRasterLen = GetEnvironmentVariableA(
                    "OSC_NO_GPU_RASTER", noGpuRaster, sizeof(noGpuRaster));
                if (!(noGpuRasterLen > 0 && noGpuRaster[0] == '1')) {
                    cmd->AppendSwitch("force-gpu-rasterization");
                }
            }
        }
        // ตัด endpoint ภายนอกให้ fail ทันที (ไม่งั้น main-frame load ค้าง)
        char hr[4] = { 0 };
        if (!(GetEnvironmentVariableA("OSC_NOHR", hr, 4) > 0 && hr[0] == '1'))
            cmd->AppendSwitchWithValue("host-resolver-rules",
                "MAP gfwsl.geforce.com ~NOTFOUND, MAP events.gfe.nvidia.com ~NOTFOUND, "
                "MAP accounts.nvgs.nvidia.com ~NOTFOUND, MAP rds-assets.nvidia.com ~NOTFOUND, "
                "MAP nvidia.custhelp.com ~NOTFOUND, MAP *.gvt1.com ~NOTFOUND, "
                "MAP dl.google.com ~NOTFOUND, MAP update.googleapis.com ~NOTFOUND");
    }
    void OnContextInitialized() override {
        CEF_REQUIRE_UI_THREAD();
        OscClient::CreateRouter();

        int w = GetSystemMetrics(SM_CXSCREEN), h = GetSystemMetrics(SM_CYSCREEN);
        int overlay_w = NotificationSafeOverlayWidth(w);
        if (g_cfg.design_w > 0 && g_cfg.design_h > 0) {
            // Canvas UI ฐานออกแบบ (เช่น 1920×1080 = UI scale 1.0) — host ย่อ/ขยายให้พอดีจอ
            g_view_w = g_cfg.design_w;
            g_view_h = g_cfg.design_h;
        } else if (g_cfg.design_w > 0) {
            // กว้างตาม design (UI 1.0) + สูงตามสัดส่วนจอจริง — จอ 16:10 ได้ 1920×1200
            // (บังคับ 1920×1080 บนจอ 16:10 = ภาพยืดแนวตั้ง ~11% — บทเรียน 06:41)
            g_view_w = g_cfg.design_w;
            g_view_h = (int)((LONGLONG)g_cfg.design_w * h / w);
        } else {
            // โหมด zoom path (เหมือนแท้): view = ขนาดจอ · zoom เป็นตัว scale เดียว
            g_render_dsf = 1.0;
            g_view_w = (int)(w * g_cfg.render_scale);
            g_view_h = (int)(h * g_cfg.render_scale);
        }

        WNDCLASSW wc{}; wc.lpfnWndProc = WndProc;
        wc.hInstance = GetModuleHandleW(nullptr);
        wc.hCursor = LoadCursor(nullptr, IDC_ARROW);
        wc.lpszClassName = L"DulukaOscNative";
        RegisterClassW(&wc);
        // Create topmost for the entire process lifetime, retaining its full visible region.
        g_hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            L"DulukaOscNative", L"NVIDIA OSC", WS_POPUP,
            0, 0, overlay_w, h, nullptr, nullptr, wc.hInstance, nullptr);
        g_game_focused.store(IsHookGameForegroundStable(), std::memory_order_release);
        // ★ บทเรียน 03:57 — DComp กับ LAYERED ขัดกัน (DWM ใช้เลเยอร์ GDI ทับ visual)
        //   และ LAYERED ก็ทำให้ DComp มองไม่เห็น → โมเดลใหม่ไม่พึ่งการซ่อนหน้าต่างแล้ว
        //   Click-through/input mode follows the main-frame URL, not the open/closed command.
        // GPU display uses DComp; the main HWND becomes layered only on #/base.
        if (g_cfg.windowed) {
            // ★ [P1#2 Copilot] windowed mode = CEF render เองใน child — ห้าม init
            //   D3D11/DComp เลย (CreateTargetForHwnd topmost วางทับ child + swapchain
            //   ไม่ได้รับเฟรมจาก CEF native = surface ว่างบังหน้า)
            Log("[gpu] windowed mode → CEF native render (ไม่มี custom compositor)");
        } else if (g_cfg.gdi_compositor) {
            // โหมด GDI: layered window + UpdateLayeredWindow (เส้นทางที่พิสูจน์แล้วว่าแสดงผล)
            SetWindowLongW(g_hwnd, GWL_EXSTYLE,
                GetWindowLongW(g_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
            g_clickthrough_hwnd = CreateClickThroughWindow(w, h);
            Log(g_clickthrough_hwnd
                ? "[gpu] compositor=gdi → ULW path with URL-driven click-through proxy"
                : "[gpu] compositor=gdi → ULW path (proxy unavailable; using layered hit-testing)");
        } else {
            g_clickthrough_hwnd = CreateClickThroughWindow(w, h);
            if (!g_clickthrough_hwnd || !GpuCompositorInit(g_hwnd, w, h)) {
                if (g_clickthrough_hwnd) {
                    DestroyWindow(g_clickthrough_hwnd);
                    g_clickthrough_hwnd = nullptr;
                }
                SetWindowLongW(g_hwnd, GWL_EXSTYLE,
                    GetWindowLongW(g_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
                Log("[gpu] hybrid init failed → layered CPU fallback");
            } else {
                ShowWindow(g_hwnd, SW_HIDE);
                Log("[gpu] hybrid ready: DComp surface with route-driven layered pass-through");
            }
        }
        if (!g_clickthrough_hwnd) {
            ShowWindow(g_hwnd, SW_SHOWNOACTIVATE);
            SetWindowPos(g_hwnd, HWND_TOPMOST,
                0, 0, overlay_w, h, SWP_NOACTIVATE);
        }
        ApplyOpenShareInputMode(); // OpenShare starts closed; input mode follows its state.
        Log("[window] boot: click-through until OpenShare opens");
        SetTimer(g_hwnd, 1, 30000, nullptr);   // เฝ้าระวัง: หน้าค้าง about:blank → กู้คืนเอง
        SetTimer(g_hwnd, 5, 100, nullptr);     // focus can change while the CEF surface is idle

        // zoom แท้กำหนด scale css→จอ (ใช้แปลง display rects)
        {
            double fit = (double)w / 1920.0, fh = (double)h / 1080.0;
            if (fh < fit) fit = fh;
            g_css_scale = fit;
        }

        // ★ บทเรียน 06:25 — ห้าม CreateBrowserSync ใน OnContextInitialized
        //   (CEF เองเตือน: "Always execute asynchronously ... during app initialization")
        //   init ของ global browser context แข่งกัน → NOTREACHED ud2 ~25-50%
        CefPostDelayedTask(TID_UI, new CreateBrowserTask(), 400);
    }
    IMPLEMENT_REFCOUNTING(OscApp);
};

// ================= main =================
int WINAPI wWinMain(HINSTANCE hInst, HINSTANCE, PWSTR, int) {
    // ★ บทเรียน 2026-10-07 (renderer TERMINATED status=2 วนทุกโหมด/ทุกหน้า — แก้ด้วย
    //   stub page + windowed test): child process ห้ามรันโค้ดของเราก่อน CefExecuteProcess!
    //   เดิม LoadConfig/LogInit/VEH รันใน renderer ด้วย — VEH จับ 0xE06D7363 (C++
    //   exception ที่ Chromium โยนเองตอนบูต) แล้วสร้าง std::string + เขียนไฟล์ระหว่าง
    //   exception dispatch (heap/loader lock ถูกถือ) → deadlock → renderer โดน kill
    //   (ตายเงียบ — ไม่มี [crash] ใน log เพราะตายก่อนเขียนสำเร็จ) · dumb host ไม่มี
    //   โค้ดก่อน CefInitialize = เสถียรเสมอ จึงจบเส้นทาง child ที่บรรทัดแรก
    timeBeginPeriod(1);   // timer ละเอียด 1ms — ยิง begin frame ตาม Hz จอได้
    CefMainArgs args(hInst);
    CefRefPtr<OscApp> app = new OscApp();
    if (CefExecuteProcess(args, app.get(), nullptr) >= 0) return 0;

    // CEF renderer/GPU/utility processes return above; only the main overlay
    // process owns this mutex, shared by every staged copy of NVIDIA OSC.exe.
    HANDLE instance_mutex = CreateMutexW(
        nullptr, TRUE, L"Local\\NVIDIA.ShadowPlay.OSC");
    if (!instance_mutex) {
        Log("[instance] mutex creation failed: " + std::to_string(GetLastError()));
        return 1;
    }
    if (GetLastError() == ERROR_ALREADY_EXISTS) {
        CloseHandle(instance_mutex);
        return 0;
    }

    LoadConfig();
    LogInit();
    osc::d3d9_frame_publisher::SetVisibilityProvider(&OscFramePublisherOverlayVisible);
    osc::d3d11_frame_publisher::Initialize(osc::d3d11_frame_publisher::HostBindings{
        &OscHostGpuDevice, &OscHostGpuContext, &OscHostSharedTexture, &OscHostWindow,
        &OscHostViewWidth, &OscHostViewHeight, &OscHostGameFocused,
        &OscHostOverlayVisible, &OscHostD3D11PublisherLog
    });
    AddVectoredExceptionHandler(1, CrashVecHandler);

    // flag จากรอบก่อน: shared texture ไม่เวิร์กบนเครื่องนี้ → ใช้ GPU-assisted upload
    {
        wchar_t exe[MAX_PATH]; GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::wstring dir(exe); dir.resize(dir.find_last_of(L'\\'));
        g_gpu_shared = GetFileAttributesW((dir + L"\\appdata\\gpu-off.flag").c_str()) == INVALID_FILE_ATTRIBUTES;
        if (!g_gpu_shared) Log("[gpu] gpu-off.flag พบ → โหมด GPU-assisted upload");
    }
    // ★ fallback chain: บูตก่อน crash ตอนสร้าง browser (เกม fullscreen) → software รอบนี้
    if (HasFlag(L"boot-pending.flag")) {
        ClearFlag(L"boot-pending.flag");
        TouchFlag(L"sw-render.flag");
        Log("[gpu] บูตก่อนพังตอนสร้าง browser → สลับ software render รอบนี้");
    }
    g_close_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);   // auto-reset: กันสถานะค้างทำหน้าใหม่ปิดทันที

    CefSettings settings;
    // จับ C++ terminate (0xe06d7363) — เขียนเหตุผลก่อนตาย
    std::set_terminate([]() {
        Log("[crash] std::terminate — unhandled C++ exception");
        std::abort();
    });
    // OSR + handler ครบแล้ว (Phase B) — เปิดได้ (จำบทเรียน: เปิดก่อนมี handler = crash-loop)
    settings.windowless_rendering_enabled = !g_cfg.windowed;   // ★ windowed = CEF render เอง (GPU)
    settings.background_color = CefColorSetARGB(0, 0, 0, 0);
    settings.log_severity = LOGSEVERITY_WARNING;
    // ★ bisect 2026-10-07: OSC_NOLOGFILE=1 = ไม่ตั้ง log_file (ลูกไม่ได้รับ --log-file)
    {
        char nlf[4] = { 0 };
        if (!(GetEnvironmentVariableA("OSC_NOLOGFILE", nlf, 4) > 0 && nlf[0] == '1'))
            CefString(&settings.log_file) =
                L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Logs\\OSC-cef.log";
    }
    settings.no_sandbox = true;               // ทางการ: ปิด sandbox ระดับ settings (switch มาช้าเกิน)
    // ★ บทเรียน 06:20 — ไม่ตั้ง locale = child (utility/renderer) ไม่มี --lang
    //   → chrome_main_delegate CHECK ตาย → CreateBrowserSync FATAL ตาม (ud2 ตั้งแต่ 29 ก.ย. 04:14)
    //   (bisect: OSC_NOLOCALE=1 = ไม่ตั้ง — ทดสอบฝั่งกุญแจ)
    {
        char nlc[4] = { 0 };
        if (!(GetEnvironmentVariableA("OSC_NOLOCALE", nlc, 4) > 0 && nlc[0] == '1'))
            CefString(&settings.locale) = "en-US";
    }
    {
        char mz[4] = { 0 };
        if (GetEnvironmentVariableA("OSC_MIN", mz, 4) > 0 && mz[0] == '1') {
            settings.remote_debugging_port = 0;
            Log("[cfg] OSC_MIN=1 → remote debugging off");
        } else {
            settings.remote_debugging_port = g_cfg.debug_port;   // วินิจฉัย: /json
        }
    }
    // ★ สูตร CefSharp: CEF วิ่งเธรดของตัวเอง (MTML) แอปปั๊มข้อความเอง — ทุก CEF ที่ "ผ่าน" บนเครื่องนี้เป็นแบบนี้
    settings.multi_threaded_message_loop = true;
    // ★ บทเรียน 05:43 — ลบ CefCache ทิ้งระหว่างรัน = CreateBrowserSync IMMEDIATE_CRASH
    //   (cache dir หาย mid-day แล้ว CEF 138 ไม่กู้เอง) — OSC_NEWCACHE=1 ทดสอบ cache ใหม่
    // ★ path รวมศูนย์ที่ nvidia-osc.json (cachePath/subprocessPath) — เดิม hardcode ชี้ dir
    //   เก่า "NVIDIA OSC Native" ซึ่งมี CefCache ปน CEF 138 (พิษต่อ 73 — บทเรียน 06:5x
    //   renderer crash-loop) — deploy ใหม่ = build\...\Overlay OSC\NVIDIA OSC\ + cache เปล่า
    {
        char nc[4] = { 0 };
        std::wstring cache = g_cfg.cache_path;
        if (GetEnvironmentVariableA("OSC_NEWCACHE", nc, 4) > 0 && nc[0] == '1') {
            cache += L"2";
            Log("[cfg] OSC_NEWCACHE=1 → ใช้ cache สำรอง (ต่อท้าย 2)");
        }
        CefString(&settings.cache_path) = cache.c_str();
    }
    CefString(&settings.browser_subprocess_path) = g_cfg.subprocess_path.c_str();
    if (!CefInitialize(args, settings, app.get(), nullptr)) return 1;
    Log("[main] initialized — MTML + own pump");

    HANDLE hook_thread = CreateThread(nullptr, 0, HookThreadProc, nullptr, 0, nullptr);
    if (!hook_thread) {
        Log("[ui] ERROR: could not start low-level mouse hook thread err=" + std::to_string(GetLastError()));
        CefShutdown();
        return 1;
    }
    CloseHandle(hook_thread);
    CreateThread(nullptr, 0, ServerThread, nullptr, 0, nullptr);
    CreateThread(nullptr, 0, ToggleListener, nullptr, 0, nullptr);

    // โชว์หลังหน้าโหลดพอสมควร (เหมือนแท้: overlay โผล่เมื่อพร้อม) — เริ่มซ่อน รอ Alt+Z
    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0)) {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    OscStopDesktopPresentWorker();
    CefShutdown();
    return 0;
}
