// osc_main.cpp — NVIDIA OSC Native (ของเรา): host C++ เหมือน NVIDIA Share.exe
//
// เฟส B+C: OSR (offscreen) + layered compositor per-pixel alpha + input forwarding
// + page server :3000 + toggle :59003 + อ่าน NvConfig\nvidia-osc.json — ใช้แทน CefSharp
//
// เลียนแบบของแท้ (จาก import table ของ Share.exe จริง):
//   CEF OnPaint (BGRA) → premultiply → UpdateLayeredWindow (WS_EX_LAYERED)
//   = Layered Window + AlphaBlend + GDI แบบเดียวกับ offscreen_window.cpp ของ NVIDIA
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
#include "include/wrapper/cef_helpers.h"
#include "include/wrapper/cef_message_router.h"
#include "include/base/cef_logging.h"

#include <windows.h>
#include <psapi.h>
#include <windowsx.h>      // GET_X_LPARAM / GET_Y_LPARAM
#include <string>
#include <atomic>
#include <shlwapi.h>
#include <cmath>
#include <vector>
#include <functional>
// ★ GPU compositor ทั้งสายแบบของแท้ (offscreen_renderer_d3d11.cpp):
//   CEF shared texture (D3D11) → shader premultiply → swapchain alpha → DComp
//   ไม่มีพิกเซลแตะ CPU เลย (เดิม: premultiply 7MB/เฟรมใน C++ = กระตุก + กิน CPU)
#include <d3d11.h>
#include <d3d11_3.h>   // ID3D11Device1 + OpenSharedResource1 (NT handle ของ CEF)
#include <dxgi1_2.h>
#include <dcomp.h>
#include <d3dcompiler.h>
#pragma comment(lib, "d3d11.lib")
#pragma comment(lib, "dxgi.lib")
#pragma comment(lib, "dcomp.lib")
#pragma comment(lib, "d3dcompiler.lib")
#pragma comment(lib, "psapi.lib")

// ================= config (แบบง่าย: หา "key": ค่า ใน json) =================
struct OscConfig {
    std::wstring page_dir = L"C:\\My Project\\NVIDIA-Shadowplay\\Project\\Overlay OSC\\NVIDIA OSC\\osc";
    std::wstring log_file = L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Logs\\NVIDIA OSC Native.log";
    double render_scale = 1.0;
    int design_w = 0, design_h = 0;   // Canvas UI ฐานออกแบบ (1920×1080 = UI 1.0) — 0 = ตามจอ×renderScale
    bool show_on_boot = false;
    bool topmost = true;
    int page_port = 3000;
    int toggle_port = 59003;
    int frame_rate = 60;      // CEF OSR default 30 = กระตุก — ตั้งให้เต็มจอปกติ
    double ui_scale = 1.0;    // คูณเข้า fit ของสูตร zoom แท้ (1.0 = ตรง binary เป๊ะ)
    // ★ Phase 1 (path รวมศูนย์ที่ nvidia-osc.json) — ค่า default ตรงกับ json เสมอ
    std::wstring cache_path = L"C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\Overlay OSC\NVIDIA OSC\CefCache";
    std::wstring subprocess_path = L"C:\My Project\NVIDIA-Shadowplay\build\NVIDIA ShadowPlay\Overlay OSC\NVIDIA OSC\NVIDIA OSC.exe";
    int debug_port = 59099;
    bool gdi_compositor = false;   // true = UpdateLayeredWindow (แสดงผลชัวร์) แทน DComp
};
static OscConfig g_cfg;

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
    HANDLE f = CreateFileW(L"C:\\My Project\\NVIDIA-Shadowplay\\Project\\NvConfig\\nvidia-osc.json",
        GENERIC_READ, FILE_SHARE_READ, nullptr, OPEN_EXISTING, 0, nullptr);
    if (f == INVALID_HANDLE_VALUE) return;
    char buf[8192]; DWORD n = 0; ReadFile(f, buf, sizeof(buf) - 1, &n, nullptr); CloseHandle(f);
    buf[n] = 0;
    std::string json(buf);
    std::wstring page = CfgStr(json, "page");
    if (!page.empty()) {
        // page = ...\osc\index.html → dir = ...\osc
        size_t k = page.rfind(L'\\');
        if (k != std::wstring::npos) g_cfg.page_dir = page.substr(0, k);
    }
    std::wstring lg = CfgStr(json, "logFile");
    if (!lg.empty()) g_cfg.log_file = lg;
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
    if (!cp.empty()) g_cfg.cache_path = cp;
    std::wstring sp = CfgStr(json, "subprocessPath");
    if (!sp.empty()) g_cfg.subprocess_path = sp;
    g_cfg.debug_port = (int)CfgNum(json, "debugPort", 59099);
    g_cfg.gdi_compositor = CfgStr(json, "compositor") == L"gdi";
}

static HANDLE g_log = INVALID_HANDLE_VALUE;
static void LogInit() {
    g_log = CreateFileW(g_cfg.log_file.c_str(), FILE_APPEND_DATA,
        FILE_SHARE_READ | FILE_SHARE_WRITE, nullptr, OPEN_ALWAYS, 0, nullptr);
}
static void Log(const std::string& msg) {
    HANDLE f = g_log;
    if (f == INVALID_HANDLE_VALUE) return;
    SYSTEMTIME st; GetLocalTime(&st);
    char line[1200];
    int n = snprintf(line, sizeof(line), "%04d-%02d-%02d %02d:%02d:%02d %s\r\n",
        st.wYear, st.wMonth, st.wDay, st.wHour, st.wMinute, st.wSecond, msg.c_str());
    DWORD w = 0; WriteFile(f, line, n, &w, nullptr);
}

// ★ VEH: จับ backtrace ของ int3 (CHECK ใน libcef) ก่อน process ตาย — เขียน module+offset
static LONG WINAPI CrashVecHandler(EXCEPTION_POINTERS* ep) {
    const DWORD c = ep->ExceptionRecord->ExceptionCode;
    if (c != 0x80000003 && c != 0xe06d7363 && c != 0xC0000409) return EXCEPTION_CONTINUE_SEARCH;
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
static CefRefPtr<CefBrowser> g_browser;
static std::string g_page_url;          // URL หน้า OSC — กู้คืนเมื่อ renderer ตายกลางทาง
static std::atomic<bool> g_visible{ false };
static bool g_close_registered = false;
static CefRefPtr<CefMessageRouterBrowserSide::Callback> g_close_callback;
static HANDLE g_close_event = nullptr;   // สัญญาณปิด OSC สำหรับ long-poll /close-event ของ shim
static int g_view_w = 0, g_view_h = 0;     // ขนาด view = CSS px (สูตรแท้: 1920×1200 ที่จอ 1680×1050)
static double g_render_dsf = 1.0;          // device scale factor = fit ของจอ (เส้นทาง rasterize แบบแท้)

// ================= compositor: UpdateLayeredWindow per-pixel alpha =================
static HDC g_hdc_mem = nullptr;
static HBITMAP g_hbm = nullptr, g_hbm_old = nullptr;
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
static ID3D11SamplerState* g_gpu_samp = nullptr;
static bool g_gpu_ok = false;           // GPU path ใช้ได้
static std::atomic<bool> g_gpu_frame{ false };
static bool g_gpu_shared = false;       // ขอ shared texture จาก CEF (เต็ม GPU)
static ULONGLONG g_page_loaded_ms = 0;  // เฝ้า: shared ไม่ยิงใน 8 วิ → สลับโหมด upload
static ID3D11Texture2D* g_up_tex = nullptr;   // texture รับ upload (โหมดกึ่ง GPU)
static int g_up_w = 0, g_up_h = 0;
static IDCompositionDevice* g_dcomp_dev = nullptr;
static IDCompositionTarget* g_dcomp_target = nullptr;
static IDCompositionVisual* g_dcomp_visual = nullptr;
static IDXGIDevice2* g_dxgi_dev2 = nullptr;
static bool g_dcomp_bound = false;
static ID3D11Texture2D* g_own_rt = nullptr;   // RT ของเรา (back buffer ของ swapchain เครื่องนี้ bind=0 วาดไม่ได้)
static int g_own_w = 0, g_own_h = 0;

// วาดลง RT ของเราเสมอ แล้ว copy ลง back buffer — บังคับทุกเส้นทางผ่านนี้
static bool EnsureOwnRT(int w, int h) {
    if (g_own_rt && g_own_w == w && g_own_h == h) return true;
    if (g_own_rt) { g_own_rt->Release(); g_own_rt = nullptr; g_gpu_rtv->Release(); g_gpu_rtv = nullptr; }
    D3D11_TEXTURE2D_DESC td{};
    td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
    td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
    td.Usage = D3D11_USAGE_DEFAULT;
    td.BindFlags = D3D11_BIND_RENDER_TARGET | D3D11_BIND_SHADER_RESOURCE;
    if (FAILED(g_gpu_dev->CreateTexture2D(&td, nullptr, &g_own_rt))) return false;
    if (FAILED(g_gpu_dev->CreateRenderTargetView(g_own_rt, nullptr, &g_gpu_rtv))) return false;
    g_own_w = w; g_own_h = h;
    return true;
}
// copy RT → back buffer ของ swapchain (CopyResource ใช้ได้แม้ bind=0)
static void PresentToSwap(int w, int h) {
    ID3D11Texture2D* back = nullptr;
    if (SUCCEEDED(g_gpu_swap->GetBuffer(0, __uuidof(ID3D11Texture2D), (void**)&back))) {
        g_gpu_ctx->CopyResource(back, g_own_rt);
        back->Release();
        HRESULT hp = g_gpu_swap->Present(1, 0);          // vsync
        static ULONGLONG last_hr_log = 0;
        ULONGLONG now = GetTickCount64();
        if (FAILED(hp) && now - last_hr_log > 3000) {
            last_hr_log = now;
            char b[80]; snprintf(b, sizeof(b), "[gpu] Present FAILED hr=0x%08lX", (unsigned long)hp);
            Log(b);
        }
        if (g_dcomp_dev) g_dcomp_dev->Commit();
        static bool pres_log = false;
        if (!pres_log) { pres_log = true; Log("[gpu] composite PRESENT ok (own-rt + copy)"); }
        g_gpu_frame.store(true);
    }
}

static const char* kQuadVS =
    "struct VSOut { float4 pos : SV_Position; float2 uv : TEXCOORD; };"
    "VSOut VS(uint id : SV_VertexID) {"
    "  VSOut o;"
    "  o.uv = float2((id << 1) & 2, id & 2);"
    "  o.pos = float4(o.uv.x * 2 - 1, 1 - o.uv.y * 2, 0, 1);"
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

static bool GpuCompositorInit(HWND hwnd, int w, int h) {
    UINT flags = D3D11_CREATE_DEVICE_BGRA_SUPPORT;
    D3D_FEATURE_LEVEL fl;
    HRESULT hr = D3D11CreateDevice(nullptr, D3D_DRIVER_TYPE_HARDWARE, nullptr, flags,
        nullptr, 0, D3D11_SDK_VERSION, &g_gpu_dev, &fl, &g_gpu_ctx);
    if (FAILED(hr)) { Log("[gpu] device FAILED hr=" + std::to_string((long)hr)); return false; }
    Log("[gpu] device OK");

    IDXGIDevice2* dxgiDev = nullptr; IDXGIFactory2* fac = nullptr;
    IDXGISwapChain1* swap = nullptr; ID3D11PixelShader* ps = nullptr;
    ID3D11VertexShader* vs = nullptr; ID3D11SamplerState* samp = nullptr;

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
        IDCompositionDevice* dcompDev = nullptr; IDCompositionTarget* target = nullptr; IDCompositionVisual* vis = nullptr;
        if (FAILED(hr = DCompositionCreateDevice(dxgiDev, __uuidof(IDCompositionDevice), (void**)&dcompDev)))
            { Log("[gpu] step dcomp dev hr=" + std::to_string((long)hr)); goto fail; }
        if (FAILED(hr = dcompDev->CreateTargetForHwnd(hwnd, TRUE, &target)))
            { Log("[gpu] step target hr=" + std::to_string((long)hr)); goto fail; }
        if (FAILED(hr = dcompDev->CreateVisual(&vis)))
            { Log("[gpu] step visual hr=" + std::to_string((long)hr)); goto fail; }
        // ★ target/visual ย้ายไปสร้างตอนโชว์หน้าต่างครั้งแรก (BindDcompTarget)
        //   เดิมสร้างตอนหน้าต่างซ่อน → DWM ไม่ผูก visual tree → บางบูตแสดงไม่ออกตลอด
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
    g_gpu_swap = swap; g_gpu_vs = vs; g_gpu_ps = ps; g_gpu_samp = samp;
    dxgiDev->Release(); fac->Release();
    g_gpu_ok = true;
    Log("[gpu] D3D11 compositor ready (shared texture + DComp premultiplied)");
    return true;
fail:
    if (swap) swap->Release(); if (ps) ps->Release(); if (vs) vs->Release(); if (samp) samp->Release();
    if (fac) fac->Release(); if (dxgiDev) dxgiDev->Release();
    Log("[gpu] init FAILED → fallback CPU path");
    return false;
}

// วาด shared texture ของ CEF ลง swapchain (เรียกจาก OnAcceleratedPaint — เธรด UI ของ CEF)
static void GpuComposite(uint64_t shared_handle, int w, int h) {
    if (!g_gpu_ok) return;
    ID3D11Resource* tex = nullptr;
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
            return;
        }
    }
    // shared texture ของ CEF อาจถูกสร้างเป็น TYPELESS — SRV desc ต้องบังคับรูปแบบจริง
    ID3D11ShaderResourceView* srv = nullptr;
    {
        D3D11_SHADER_RESOURCE_VIEW_DESC sd{};
        sd.Format = DXGI_FORMAT_B8G8R8A8_UNORM;
        sd.ViewDimension = D3D11_SRV_DIMENSION_TEXTURE2D;
        sd.Texture2D.MipLevels = 1;
        if (FAILED(g_gpu_dev->CreateShaderResourceView(tex, &sd, &srv)) &&
            FAILED(g_gpu_dev->CreateShaderResourceView(tex, nullptr, &srv))) {
            static int srf = 0;
            if (srf++ < 3) Log("[gpu] shared SRV create FAILED");
            tex->Release(); return;
        }
    }
    // ★ วินิจฉัย: อ่านพิกเซลจริงจาก shared texture (ครั้งเดียว)
    {
        static int dumped = 0;
        dumped++;
        if (dumped < 3 || dumped == 300 || dumped == 1200 || dumped == 3000) {
            ID3D11Texture2D* t2 = nullptr;
            if (FAILED(tex->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&t2)) || !t2) { tex->Release(); srv->Release(); return; }
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
                    size_t mid = (size_t)(sd2.Height / 2) * mp.RowPitch + (sd2.Width / 2) * 4;
                    // ตำแหน่ง pill (CSS 1096 คูณ 0.875 → ~959) กลางจอแนวนอน ~840
                    size_t pill = (size_t)(959) * mp.RowPitch + (size_t)(840) * 4;
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

    if (EnsureOwnRT(w, h)) {
        // ★ GPU path หลัก: copy shared texture (premultiplied แล้ว) ตรงเข้า
        //   own RT → back buffer — pure GPU copy (shader path พิสูจน์แล้ววะโปร่ง 09:24)
        char dc[4] = { 0 };
        bool want_direct = true;
        if (GetEnvironmentVariableA("OSC_SHADERDRAW", dc, 4) > 0 && dc[0] == '1')
            want_direct = false;   // วินิจฉัยย้อนกลับ: OSC_SHADERDRAW=1 ใช้ shader
        if (want_direct) {
            ID3D11Texture2D* t2 = nullptr;
            if (SUCCEEDED(tex->QueryInterface(__uuidof(ID3D11Texture2D), (void**)&t2)) && t2) {
                D3D11_TEXTURE2D_DESC td{}; t2->GetDesc(&td);
                if ((int)td.Width == w && (int)td.Height == h) {
                    g_gpu_ctx->CopyResource(g_own_rt, t2);
                    PresentToSwap(w, h);
                    static bool dcl = false;
                    if (!dcl) { dcl = true; Log("[gpu] direct-copy path active (GPU ล้วน)"); }
                } else {
                    Log("[gpu] DIRECTCOPY size mismatch — shader path");
                }
                t2->Release();
            }
        } else {
            D3D11_VIEWPORT vp{ 0, 0, (float)w, (float)h, 0, 1 };
            g_gpu_ctx->OMSetRenderTargets(1, &g_gpu_rtv, nullptr);
            g_gpu_ctx->RSSetViewports(1, &vp);
            g_gpu_ctx->PSSetShaderResources(0, 1, &srv);
            g_gpu_ctx->PSSetSamplers(0, 1, &g_gpu_samp);
            g_gpu_ctx->VSSetShader(g_gpu_vs, nullptr, 0);
            g_gpu_ctx->PSSetShader(g_gpu_ps_pass ? g_gpu_ps_pass : g_gpu_ps, nullptr, 0);
            g_gpu_ctx->IASetPrimitiveTopology(D3D11_PRIMITIVE_TOPOLOGY_TRIANGLELIST);
            g_gpu_ctx->Draw(3, 0);              // fullscreen triangle
            PresentToSwap(w, h);
        }
    }
    srv->Release(); tex->Release();
}

// โหมดกึ่ง GPU (fallback): CEF ส่ง BGRA มาทาง CPU ครั้งเดียว → upload → shader premultiply
// ไม่มี SetDIBits/UpdateLayeredWindow/premultiply-loop เลย — GDI=0
static void GpuUploadComposite(const void* bgra, int w, int h) {
    if (!g_gpu_ok) return;
    if (!g_up_tex || g_up_w != w || g_up_h != h) {
        if (g_up_tex) { g_up_tex->Release(); g_up_tex = nullptr; }
        D3D11_TEXTURE2D_DESC td{};
        td.Width = w; td.Height = h; td.MipLevels = 1; td.ArraySize = 1;
        td.Format = DXGI_FORMAT_B8G8R8A8_UNORM; td.SampleDesc.Count = 1;
        td.Usage = D3D11_USAGE_DYNAMIC; td.BindFlags = D3D11_BIND_SHADER_RESOURCE;
        td.CPUAccessFlags = D3D11_CPU_ACCESS_WRITE;
        if (FAILED(g_gpu_dev->CreateTexture2D(&td, nullptr, &g_up_tex))) {
            static int ctf = 0; if (ctf++ < 3) Log("[gpu] upload CreateTexture2D FAILED"); return;
        }
        g_up_w = w; g_up_h = h;
    }
    D3D11_MAPPED_SUBRESOURCE map{};
    if (FAILED(g_gpu_ctx->Map(g_up_tex, 0, D3D11_MAP_WRITE_DISCARD, 0, &map))) {
        static int mf = 0; if (mf++ < 3) Log("[gpu] upload Map FAILED"); return;
    }
    // BGRA ตรง ๆ — เส้นทาง DMA ของไดรเวอร์ (ไม่ใช่ลูปคำนวณ)
    if ((int)map.RowPitch == w * 4) {
        memcpy(map.pData, bgra, (size_t)w * h * 4);
    } else {
        for (int y = 0; y < h; y++)
            memcpy((char*)map.pData + (size_t)y * map.RowPitch,
                   (const char*)bgra + (size_t)y * w * 4, (size_t)w * 4);
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
    g_gpu_ctx->Unmap(g_up_tex, 0);
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

// ═══ click-through ตาม display rects (QUERY_OSC_SET_DISPLAY_RECTS ของแท้) ═══
// หน้าบอก host ว่า UI อยู่ตรงไหน (พิกัด CSS) → host เก็บเป็น rect จอ
// นอก rect = HTTRANSPARENT (คลิกทะลุ) — แบบนี้ไม่ต้องพึ่ง alpha hit-test ของ layered
static std::vector<RECT> g_ui_rects;         // พิกัดจอ (scale แล้ว)
static double g_css_scale = 1.0;             // css px → จอ px (zoom แท้ fit)
static bool g_ui_open = false;

// ★ สั่ง visibility จากเธรดอื่น (CEF/HTTP) ผ่าน message — หน้าต่างเป็นของเธรด CEF UI
static const UINT WM_OSC_VIS = WM_APP + 2;

// ★ หน้าต่างถือ WS_EX_TRANSPARENT ตลอดชีวิต (คลิกทะลุข้ามโปรเซสแบบระบบ)
//   คลิก "ทับ UI" จับที่ low-level mouse hook แทน — HTTRANSPARENT/สลับ style ไม่พอ
static void SetClickThrough(bool on) { (void)on; }

static void ParseDisplayRects(const std::string& req) {
    // ดึง array หลัง "displayRects": [ {...}, ... ]
    size_t p = req.find("\"displayRects\"");
    if (p == std::string::npos) return;
    size_t ab = req.find('[', p);
    if (ab == std::string::npos) return;
    size_t ae = req.find(']', ab);
    if (ae == std::string::npos) ae = req.size();
    std::string body = req.substr(ab, ae - ab);
    g_ui_rects.clear();
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
        RECT r;
        r.left = (LONG)(x * g_css_scale); r.top = (LONG)(y * g_css_scale);
        r.right = (LONG)((x + w) * g_css_scale); r.bottom = (LONG)((y + h) * g_css_scale);
        g_ui_rects.push_back(r);
        open = true;
    }
    if (g_ui_open != open)
        Log(std::string("[ui] displayRects: ") + (open ? (std::to_string(g_ui_rects.size()) + " rects — interactive") : "empty — click-through"));
    g_ui_open = open;
    SetClickThrough(!open || g_ui_rects.empty());
}

// view-scale state: ตำแหน่ง/ขนาดภาพที่ถูกวางลงจอ (ใช้แมพเมาส์จอ↔view)
static int g_disp_w = 0, g_disp_h = 0, g_dst_x = 0, g_dst_y = 0;
static std::atomic<double> g_ui_adjust{ 1.0 };   // ปรับสดด้วย Ctrl+ลูกเมาส์

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
static std::vector<unsigned char> g_dst;

// ⚠ เคารพขอบเขต x ให้เป๊ะ: บัฟเฟอร์ของ CEF ใช้ได้เฉพาะใน dirty rect —
// อ่านเกินออกไป = ดึงพิกเซลเก่ามาทับ = UI สองสถานะซ้อนกัน (บทเรียน 06:2x)
static void PremultRect(const unsigned char* src, int w, int x0, int x1, int y0, int y1) {
    for (int y = y0; y < y1; y++) {
        unsigned char* d = g_dst.data() + ((size_t)w * y + x0) * 4;
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
        GpuUploadComposite(bgra, w, h);
        if (g_gpu_frame.load()) { EnsureShownOnce(); return; }
    }
    if (!g_hdc_mem || g_bmp_w != w || g_bmp_h != h) {
        if (g_hdc_mem) {
            SelectObject(g_hdc_mem, g_hbm_old);
            DeleteObject(g_hbm);
            DeleteDC(g_hdc_mem);
        }
        HDC scr = GetDC(nullptr);
        g_hdc_mem = CreateCompatibleDC(scr);
        g_hbm = CreateCompatibleBitmap(scr, w, h);
        ReleaseDC(nullptr, scr);
        g_hbm_old = (HBITMAP)SelectObject(g_hdc_mem, g_hbm);
        g_bmp_w = w; g_bmp_h = h;
        g_dst.assign((size_t)w * h * 4, 0);
    }
    // CEF ให้ BGRA (ไม่ premultiply) — layered window ต้องการ premultiplied
    // ★ เต็มเฟรมทุกครั้ง: ในโหมด GPU+Canvas compositing, dirty rects ที่ CEF รายงาน
    //   ไม่ครอบคลุมบริเวณที่ถูกลบ (ผี UI สองสถานะซ้อนกัน — บทเรียน 06:29)
    //   ความถูกต้องมาก่อนความเร็ว: 7MB/premultiply-ต่อเฟรม ยังไหวที่ 60fps
    const unsigned char* src = (const unsigned char*)bgra;
    PremultRect(src, w, 0, w, 0, h);
    (void)dirty;
    BITMAPINFO bi{};
    bi.bmiHeader.biSize = sizeof(BITMAPINFOHEADER);
    bi.bmiHeader.biWidth = w;
    bi.bmiHeader.biHeight = -h;   // top-down
    bi.bmiHeader.biPlanes = 1;
    bi.bmiHeader.biBitCount = 32;
    bi.bmiHeader.biCompression = BI_RGB;
    SetDIBits(g_hdc_mem, g_hbm, 0, h, g_dst.data(), &bi, DIB_RGB_COLORS);

    // ★ view-scale: หน้าเพจล็อกที่ design (เช่น 1920) zoom 100% เสมอ — layout ไม่ขยับ
    //   host ขยาย/ย่อ "ทั้งหน้า" เป็นภาพเดียว: S = (จอ/view) × uiScale × g_ui_adjust
    //   uiScale > 1 = ใหญ่กว่า fit → ภาพล้นจอ ตัดขอบกลางจอ (ขอบ = ผ้าม่านเปล่า ไม่มีองค์ประกอบ)
    //   g_ui_adjust หมุนได้สด ๆ ด้วย Ctrl+ลูกเมาส์ — เห็นผลทันทีไม่ต้องรีสตาร์ท
    int sw = GetSystemMetrics(SM_CXSCREEN), sh = GetSystemMetrics(SM_CYSCREEN);
    double S = ((double)sw / w) * g_cfg.ui_scale * g_ui_adjust.load();
    g_disp_w = (int)(w * S); g_disp_h = (int)(h * S);
    g_dst_x = (sw - g_disp_w) / 2; g_dst_y = (sh - g_disp_h) / 2;   // ≤0 เมื่อขยาย = crop กลางจอ
    POINT dstpt{ g_dst_x, g_dst_y };
    SIZE winsz{ g_disp_w, g_disp_h };
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
        reply("{\"port\":59011,\"disableSecurity\":true,"
              "\"securityCookie\":\"\","
              "\"gfwsl\":{\"server\":\"https://gfwsl.geforce.com/\"},"
              "\"jarvis\":{\"server\":\"\"},"
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
    if (cmd == "QUERY_WIN_CLOSE_OSC") {
        // แบบของแท้: หน้าเพจเป็นเจ้าของ UI — ปิดเองทาง socket แล้ว
        g_ui_rects.clear();
        g_ui_open = false;
        if (g_hwnd) PostMessageW(g_hwnd, WM_OSC_VIS, 0, 0);   // ซ่อน = คลิกทะลุทั้งจอ
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
    if (cmd == "QUERY_WIN_OPEN_OSC") {
        // UI เปิดได้ = render ใช้ได้ → ให้บูตหน้าลอง GPU เต็มรูปแบบอีกครั้ง
        ClearFlag(L"sw-render.flag");
        g_ui_open = true;
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

// ================= หน้าต่าง: แบบของแท้ — เปิดค้างตลอดเวลา =================
// Share.exe จริง: หน้าต่าง Topmost ทับจอ "ตลอด" พื้นหลังโปร่งใส — พิกเซล alpha=0
// ของ layered window คลิกทะลุเองตามกลไกของระบบ เปิด/ปิด OSC จึงเป็นเรื่องของ
// "หน้าเพจ" (วาด UI / วาดโปร่งใส) ทาง socket :59002 เท่านั้น — host ไม่แตะ
// visibility เลย (เดิมที่ซ่อน/โชว์ตาม Alt+Z ทำ parity เพี้ยน = ต้องกดรัว)
static void EnsureShownOnce() {
    static bool done = false;
    if (done || !g_hwnd) return;
    done = true;
    // ★ ไม่โชว์ที่นี่อีกแล้ว — หน้าต่างโผล่เมื่อ OSC เปิดเท่านั้น (WIN_OPEN_OSC)
    Log("[window] first composite — stays hidden until OSC opens (click-through = no window)");
}

// ================= client + OSR render handler =================
// ★ สร้าง browser หลัง init เสร็จสนิท (เรียกผ่าน CefPostDelayedTask)
class OscClient : public CefClient,
                  public CefLifeSpanHandler,
                  public CefLoadHandler,
                  public CefRenderHandler,
                  public CefDisplayHandler,
                  public CefRequestHandler,
                  public CefContextMenuHandler {
public:
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    CefRefPtr<CefLoadHandler> GetLoadHandler() override { return this; }
    CefRefPtr<CefRenderHandler> GetRenderHandler() override { return this; }
    CefRefPtr<CefDisplayHandler> GetDisplayHandler() override { return this; }
    CefRefPtr<CefRequestHandler> GetRequestHandler() override { return this; }
    // Overlay: ไม่มี context menu (คลิกขวา = เมนู browser ไม่จำเป็น)
    CefRefPtr<CefContextMenuHandler> GetContextMenuHandler() override { return this; }
    void OnBeforeContextMenu(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame>, CefRefPtr<CefContextMenuParams>,
        CefRefPtr<CefMenuModel> model) override { model->Clear(); }

    void OnLoadingStateChange(CefRefPtr<CefBrowser> browser, bool isLoading, bool, bool) override {
        if (!isLoading) {
            Log("[cef] frame loaded");
            g_page_loaded_ms = GetTickCount64();
            // ล็อก zoom 100% ทุก load — กัน zoom เก่าที่ Chromium จำใน cache มายุ่ง layout
            ApplyGenuineZoom(true);
            // ฉีด cefQuery shim (ทาง HTTP /cefquery — ไม่ใช้ render-side router)
                browser->GetMainFrame()->ExecuteJavaScript(
                // CEF 73 = เอนจินเดียวกับ Share.exe แท้ → CSS แท้ทำงานถูกต้องเอง
                // (ถอด CSS injection ที่เคยง้อ Chrome 138 แล้ว — 2026-10-07)
                "(function(){"
                "if(window.cefQuery)return;"
                "function post(b,p){return fetch('/cefquery',{method:'POST',headers:{'X-OSC-PERSISTENT':p?'1':'0'},body:b});}"
                "window.cefQuery=function(q){"
                "if(!q||typeof q.request!=='string'){q&&q.onFailure&&q.onFailure(1,'bad query');return;}"
                "var pers=!!q.persistent;"
                "post(q.request,pers).then(function(r){"
                "if(!pers)return r.text().then(function(t){q.onSuccess&&q.onSuccess(t);});"
                "(function wait(){fetch('/close-event').then(function(r){return r.status===200?r.text():null;})"
                ".then(function(t){if(t){q.onSuccess&&q.onSuccess(t);}else{setTimeout(wait,500);}})"
                ".catch(function(){setTimeout(wait,1000);});})();"
                "return null;})"
                ".catch(function(e){q.onFailure&&q.onFailure(1,String(e));});};"
                "window.cefQueryCancel=function(){};"
                "})();",
                browser->GetMainFrame()->GetURL(), 0);
        }
    }
    void OnLoadError(CefRefPtr<CefBrowser>, CefRefPtr<CefFrame> frame, ErrorCode code,
        const CefString& text, const CefString& url) override {
        Log("[loaderror] code=" + std::to_string((int)code) + " " + text.ToString() + " " + url.ToString());
    }
    void OnRenderProcessTerminated(CefRefPtr<CefBrowser> browser, CefRequestHandler::TerminationStatus status) override {
        Log("[renderer] TERMINATED status=" + std::to_string((int)status) +
            " → LoadURL page");
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
    void OnAcceleratedPaint(CefRefPtr<CefBrowser>, PaintElementType type, const RectList&,
        void* shared_handle) override {
        if (type != PET_VIEW) return;
        static int calls = 0;
        if (calls++ < 3) Log("[gpu] OnAcceleratedPaint #" + std::to_string(calls) +
            " handle=0x" + std::to_string((unsigned long long)(uintptr_t)shared_handle));
        static int cnt = 0;
        static ULONGLONG t0 = 0;
        ULONGLONG now = GetTickCount64();
        if (!t0) t0 = now;
        cnt++;
        if (now - t0 >= 2000) {
            Log("[fps] " + std::to_string(cnt * 1000 / (int)(now - t0)) + " fps (GPU path)");
            cnt = 0; t0 = now;
        }
        GpuComposite((uint64_t)(uintptr_t)shared_handle, g_view_w, g_view_h);
        if (g_gpu_frame.load()) EnsureShownOnce();
    }
    bool GetScreenPoint(CefRefPtr<CefBrowser>, int viewX, int viewY, int& screenX, int& screenY) override {
        // view → จอ (ผ่าผ่านตำแหน่ง/ขนาดภาพบนจอ)
        if (g_disp_w > 0) {
            screenX = g_dst_x + (int)((LONGLONG)viewX * g_disp_w / g_view_w);
            screenY = g_dst_y + (int)((LONGLONG)viewY * g_disp_h / g_view_h);
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
    if (GetEnvironmentVariableA("OSC_WINDOWED", ow, 4) > 0 && ow[0] == '1') {
        RECT rcChild = { 0, 0, 800, 600 };
        wi.SetAsChild(g_hwnd, rcChild);
        Log("[cfg] OSC_WINDOWED=1 → child windowed browser");
    } else {
        wi.SetAsWindowless(g_hwnd);
    }
    char nsh[4] = { 0 };
    DWORD nshlen = GetEnvironmentVariableA("OSC_NOSHARED", nsh, 4);
    wi.shared_texture_enabled = (g_gpu_ok && g_gpu_shared && !(nshlen > 0 && nsh[0] == '1')) ? 1 : 0;
    CefBrowserSettings bs;
    bs.background_color = CefColorSetARGB(0, 0, 0, 0);
    bs.windowless_frame_rate = g_cfg.frame_rate;
    g_page_url = "http://localhost:" + std::to_string(g_cfg.page_port) + "/index.html";
    TouchFlag(L"boot-pending.flag");   // ค้าง = พังตรงนี้ → บูตหน้า sw mode
    auto created = CefBrowserHost::CreateBrowserSync(wi, new OscClient(),
        g_page_url, bs, nullptr);
    if (created) ClearFlag(L"boot-pending.flag");
    Log("=== NVIDIA OSC Native boot (OSR, CEF 73 = engine เดียวกับ Share.exe) === CreateBrowserSync=" +
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
    if (g_disp_w > 0) {
        int x = (int)((LONGLONG)(pt.x - g_dst_x) * g_view_w / g_disp_w);
        int y = (int)((LONGLONG)(pt.y - g_dst_y) * g_view_h / g_disp_h);
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

// ★ low-level mouse hook: หน้าต่าง WS_EX_TRANSPARENT ถาวร (คลิกทะลุเกมเสมอ)
//   คลิกที่ตรง UI rects เมื่อ OSC เปิด = ดักที่ hook → ส่ง CEF → ไม่ตกลงเกม
static CefMouseEvent MkMousePt(POINT s) {
    CefMouseEvent e{};
    if (g_disp_w > 0) {
        int x = (int)((LONGLONG)(s.x - g_dst_x) * g_view_w / g_disp_w);
        int y = (int)((LONGLONG)(s.y - g_dst_y) * g_view_h / g_disp_h);
        e.x = x < 0 ? 0 : (x >= g_view_w ? g_view_w - 1 : x);
        e.y = y < 0 ? 0 : (y >= g_view_h ? g_view_h - 1 : y);
    } else { e.x = s.x; e.y = s.y; }
    if (GetKeyState(VK_SHIFT) < 0)   e.modifiers |= EVENTFLAG_SHIFT_DOWN;
    if (GetKeyState(VK_CONTROL) < 0) e.modifiers |= EVENTFLAG_CONTROL_DOWN;
    if (GetKeyState(VK_MENU) < 0)    e.modifiers |= EVENTFLAG_ALT_DOWN;
    return e;
}
static bool ScreenInUiRects(POINT s) {
    // ★ แบบของแท้: OSC เปิด = modal — จับเมาส์ทั้งจอส่งเข้า CEF
    //   (คลิกนอกปุ่ม = เพจปิด overlay เอง / คลิกปุ่ม = action)
    //   ยุคก่อน hook อยู่บนเธรด UI ที่ตันเลยเคอร์เซอร์แข็ง — ตอนนี้ hook เธรดเฉพาะแล้ว
    if (!g_ui_open) return false;
    (void)s;
    return true;
}
static HHOOK g_mouse_hook = nullptr;
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
        case WM_LBUTTONDOWN:  host->SetFocus(true); host->SendMouseClickEvent(e, MBT_LEFT, false, 1); break;
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
    if (code == HC_ACTION && g_browser) {
        MSLLHOOKSTRUCT* m = (MSLLHOOKSTRUCT*)l;
        // ★ เมาส์ต้องขยับได้เสมอ: WM_MOUSEMOVE ห้ามกืน (return 1 = เคอร์เซอร์แข็ง)
        if (w == WM_MOUSEMOVE) {
            if (ScreenInUiRects(m->pt)) {
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
        // คลิก/ล้อ: กลืนเฉพาะเมื่ออยู่ในกรอบปุ่มจริง
        if (ScreenInUiRects(m->pt)) {
            HookEv ev{ (int)w, 0, m->pt.x, m->pt.y };
            if (w == WM_MOUSEWHEEL) ev.data = GET_WHEEL_DELTA_WPARAM(m->mouseData);
            auto t = new HookEventTask();
            t->ev = ev;
            CefPostTask(TID_UI, t);
            return 1;   // กลืนคลิก — ไม่ให้ตกลงเกม
        }
    }
    return CallNextHookEx(nullptr, code, w, l);
}
static DWORD WINAPI HookThreadProc(LPVOID) {
    g_mouse_hook = SetWindowsHookExW(WH_MOUSE_LL, LowLevelMouseProc, GetModuleHandleW(nullptr), 0);
    if (g_mouse_hook) Log("[ui] low-level mouse hook installed (dedicated thread)");
    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0) > 0) { TranslateMessage(&m); DispatchMessageW(&m); }
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

// ★ ผูก DComp target/visual กับหน้าต่าง "หลังโชว์" — DWM จะจับ visual tree ถูกต้อง
static void BindDcompTarget() {
    if (g_dcomp_bound || !g_dcomp_dev || !g_dxgi_dev2 || !g_hwnd) return;
    IDCompositionTarget* target = nullptr;
    IDCompositionVisual* vis = nullptr;
    if (FAILED(g_dcomp_dev->CreateTargetForHwnd(g_hwnd, TRUE, &target))) { Log("[gpu] BindDcompTarget: target FAILED"); return; }
    if (FAILED(g_dcomp_dev->CreateVisual(&vis))) { target->Release(); Log("[gpu] BindDcompTarget: visual FAILED"); return; }
    if (FAILED(vis->SetContent(g_gpu_swap))) { vis->Release(); target->Release(); Log("[gpu] BindDcompTarget: SetContent FAILED"); return; }
    if (FAILED(target->SetRoot(vis))) { vis->Release(); target->Release(); Log("[gpu] BindDcompTarget: SetRoot FAILED"); return; }
    if (FAILED(g_dcomp_dev->Commit())) { vis->Release(); target->Release(); Log("[gpu] BindDcompTarget: Commit FAILED"); return; }
    vis->Release(); target->Release();
    g_dcomp_bound = true;
    Log("[gpu] dcomp target BOUND after show");
}

static LRESULT CALLBACK WndProc(HWND h, UINT msg, WPARAM w, LPARAM l) {
    if (g_browser) {
        auto host = g_browser->GetHost();
        switch (msg) {
        case WM_MOUSEMOVE: { auto e = MkMouse(l); host->SendMouseMoveEvent(e, false); return 0; }
        case WM_MOUSELEAVE: { CefMouseEvent e{}; e.x = -1; e.y = -1; host->SendMouseMoveEvent(e, true); return 0; }
        case WM_LBUTTONDOWN: { auto e = MkMouse(l); g_browser->GetHost()->SetFocus(true); host->SendMouseClickEvent(e, MBT_LEFT, false, 1); SetCapture(h); return 0; }
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
        case WM_KEYDOWN: case WM_SYSKEYDOWN:
            ForwardKey(msg, w, l); return 0;
        case WM_KEYUP: case WM_SYSKEYUP:
            ForwardKey(msg, w, l); return 0;
        case WM_CHAR:
            ForwardKey(msg, w, l); return 0;
        }
    }
    switch (msg) {
    case WM_OSC_VIS: {
        if (w) {
            // ★ ผู้ใช้กำหนด: OSC ขึ้นจอหลักเสมอ (primary)
            POINT zero{ 0, 0 };
            HMONITOR mon = MonitorFromPoint(zero, MONITOR_DEFAULTTOPRIMARY);
            MONITORINFO mi{ sizeof(mi) };
            if (mon && GetMonitorInfoW(mon, &mi)) {
                SetWindowPos(g_hwnd, HWND_TOPMOST,
                    mi.rcMonitor.left, mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left,
                    mi.rcMonitor.bottom - mi.rcMonitor.top,
                    SWP_NOACTIVATE);
                char b[96];
                snprintf(b, sizeof(b), "[win] monitor-follow: %d,%d %dx%d",
                    mi.rcMonitor.left, mi.rcMonitor.top,
                    mi.rcMonitor.right - mi.rcMonitor.left, mi.rcMonitor.bottom - mi.rcMonitor.top);
                Log(b);
            }
        }
        ShowWindow(g_hwnd, w ? SW_SHOWNOACTIVATE : SW_HIDE);
        // ★ OSR: CEF ไม่ repaint เองเมื่อหน้านิ่ง — ตอนเปิด UI ต้องไล่ invalidate
        if (w) {
            SetTimer(g_hwnd, 2, 16, nullptr);    // เปิด: 60Hz
            if (g_browser) g_browser->GetHost()->Invalidate(PET_VIEW);
        } else {
            SetTimer(g_hwnd, 2, 250, nullptr);   // ปิด: 4Hz — CEF อุ่นเครื่องไว้ เปิดเมื่อไหร่เฟรมมีเนื้อหาทันที
        }
        // DWM ทิ้ง visual tree ของหน้าต่างที่ถูกซ่อน — bind ใหม่ + Commit ทุกครั้งที่โชว์
        if (w && g_dcomp_dev) {
            BindDcompTarget();
            g_dcomp_dev->Commit();
            // ★ วินิจฉัย UI ไม่ขึ้น: OSC_REDTEST=1 → เฟรมแดงสด 3 วิตอนเปิด
            char rt[4] = { 0 };
            if (GetEnvironmentVariableA("OSC_REDTEST", rt, 4) > 0 && rt[0] == '1'
                && g_own_rt && g_gpu_rtv && g_gpu_ctx) {
                const float red[4] = { 0, 0, 1, 1 };
                g_gpu_ctx->ClearRenderTargetView(g_gpu_rtv, red);
                PresentToSwap(0, 0);
                Log("[diag] REDTEST frame presented");
                Sleep(3000);
            }
        }
        Log(std::string("[window] ") + (w ? "shown (OSC open)" : "hidden (OSC closed — click-through)"));
        return 0;
    }
    case WM_NCHITTEST: {
        // ★ click-through แบบของแท้ (ไม่ใช่ alpha hit-test): นอก UI rects = ทะลุ
        if (g_gpu_ok) {
            if (!g_ui_open || g_ui_rects.empty()) return HTTRANSPARENT;
            POINT pt{ GET_X_LPARAM(l), GET_Y_LPARAM(l) };
            for (auto& r : g_ui_rects)
                if (pt.x >= r.left && pt.x < r.right && pt.y >= r.top && pt.y < r.bottom)
                    return HTCLIENT;
            return HTTRANSPARENT;
        }
        break;   // CPU/layered path: alpha hit-test ของระบบทำให้อยู่แล้ว
    }
    case WM_TIMER: {
        // WM_TIMER วิ่งบนเธรด UI ของ CEF (หน้าต่างถูกสร้างบนเธรดนั้น) — เรียก browser ได้ตรง ๆ
        // timer 2 = ไล่ invalidate ขณะ OSC เปิด (OSR ไม่ repaint เอง)
        if (w == 2 && g_browser) {
            g_browser->GetHost()->Invalidate(PET_VIEW);
            // ★ วิดีโอ/เกม fullscreen ยกตัวเองขึ้น topmost ทับเรา — ยืนยันสิทธิ์บนสุดทุก ~0.5s
            static int tz = 0;
            if (++tz >= 32 && g_ui_open) {
                tz = 0;
                SetWindowPos(g_hwnd, HWND_TOPMOST, 0, 0, 0, 0,
                    SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);
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
            if (g_gpu_shared && !g_gpu_frame.load() && g_page_loaded_ms &&
                GetTickCount64() - g_page_loaded_ms > 10000) {
                Log("[gpu] shared texture ไม่ส่งเฟรม 10 วิ → สลับโหมด upload (รีสตาร์ท)");
                HANDLE f = CreateFileW((StorePath().substr(0, StorePath().find_last_of(L'\\')) +
                    L"\\gpu-off.flag").c_str(), GENERIC_WRITE, 0, nullptr,
                    CREATE_ALWAYS, 0, nullptr);
                if (f != INVALID_HANDLE_VALUE) CloseHandle(f);
                ExitProcess(0);   // แม่เลี้ยงใหม่ → boot ถัดไปอ่าน flag
            }
        }
        return 0;
    }
    case WM_USER + 1:   // (เดิม: show) แบบของแท้หน้าต่างเปิดค้าง — ไม่มี show/hide แล้ว
    case WM_USER + 2:
        Log("[window] WM_USER show/hide request ignored (genuine model)");
        return 0;
    case WM_CLOSE:
        if (g_browser) g_browser->GetHost()->CloseBrowser(true);
        return 0;
    case WM_DESTROY:
        PostQuitMessage(0);
        return 0;
    }
    return DefWindowProcW(h, msg, w, l);
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
        const char* r = "HTTP/1.1 200 OK\r\nContent-Length: 2\r\n\r\nok";
        send(c, r, (int)strlen(r), 0); closesocket(c);
        // แบบของแท้: host ไม่ยุ่ง visibility — เปิด/ปิดเป็นของหน้าเพจทาง :59002
        Log("[toggle] " + path + " ignored — page owns UI state (genuine model)");
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
    CefRefPtr<CefRenderProcessHandler> GetRenderProcessHandler() override { return this; }

    // ---- render side ของ message router ----
    // ⚠ ชั่วคราว: ปิด (bisect — รอบที่ paint วิ่งยังไม่มี router ฝั่งนี้) เปิดคืนเมื่อรู้สาเหตุ
    static bool RouterEnabled() { static bool v = false; return v; }
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
    bool OnProcessMessageReceived(CefRefPtr<CefBrowser> browser,
        CefProcessId source, CefRefPtr<CefProcessMessage> message) override {
        if (RouterEnabled()) return Router()->OnProcessMessageReceived(browser, source, message);
        return false;
    }
    void OnBeforeCommandLineProcessing(const CefString&, CefRefPtr<CefCommandLine> cmd) override {
        // ★ ชุดที่พิสูจน์แล้วว่า "วาดได้ + renderer เสถียร" บนเครื่องนี้:
        //   no-pre-read + host-resolver-rules — ส่วน GPU เลื่อนขั้นเป็น ANGLE→D3D11
        //   เพื่อ 60fps (จากเดิม disable-gpu = raster ด้วย CPU อย่างเดียว = กระตุก)
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
            cmd->AppendSwitch("enable-gpu");   // OSR shared texture ต้องการ GPU compositing จริง
            cmd->AppendSwitchWithValue("use-angle", "d3d11");
        }
        // ตัด endpoint ภายนอกให้ fail ทันที (ไม่งั้น main-frame load ค้าง)
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
        g_hwnd = CreateWindowExW(
            WS_EX_TOOLWINDOW | (g_cfg.topmost ? WS_EX_TOPMOST : 0),
            L"DulukaOscNative", L"NVIDIA OSC", WS_POPUP,
            0, 0, w, h, nullptr, nullptr, wc.hInstance, nullptr);
        // ★ บทเรียน 03:57 — DComp กับ LAYERED ขัดกัน (DWM ใช้เลเยอร์ GDI ทับ visual)
        //   และ LAYERED ก็ทำให้ DComp มองไม่เห็น → ใช้หน้าต่างธรรมดา non-layered
        //   + "ซ่อนหน้าต่างเมื่อ OSC ปิด" (ไม่มีหน้าต่าง = คลิกทะลุ 100% ข้ามโปรเซส)
        //   + LL mouse hook ส่งคลิกเข้า CEF เมื่อเปิด
        // input path: hook ข้ามโปรเซส (ต้องอยู่บนเธรดที่มี message pump = เธรดนี้)
        CreateThread(nullptr, 0, HookThreadProc, nullptr, 0, nullptr);   // hook เธรดเฉพาะ
        // ★ GPU path ของแท้: init D3D11+DComp ก่อน ถ้าได้ = ไม่ต้อง layered เลย
        //   (alpha จัดการโดย swapchain premultiplied + DWM — คลิกทะลุใช้ display rects)
        if (g_cfg.gdi_compositor) {
            // โหมด GDI: layered window + UpdateLayeredWindow (เส้นทางที่พิสูจน์แล้วว่าแสดงผล)
            SetWindowLongW(g_hwnd, GWL_EXSTYLE,
                GetWindowLongW(g_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
            Log("[gpu] compositor=gdi → ULW path");
        } else if (GpuCompositorInit(g_hwnd, w, h)) {
            // ★ คง WS_EX_LAYERED ไว้ — DComp วาดผ่าน DWM ได้ปกติ และ LAYERED+TRANSPARENT
            //   คือคู่เดียวที่ hit-test ทะลุข้ามโปรเซส (ทดสอบ WindowFromPoint แล้ว)
            Log("[gpu] window: DComp + LAYERED(alpha=255) + TRANSPARENT = click-through ข้ามโปรเซส");
        } else {
            SetWindowLongW(g_hwnd, GWL_EXSTYLE,
                GetWindowLongW(g_hwnd, GWL_EXSTYLE) | WS_EX_LAYERED);
        }
        ShowWindow(g_hwnd, SW_HIDE);   // GPU: รอเฟรมแรก / CPU: layered ต้อง ULW ก่อนแสดง
        SetTimer(g_hwnd, 1, 30000, nullptr);   // เฝ้าระวัง: หน้าค้าง about:blank → กู้คืนเอง
        SetTimer(g_hwnd, 2, 250, nullptr);     // อุ่น CEF: invalidate จาง ๆ ตั้งแต่บูต

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
    LoadConfig();
    LogInit();
    AddVectoredExceptionHandler(1, CrashVecHandler);

    // วินิจฉัย: บันทึกชนิดของ process ลูก (--type=... )
    // ★ บทเรียน 05:38 — เดิมใช้ std::string(w.substr().begin(), w.substr().end())
    //   = dangling iterator จาก temporary 2 ตัว → length_error ตามดวง
    //   → child (GPU) ตายรัว ๆ → OSR ไม่มี GPU process → browser FATAL
    {
        LPWSTR cl = GetCommandLineW();
        std::wstring w(cl);
        size_t t = w.find(L"--type=");
        if (t != std::wstring::npos) {
            std::string s;
            for (size_t i = t; i < w.size() && i < t + 60; i++) s += (char)w[i];
            Log("[child] " + s);
        }
    }

    // ★ fallback chain: บูตก่อน crash ตอนสร้าง browser (เกม fullscreen) → software รอบนี้
    if (HasFlag(L"boot-pending.flag")) {
        ClearFlag(L"boot-pending.flag");
        TouchFlag(L"sw-render.flag");
        Log("[gpu] บูตก่อนพังตอนสร้าง browser → สลับ software render รอบนี้");
    }
    CefMainArgs args(hInst);
    CefRefPtr<OscApp> app = new OscApp();
    if (CefExecuteProcess(args, app.get(), nullptr) >= 0) return 0;
    g_close_event = CreateEventW(nullptr, FALSE, FALSE, nullptr);   // auto-reset: กันสถานะค้างทำหน้าใหม่ปิดทันที
    // flag จากรอบก่อน: shared texture ไม่เวิร์กบนเครื่องนี้ → ใช้ GPU-assisted upload
    {
        wchar_t exe[MAX_PATH]; GetModuleFileNameW(nullptr, exe, MAX_PATH);
        std::wstring dir(exe); dir.resize(dir.find_last_of(L'\\'));
        g_gpu_shared = GetFileAttributesW((dir + L"\\appdata\\gpu-off.flag").c_str()) == INVALID_FILE_ATTRIBUTES;
        if (!g_gpu_shared) Log("[gpu] gpu-off.flag พบ → โหมด GPU-assisted upload");
    }

    CefSettings settings;
    // จับ C++ terminate (0xe06d7363) — เขียนเหตุผลก่อนตาย
    std::set_terminate([]() {
        Log("[crash] std::terminate — unhandled C++ exception");
        std::abort();
    });
    // OSR + handler ครบแล้ว (Phase B) — เปิดได้ (จำบทเรียน: เปิดก่อนมี handler = crash-loop)
    settings.windowless_rendering_enabled = true;
    settings.background_color = CefColorSetARGB(0, 0, 0, 0);
    settings.log_severity = LOGSEVERITY_VERBOSE;   // จับ CHECK-fail: ต้องเห็นทุกบรรทัดก่อน ud2
    CefString(&settings.log_file) =
        L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Logs\\OSC-cef.log";
    settings.no_sandbox = true;               // ทางการ: ปิด sandbox ระดับ settings (switch มาช้าเกิน)
    // ★ บทเรียน 06:20 — ไม่ตั้ง locale = child (utility/renderer) ไม่มี --lang
    //   → chrome_main_delegate CHECK ตาย → CreateBrowserSync FATAL ตาม (ud2 ตั้งแต่ 29 ก.ย. 04:14)
    CefString(&settings.locale) = "en-US";
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
    {
        char nc[4] = { 0 };
        if (GetEnvironmentVariableA("OSC_NEWCACHE", nc, 4) > 0 && nc[0] == '1') {
            CefString(&settings.cache_path) =
                L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Overlay OSC\\NVIDIA OSC Native\\CefCache2";
            Log("[cfg] OSC_NEWCACHE=1 → CefCache2");
        } else {
            CefString(&settings.cache_path) =
                L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Overlay OSC\\NVIDIA OSC Native\\CefCache";
        }
    }
    CefString(&settings.browser_subprocess_path) =
        L"C:\\My Project\\NVIDIA-Shadowplay\\build\\NVIDIA ShadowPlay\\Overlay OSC\\NVIDIA OSC Native\\NVIDIA OSC.exe";
    if (!CefInitialize(args, settings, app.get(), nullptr)) return 1;
    Log("[main] initialized — MTML + own pump");

    CreateThread(nullptr, 0, ServerThread, nullptr, 0, nullptr);
    CreateThread(nullptr, 0, ToggleListener, nullptr, 0, nullptr);

    // โชว์หลังหน้าโหลดพอสมควร (เหมือนแท้: overlay โผล่เมื่อพร้อม) — เริ่มซ่อน รอ Alt+Z
    MSG m;
    while (GetMessageW(&m, nullptr, 0, 0)) {
        TranslateMessage(&m);
        DispatchMessageW(&m);
    }
    CefShutdown();
    return 0;
}
