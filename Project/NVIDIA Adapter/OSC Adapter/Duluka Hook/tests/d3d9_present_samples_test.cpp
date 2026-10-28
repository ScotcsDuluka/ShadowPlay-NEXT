// Isolated SAME-PROCESS diagnostic fixture, not a game FPS benchmark.
// Calls real IDirect3DDevice9[Ex]::Present through the registered Hook.
#include <windows.h>
#include <d3d9.h>
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <cstring>
#include <filesystem>
#include <fstream>
#include <iomanip>
#include <iostream>
#include <vector>
#include "../include/d3d9_present_samples.h"

using namespace duluka::diagnostics;
using BeginSamples = BOOL(WINAPI *)(DWORD);
using StopSamples = BOOL(WINAPI *)();
using ReadSamples = BOOL(WINAPI *)(D3D9PresentSamplesSnapshotV1*, DWORD,
    D3D9PresentSampleV1*, DWORD);
using RegisterDevice = BOOL(WINAPI *)(IDirect3DDevice9*);
using GetStatus = BOOL(WINAPI *)(std::uint64_t*, std::uint64_t*, BOOL*);

namespace
{
    bool Check(bool pass, const char* message)
    {
        std::cout << (pass ? "PASS " : "FAIL ") << message << '\n';
        return pass;
    }

    bool ValidSnapshot(const D3D9PresentSamplesSnapshotV1& snapshot,
        const std::vector<D3D9PresentSampleV1>& samples, std::uint32_t kind)
    {
        if (snapshot.size != sizeof(snapshot) ||
            snapshot.version != D3D9PresentSamplesVersion ||
            !snapshot.performanceFrequency || !snapshot.sessionId ||
            snapshot.beganQpc > snapshot.stoppedQpc || snapshot.flags ||
            snapshot.sampleCount > snapshot.capacity ||
            snapshot.attemptedCalls != snapshot.sampleCount + snapshot.busyDrops +
                snapshot.overflowDrops + snapshot.reentrantExcluded +
                snapshot.unfinishedOrStoppedCalls)
            return false;
        for (std::uint32_t i = 0; i < snapshot.sampleCount; ++i)
        {
            const auto& sample = samples[i];
            if (sample.entryQpc < snapshot.beganQpc ||
                sample.entryQpc > sample.beforeOriginalQpc ||
                sample.beforeOriginalQpc > sample.afterOriginalQpc ||
                sample.afterOriginalQpc > snapshot.stoppedQpc ||
                sample.threadId != GetCurrentThreadId() ||
                sample.presentKind != kind || sample.reserved)
                return false;
        }
        return true;
    }

    void PrintDistribution(const char* label, std::vector<double> values)
    {
        std::sort(values.begin(), values.end());
        const auto percentile = [&](double fraction) {
            const auto rank = static_cast<std::size_t>(
                std::ceil(fraction * static_cast<double>(values.size())));
            return values[(std::max)(std::size_t{1}, rank) - 1];
        };
        std::cout << std::fixed << std::setprecision(3) << label
            << " us p50=" << percentile(0.50)
            << " p95=" << percentile(0.95)
            << " p99=" << percentile(0.99)
            << " max=" << values.back() << '\n';
    }
}

int wmain(int argc, wchar_t** argv)
{
    if ((argc != 3 && argc != 4) ||
        (argc == 4 && std::wcscmp(argv[3], L"--ex") != 0))
    {
        std::cerr << "Usage: fixture <isolated-hook.dll> <new-samples.csv> [--ex]\n";
        return 2;
    }
    const bool useEx = argc == 4;
    if (std::filesystem::exists(argv[2]))
    {
        std::cerr << "Refusing to overwrite existing evidence CSV\n";
        return 3;
    }
    const auto instance = GetModuleHandleW(nullptr);
    WNDCLASSW windowClass{};
    windowClass.hInstance = instance;
    windowClass.lpfnWndProc = DefWindowProcW;
    windowClass.lpszClassName = L"DulukaD3D9PresentSamplesFixture";
    if (!RegisterClassW(&windowClass)) return 4;
    const auto window = CreateWindowExW(0, windowClass.lpszClassName,
        L"Duluka isolated Present diagnostics", WS_OVERLAPPED, 0, 0, 96, 96,
        nullptr, nullptr, instance, nullptr);
    if (!window) return 5;
    IDirect3D9* factory = nullptr;
    IDirect3D9Ex* factoryEx = nullptr;
    if (useEx)
    {
        if (FAILED(Direct3DCreate9Ex(D3D_SDK_VERSION, &factoryEx))) return 6;
        factory = factoryEx;
    }
    else factory = Direct3DCreate9(D3D_SDK_VERSION);
    if (!factory) return 6;
    D3DPRESENT_PARAMETERS parameters{};
    parameters.BackBufferWidth = 64;
    parameters.BackBufferHeight = 64;
    parameters.BackBufferCount = 1;
    parameters.BackBufferFormat = D3DFMT_UNKNOWN;
    parameters.Windowed = TRUE;
    parameters.SwapEffect = D3DSWAPEFFECT_DISCARD;
    parameters.hDeviceWindow = window;
    parameters.PresentationInterval = D3DPRESENT_INTERVAL_IMMEDIATE;
    IDirect3DDevice9* device = nullptr;
    IDirect3DDevice9Ex* deviceEx = nullptr;
    const auto createResult = useEx
        ? factoryEx->CreateDeviceEx(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window,
            D3DCREATE_SOFTWARE_VERTEXPROCESSING, &parameters, nullptr, &deviceEx)
        : factory->CreateDevice(D3DADAPTER_DEFAULT, D3DDEVTYPE_HAL, window,
            D3DCREATE_SOFTWARE_VERTEXPROCESSING, &parameters, &device);
    if (useEx) device = deviceEx;
    if (FAILED(createResult) || !device) return 7;

    // Keep this module loaded for process lifetime. Unregister the exact
    // cooperative device before release; this is not an arbitrary-unload test.
    const auto hook = LoadLibraryW(argv[1]);
    if (!hook) return 8;
    const auto begin = reinterpret_cast<BeginSamples>(
        GetProcAddress(hook, "NvOscAdapterBeginD3D9PresentSamples"));
    const auto stop = reinterpret_cast<StopSamples>(
        GetProcAddress(hook, "NvOscAdapterStopD3D9PresentSamples"));
    const auto read = reinterpret_cast<ReadSamples>(
        GetProcAddress(hook, "NvOscAdapterReadD3D9PresentSamples"));
    const auto registerDevice = reinterpret_cast<RegisterDevice>(
        GetProcAddress(hook, "NvOscAdapterRegisterD3D9Device"));
    const auto unregisterDevice = reinterpret_cast<RegisterDevice>(
        GetProcAddress(hook, "NvOscAdapterUnregisterD3D9Device"));
    const auto status = reinterpret_cast<GetStatus>(
        GetProcAddress(hook, "NvOscAdapterGetD3D9Status"));
    if (!Check(begin && stop && read && registerDevice && unregisterDevice &&
            status, "all additive diagnostic and cooperative exports present"))
        return 9;
    std::uint64_t count = 0, draws = 0;
    BOOL installed = FALSE;
    for (unsigned i = 0; i < 250; ++i)
    {
        status(&count, &draws, &installed);
        if (installed) break;
        Sleep(20);
    }
    if (!Check(installed && registerDevice(device),
            "real pre-existing device registered")) return 10;
    const auto present = [&] {
        return useEx ? deviceEx->PresentEx(nullptr, nullptr, nullptr, nullptr, 0)
            : device->Present(nullptr, nullptr, nullptr, nullptr);
    };
    constexpr DWORD Capacity = 128;
    std::vector<D3D9PresentSampleV1> samples(Capacity);
    const auto bytes = static_cast<DWORD>(samples.size() * sizeof(samples[0]));
    D3D9PresentSamplesSnapshotV1 snapshot{};
    bool pass = Check(!read(&snapshot, sizeof(snapshot), samples.data(), bytes),
        "disabled by default; no fabricated stopped session");
    pass &= Check(SUCCEEDED(present()) && !stop(),
        "disabled Present forwards without starting recorder");
    pass &= Check(!begin(0) && !begin(D3D9PresentSamplesCapacity + 1),
        "invalid capacities rejected");
    if (!begin(Capacity)) return 11;
    pass &= Check(!begin(Capacity) &&
        !read(&snapshot, sizeof(snapshot), samples.data(), bytes),
        "active Begin and active Read rejected");
    std::uint64_t before = 0;
    status(&before, &draws, &installed);
    std::vector<HRESULT> results;
    constexpr unsigned Calls = 96;
    for (unsigned i = 0; i < Calls; ++i) results.push_back(present());
    status(&count, &draws, &installed);
    if (!stop() || !read(&snapshot, sizeof(snapshot), samples.data(), bytes))
        return 12;
    const auto kind = useEx ? D3D9PresentExKind : D3D9PresentKind;
    pass &= Check(count - before == Calls && snapshot.sampleCount == Calls &&
        snapshot.attemptedCalls == Calls && !snapshot.busyDrops &&
        !snapshot.overflowDrops && !snapshot.reentrantExcluded &&
        !snapshot.unfinishedOrStoppedCalls &&
        ValidSnapshot(snapshot, samples, kind),
        "actual intercepted Present count, timestamps, thread/API and accounting");
    for (unsigned i = 0; i < Calls; ++i)
        pass &= samples[i].result == results[i] && SUCCEEDED(results[i]);
    pass &= Check(pass, "Original Present HRESULT preserved per sample");
    pass &= Check(!read(&snapshot, sizeof(snapshot) - 1, samples.data(), bytes) &&
        !read(&snapshot, sizeof(snapshot), samples.data(), bytes - 1) &&
        !read(&snapshot, sizeof(snapshot), nullptr, bytes) &&
        !read(&snapshot, sizeof(snapshot), samples.data(), 0) && !stop(),
        "malformed/short buffers and repeated Stop rejected");
    const auto firstSnapshot = snapshot;
    const auto firstSamples = samples;
    pass &= Check(SUCCEEDED(present()) &&
        read(&snapshot, sizeof(snapshot), samples.data(), bytes) &&
        std::memcmp(&firstSnapshot, &snapshot, sizeof(snapshot)) == 0 &&
        std::memcmp(firstSamples.data(), samples.data(), bytes) == 0,
        "stopped snapshot remains frozen across disabled Presents");

    // Export caller-side distributions and immutable raw fixture evidence.
    std::ofstream csv{std::filesystem::path(argv[2])};
    csv << "session,entry_qpc,before_original_qpc,after_original_qpc,thread,kind,hresult\n";
    std::vector<double> overhead, original;
    for (std::uint32_t i = 0; i < firstSnapshot.sampleCount; ++i)
    {
        const auto& sample = firstSamples[i];
        const auto scale = 1000000.0 /
            static_cast<double>(firstSnapshot.performanceFrequency);
        overhead.push_back(static_cast<double>(
            sample.beforeOriginalQpc - sample.entryQpc) * scale);
        original.push_back(static_cast<double>(
            sample.afterOriginalQpc - sample.beforeOriginalQpc) * scale);
        csv << firstSnapshot.sessionId << ',' << sample.entryQpc << ','
            << sample.beforeOriginalQpc << ',' << sample.afterOriginalQpc << ','
            << sample.threadId << ',' << sample.presentKind << ','
            << sample.result << '\n';
    }
    csv.close();
    pass &= Check(csv.good(), "new per-sample CSV written without overwriting");
    if (!overhead.empty())
    {
        PrintDistribution("FIXTURE Hook entry-to-Original", overhead);
        PrintDistribution("FIXTURE Original Present only", original);
    }
    if (!begin(8)) return 13;
    for (unsigned i = 0; i < 20; ++i) pass &= SUCCEEDED(present());
    if (!stop() || !read(&snapshot, sizeof(snapshot), samples.data(), bytes))
        return 14;
    pass &= Check(snapshot.sessionId > firstSnapshot.sessionId &&
        snapshot.sampleCount == 8 && snapshot.attemptedCalls == 20 &&
        snapshot.overflowDrops == 12 && !snapshot.busyDrops &&
        !snapshot.unfinishedOrStoppedCalls && ValidSnapshot(snapshot, samples, kind),
        "bounded capture retains first 8 and counts 12 overflow drops");
    const auto previousSession = snapshot.sessionId;
    if (!begin(Capacity) || FAILED(present()) || !stop() ||
        !read(&snapshot, sizeof(snapshot), samples.data(), bytes)) return 15;
    pass &= Check(snapshot.sessionId > previousSession &&
        snapshot.sampleCount == 1 && snapshot.attemptedCalls == 1 &&
        !snapshot.overflowDrops && ValidSnapshot(snapshot, samples, kind),
        "restart resets buffer/counters with a distinct generation");
    pass &= Check(unregisterDevice(device), "cooperative device restored before release");
    device->Release();
    factory->Release();
    DestroyWindow(window);
    std::cout << "UNVERIFIED: game FPS/1% low/display pacing; fixture is not game evidence.\n";
    return pass ? 0 : 16;
}
