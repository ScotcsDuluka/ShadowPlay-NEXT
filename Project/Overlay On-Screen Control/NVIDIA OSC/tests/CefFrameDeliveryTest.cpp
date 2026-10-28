// Isolated CEF 73 OSR producer probe. No game/Hook/OSC endpoint or visible UI.
// Counts actual callbacks; does not equate them with unique/displayed images.
#define NOMINMAX
#define WIN32_LEAN_AND_MEAN
#include <windows.h>
#include <d3d11_1.h>
#include "include/cef_app.h"
#include "include/cef_client.h"
#include "include/cef_browser.h"
#include "include/cef_task.h"
#include "include/wrapper/cef_helpers.h"
#include "../src/OscDesktopFps.h"
#include "../src/OscModeFrameBoost.h"
#include <array>
#include <atomic>
#include <filesystem>
#include <fstream>
#include <functional>
#include <iostream>
#include <mutex>
#include <vector>

namespace {
std::uint64_t Now() { LARGE_INTEGER q{}; QueryPerformanceCounter(&q); return static_cast<std::uint64_t>(q.QuadPart); }
class Task : public CefTask {
public:
    explicit Task(std::function<void()> action):action_(std::move(action)){}
    void Execute() override { action_(); }
private:
    std::function<void()> action_;
    IMPLEMENT_REFCOUNTING(Task);
};
struct Phase { int requested; int milliseconds; const char* label; std::uint64_t start=0,end=0; int reported=0; };
struct Sample { int phase; bool gpu; std::uint64_t qpc; bool handle; bool pixelRead=false; std::uint32_t pixelSerial=0; };
class Probe : public CefApp, public CefBrowserProcessHandler, public CefClient,
              public CefRenderHandler, public CefLifeSpanHandler {
public:
    CefRefPtr<CefBrowserProcessHandler> GetBrowserProcessHandler() override { return this; }
    CefRefPtr<CefRenderHandler> GetRenderHandler() override { return this; }
    CefRefPtr<CefLifeSpanHandler> GetLifeSpanHandler() override { return this; }
    void OnBeforeCommandLineProcessing(const CefString&,CefRefPtr<CefCommandLine> command) override {
        command->AppendSwitch("enable-gpu");
        command->AppendSwitch("force-gpu-rasterization");
        command->AppendSwitchWithValue("use-angle","d3d11");
        command->AppendSwitchWithValue("disable-features","VizDisplayCompositor");
        command->AppendSwitch("disable-background-timer-throttling");
        command->AppendSwitch("disable-renderer-backgrounding");
        command->AppendSwitch("disable-backgrounding-occluded-windows");
    }
    void Prepare(const std::filesystem::path& directory,bool timerDriven) {
        directory_=directory;
        samples_.reserve(65536);
        done_=CreateEventW(nullptr,TRUE,FALSE,nullptr);
        WNDCLASSW windowClass{};
        windowClass.lpfnWndProc=DefWindowProcW;
        windowClass.hInstance=GetModuleHandleW(nullptr);
        windowClass.lpszClassName=L"DulukaCefFrameProbeInvisible";
        RegisterClassW(&windowClass);
        window_=CreateWindowExW(0,windowClass.lpszClassName,L"CEF frame probe (hidden)",
            WS_POPUP,0,0,640,360,nullptr,nullptr,windowClass.hInstance,nullptr);
        std::ofstream html(directory_/"animation.html");
        html << "<!doctype html><style>html,body{margin:0;width:100%;height:100%;overflow:hidden}</style>"
                "<script>let n=0;function tick(){++n;document.body.style.backgroundColor="
                "'rgb('+(n&255)+','+((n>>8)&255)+','+((n>>16)&255)+')';";
        if(timerDriven) html << "}addEventListener('load',()=>setInterval(tick,4));</script>";
        else html << "requestAnimationFrame(tick)}addEventListener('load',()=>requestAnimationFrame(tick));</script>";
    }
    HANDLE Done() const { return done_; }
    void OnContextInitialized() override {
        CefWindowInfo window;
        window.SetAsWindowless(window_);
        window.shared_texture_enabled=true;
        window.external_begin_frame_enabled=false;
        CefBrowserSettings settings;
        settings.windowless_frame_rate=osc::CefDesktopFps(75,true);
        const auto url=L"file:///"+(directory_/"animation.html").generic_wstring();
        browser_=CefBrowserHost::CreateBrowserSync(window,this,url,settings,nullptr);
        if(!browser_) { SetEvent(done_); return; }
        browser_->GetHost()->WasHidden(false);
        CefRefPtr<Probe> self=this;
        CefPostDelayedTask(TID_UI,new Task([self]{self->ConfigureNext();}),3000);
    }
    void GetViewRect(CefRefPtr<CefBrowser>,CefRect& rect) override { rect=CefRect(0,0,640,360); }
    void OnPaint(CefRefPtr<CefBrowser>,PaintElementType type,const RectList&,const void*,int,int) override {
        if(type==PET_VIEW) Record(false,false);
    }
    void OnAcceleratedPaint(CefRefPtr<CefBrowser>,PaintElementType type,const RectList&,void* handle) override {
        if(type==PET_VIEW) {
            const auto index=Record(true,handle!=nullptr);
            PollPixels();
            if(handle) QueuePixel(handle,index);
        }
    }
    void OnBeforeClose(CefRefPtr<CefBrowser>) override { browser_=nullptr; SetEvent(done_); }
    void ConfigureNext() {
        CEF_REQUIRE_UI_THREAD();
        if(next_==phases_.size()) { browser_->GetHost()->CloseBrowser(true); return; }
        browser_->GetHost()->SetWindowlessFrameRate(phases_[next_].requested);
        phases_[next_].reported=browser_->GetHost()->GetWindowlessFrameRate();
        CefRefPtr<Probe> self=this;
        CefPostDelayedTask(TID_UI,new Task([self]{self->StartPhase();}),500);
    }
    void StartPhase() {
        phases_[next_].start=Now();
        active_.store(static_cast<int>(next_),std::memory_order_release);
        std::cout << "START " << phases_[next_].label << " requested=" << phases_[next_].requested
                  << " getter=" << phases_[next_].reported << std::endl;
        CefRefPtr<Probe> self=this;
        CefPostDelayedTask(TID_UI,new Task([self]{self->EndPhase();}),phases_[next_].milliseconds);
    }
    void EndPhase() {
        active_.store(-1,std::memory_order_release);
        phases_[next_].end=Now();
        DrainPixels(); // outside the measured phase, before the next warm-up
        std::cout << "END " << phases_[next_].label << std::endl;
        ++next_; ConfigureNext();
    }
    bool WriteEvidence() {
        LARGE_INTEGER frequency{}; QueryPerformanceFrequency(&frequency);
        std::lock_guard<std::mutex> lock(mutex_);
        std::ofstream raw(directory_/"callbacks.csv");
        raw << "phase,gpu,qpc,handle_valid,pixel_read,pixel_serial\n";
        for(const auto& sample:samples_) raw << sample.phase << ',' << sample.gpu << ',' << sample.qpc << ',' << sample.handle
            << ',' << sample.pixelRead << ',' << sample.pixelSerial << '\n';
        std::ofstream phases(directory_/"phases.csv");
        phases << "phase,label,requested,getter,start_qpc,end_qpc,frequency,gpu_callbacks,cpu_callbacks,storage_overflow\n";
        bool complete=true;
        for(std::size_t i=0;i<phases_.size();++i) {
            std::uint64_t gpu=0,cpu=0;
            for(const auto& sample:samples_) if(sample.phase==static_cast<int>(i)) { if(sample.gpu) ++gpu; else ++cpu; }
            const auto& phase=phases_[i];
            phases << i << ',' << phase.label << ',' << phase.requested << ',' << phase.reported << ',' << phase.start << ',' << phase.end
                   << ',' << frequency.QuadPart << ',' << gpu << ',' << cpu << ',' << overflow_ << '\n';
            complete &= phase.end>phase.start && gpu>0 && cpu==0 && !overflow_;
        }
        return complete && raw.good() && phases.good();
    }
    void Cleanup() {
        if(source_) source_->Release();
        for(auto& slot:pixelSlots_) { if(slot.texture) slot.texture->Release(); if(slot.query) slot.query->Release(); }
        if(context_) context_->Release();
        if(device_) device_->Release();
        if(window_) DestroyWindow(window_); if(done_) CloseHandle(done_);
    }
private:
    struct PixelSlot { ID3D11Texture2D* texture=nullptr; ID3D11Query* query=nullptr; bool pending=false; std::size_t sample=SIZE_MAX; };
    void PollPixels() {
        if(!context_) return;
        for(auto& slot:pixelSlots_) {
            if(!slot.pending || context_->GetData(slot.query,nullptr,0,D3D11_ASYNC_GETDATA_DONOTFLUSH)!=S_OK) continue;
            D3D11_MAPPED_SUBRESOURCE mapped{};
            if(context_->Map(slot.texture,0,D3D11_MAP_READ,D3D11_MAP_FLAG_DO_NOT_WAIT,&mapped)!=S_OK) continue;
            const auto* bgra=static_cast<const unsigned char*>(mapped.pData);
            const auto serial=static_cast<std::uint32_t>(bgra[2]) |
                (static_cast<std::uint32_t>(bgra[1])<<8) | (static_cast<std::uint32_t>(bgra[0])<<16);
            context_->Unmap(slot.texture,0);
            if(slot.sample!=SIZE_MAX) {
                std::lock_guard<std::mutex> lock(mutex_);
                samples_[slot.sample].pixelRead=true;
                samples_[slot.sample].pixelSerial=serial;
            }
            slot.pending=false;
        }
    }
    void DrainPixels() {
        if(!context_) return;
        context_->Flush();
        const auto until=GetTickCount64()+200;
        while(GetTickCount64()<until) {
            PollPixels();
            bool pending=false; for(const auto& slot:pixelSlots_) pending|=slot.pending;
            if(!pending) break;
            Sleep(1);
        }
    }
    void QueuePixel(void* handle,std::size_t index) {
        if(!device_) {
            if(FAILED(D3D11CreateDevice(nullptr,D3D_DRIVER_TYPE_HARDWARE,nullptr,D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                    nullptr,0,D3D11_SDK_VERSION,&device_,nullptr,&context_))) return;
        }
        if(!source_ || sourceHandle_!=handle) {
            DrainPixels();
            if(source_) {source_->Release(); source_=nullptr;}
            ID3D11Device1* device1=nullptr;
            if(SUCCEEDED(device_->QueryInterface(__uuidof(ID3D11Device1),reinterpret_cast<void**>(&device1)))) {
                device1->OpenSharedResource1(handle,__uuidof(ID3D11Texture2D),reinterpret_cast<void**>(&source_));
                device1->Release();
            }
            if(!source_) device_->OpenSharedResource(handle,__uuidof(ID3D11Texture2D),reinterpret_cast<void**>(&source_));
            if(!source_) return;
            sourceHandle_=handle;
        }
        D3D11_TEXTURE2D_DESC sourceDescription{}; source_->GetDesc(&sourceDescription);
        if(sourceDescription.Format!=DXGI_FORMAT_B8G8R8A8_UNORM && sourceDescription.Format!=DXGI_FORMAT_B8G8R8A8_TYPELESS) return;
        for(auto& slot:pixelSlots_) {
            if(slot.pending) continue;
            if(!slot.texture) {
                D3D11_TEXTURE2D_DESC description{};
                description.Width=1; description.Height=1; description.MipLevels=1; description.ArraySize=1;
                description.Format=sourceDescription.Format; description.SampleDesc.Count=1;
                description.Usage=D3D11_USAGE_STAGING; description.CPUAccessFlags=D3D11_CPU_ACCESS_READ;
                if(FAILED(device_->CreateTexture2D(&description,nullptr,&slot.texture))) return;
                D3D11_QUERY_DESC query{D3D11_QUERY_EVENT,0};
                if(FAILED(device_->CreateQuery(&query,&slot.query))) return;
            }
            const D3D11_BOX pixel{0,0,0,1,1,1};
            context_->CopySubresourceRegion(slot.texture,0,0,0,0,source_,0,&pixel);
            context_->End(slot.query); context_->Flush();
            slot.pending=true; slot.sample=index;
            return;
        }
    }
    std::size_t Record(bool gpu,bool handle) {
        const auto qpc=Now();
        const auto phase=active_.load(std::memory_order_acquire);
        if(phase<0) return SIZE_MAX;
        std::lock_guard<std::mutex> lock(mutex_);
        if(samples_.size()<65536) {samples_.push_back({phase,gpu,qpc,handle}); return samples_.size()-1;}
        ++overflow_; return SIZE_MAX;
    }
    std::array<Phase,5> phases_{{
        {osc::CefDesktopFps(75,true),10000,"desktop-75"},
        {osc::CefDesktopFps(120,true),10000,"desktop-120"},
        {osc::CefHookFps(true),10000,"hook-source-240"},
        {osc::CefFrameRateMax(true),5000,"boost-240-5s"},
        {osc::CefDesktopFps(75,true),10000,"restore-75"}}};
    std::filesystem::path directory_;
    CefRefPtr<CefBrowser> browser_;
    std::atomic<int> active_{-1};
    std::mutex mutex_;
    std::vector<Sample> samples_;
    std::size_t next_=0;
    std::uint64_t overflow_=0;
    ID3D11Device* device_=nullptr;
    ID3D11DeviceContext* context_=nullptr;
    ID3D11Texture2D* source_=nullptr;
    void* sourceHandle_=nullptr;
    std::array<PixelSlot,16> pixelSlots_{};
    HWND window_=nullptr;
    HANDLE done_=nullptr;
    IMPLEMENT_REFCOUNTING(Probe);
};
}
int wmain(int argc,wchar_t** argv) {
    CefMainArgs args(GetModuleHandleW(nullptr));
    CefRefPtr<Probe> app=new Probe;
    const int subprocess=CefExecuteProcess(args,app,nullptr);
    if(subprocess>=0) return subprocess;
    if(argc<2 || !std::filesystem::is_directory(argv[1])) return 2;
    bool timerDriven=false;
    for(int i=2;i<argc;++i) {
        const std::wstring option=argv[i];
        if(option==L"--timer-animation") timerDriven=true;
        else if(option!=L"--disable-frame-rate-limit" && option!=L"--disable-gpu-vsync") return 2;
    }
    const auto directory=std::filesystem::absolute(argv[1]);
    if(std::filesystem::exists(directory/"callbacks.csv") || std::filesystem::exists(directory/"animation.html")) return 3;
    app->Prepare(directory,timerDriven);
    CefSettings settings;
    settings.no_sandbox=true;
    settings.windowless_rendering_enabled=true;
    settings.multi_threaded_message_loop=true;
    settings.log_severity=LOGSEVERITY_WARNING;
    CefString(&settings.log_file)=(directory/"cef.log").wstring();
    if(!CefInitialize(args,settings,app,nullptr)) return 4;
    const auto deadline=GetTickCount64()+90000;
    const HANDLE doneHandle=app->Done();
    while(WaitForSingleObject(app->Done(),0)!=WAIT_OBJECT_0) {
        if(GetTickCount64()>=deadline) return 5;
        MsgWaitForMultipleObjects(1,&doneHandle,FALSE,100,QS_ALLINPUT);
        MSG message{};
        while(PeekMessageW(&message,nullptr,0,0,PM_REMOVE)) { TranslateMessage(&message); DispatchMessageW(&message); }
    }
    const bool complete=app->WriteEvidence();
    CefShutdown(); app->Cleanup();
    std::cout << "GPU_CALLBACK_DATA_COMPLETE=" << complete << "; unique/displayed frames UNVERIFIED\n";
    return complete?0:6;
}
