#include <windows.h>
#include <reshade.hpp>
#include "FilterSession.h"
#include <array>
#include <filesystem>
#include <map>
#include <memory>
#include <mutex>

namespace {
using namespace ansel;
namespace api = reshade::api;
constexpr size_t MaxRequestWire = 1024 * 1024;
constexpr size_t MaxReplyWire = 16 * 1024 * 1024;

struct ReShadeRuntime final : Runtime {
    api::effect_runtime *runtime;
    std::map<std::string, api::effect_technique> techniques;
    std::map<std::string, api::effect_uniform_variable> uniforms;
    std::map<std::string, Json> defaults;
    explicit ReShadeRuntime(api::effect_runtime *r) : runtime(r) {}
    Json catalog() override {
        techniques.clear(); uniforms.clear();
        Json filters = Json::array();
        runtime->enumerate_techniques(nullptr, [&](auto *r, api::effect_technique technique) {
            char effect[4096]{}, name[1024]{}, label[1024]{};
            r->get_technique_effect_name(technique, effect);
            r->get_technique_name(technique, name);
            r->get_annotation_string_from_technique(technique, "ui_label", label);
            const auto id = Json::array({effect, name}).dump();
            techniques.emplace(id, technique);
            Json controls = Json::array();
            r->enumerate_uniform_variables(effect, [&](auto *owner, api::effect_uniform_variable uniform) {
                char uniformName[1024]{}, title[1024]{}, source[256]{};
                bool hidden = false;
                owner->get_annotation_bool_from_uniform_variable(uniform, "hidden", &hidden, 1);
                if (!hidden) owner->get_annotation_bool_from_uniform_variable(uniform, "ui_hidden", &hidden, 1);
                owner->get_annotation_string_from_uniform_variable(uniform, "source", source);
                if (hidden || source[0]) return; // Engine-owned values are read-only.
                api::format format; uint32_t rows = 0, columns = 0, length = 0;
                owner->get_uniform_variable_type(uniform, &format, &rows, &columns, &length);
                const size_t count = static_cast<size_t>(rows) * columns;
                if (!count || count > 4 || length > 1 || columns > 1) return;
                owner->get_uniform_variable_name(uniform, uniformName);
                owner->get_annotation_string_from_uniform_variable(uniform, "ui_label", title);
                Json values = Json::array(), minimum = Json::array(), maximum = Json::array();
                std::string type;
                bool bounded = false;
                if (format == api::format::r32_float) {
                    type = "float";
                    float v[4]{}, lo[4]{-1e30f,-1e30f,-1e30f,-1e30f}, hi[4]{1e30f,1e30f,1e30f,1e30f};
                    owner->get_uniform_value_float(uniform, v, count);
                    const bool hasMin = owner->get_annotation_float_from_uniform_variable(uniform, "ui_min", lo, count);
                    const bool hasMax = owner->get_annotation_float_from_uniform_variable(uniform, "ui_max", hi, count);
                    if (!hasMin) std::fill(std::begin(lo),std::end(lo),-1e30f);
                    if (!hasMax) std::fill(std::begin(hi),std::end(hi),1e30f);
                    bounded = hasMin && hasMax;
                    for (size_t i=0;i<count;++i) if (!std::isfinite(v[i])) return;
                    for (size_t i=0; i<count; ++i) { values.push_back(v[i]); minimum.push_back(lo[i]); maximum.push_back(hi[i]); }
                } else if (format == api::format::r32_sint) {
                    type = "int"; int32_t v[4]{}, lo[4]{INT32_MIN,INT32_MIN,INT32_MIN,INT32_MIN}, hi[4]{INT32_MAX,INT32_MAX,INT32_MAX,INT32_MAX};
                    owner->get_uniform_value_int(uniform, v, count);
                    const bool hasMin = owner->get_annotation_int_from_uniform_variable(uniform, "ui_min", lo, count);
                    const bool hasMax = owner->get_annotation_int_from_uniform_variable(uniform, "ui_max", hi, count);
                    if (!hasMin) std::fill(std::begin(lo),std::end(lo),INT32_MIN);
                    if (!hasMax) std::fill(std::begin(hi),std::end(hi),INT32_MAX);
                    bounded = hasMin && hasMax;
                    for (size_t i=0; i<count; ++i) { values.push_back(v[i]); minimum.push_back(lo[i]); maximum.push_back(hi[i]); }
                } else if (format == api::format::r32_uint) {
                    type = "uint"; uint32_t v[4]{}, lo[4]{}, hi[4]{UINT32_MAX,UINT32_MAX,UINT32_MAX,UINT32_MAX};
                    owner->get_uniform_value_uint(uniform, v, count);
                    const bool hasMin = owner->get_annotation_uint_from_uniform_variable(uniform, "ui_min", lo, count);
                    const bool hasMax = owner->get_annotation_uint_from_uniform_variable(uniform, "ui_max", hi, count);
                    if (!hasMin) std::fill(std::begin(lo),std::end(lo),0u);
                    if (!hasMax) std::fill(std::begin(hi),std::end(hi),UINT32_MAX);
                    bounded = hasMin && hasMax;
                    for (size_t i=0; i<count; ++i) { values.push_back(v[i]); minimum.push_back(lo[i]); maximum.push_back(hi[i]); }
                } else if (format == api::format::r32_typeless) {
                    type = "bool"; bool v[4]{};
                    owner->get_uniform_value_bool(uniform, v, count);
                    for (size_t i=0; i<count; ++i) { values.push_back(v[i]); minimum.push_back(0); maximum.push_back(1); }
                } else return;
                const auto controlId = std::string(uniformName);
                uniforms[id + "\n" + controlId] = uniform;
                const auto defaultKey = id + "\n" + controlId;
                if (!defaults.count(defaultKey)) defaults[defaultKey] = values;
                // Controls without a bounded annotation use numeric edits rather
                // than presenting a fabricated slider range.
                char uiType[64]{};
                owner->get_annotation_string_from_uniform_variable(uniform, "ui_type", uiType);
                if (std::string(uiType) == "text") return;
                const bool slider = bounded && (std::string(uiType) == "slider" || std::string(uiType) == "drag");
                float step[4]{0.01f,0.01f,0.01f,0.01f};
                owner->get_annotation_float_from_uniform_variable(uniform, "ui_step", step, count);
                Json steps = Json::array();
                for (size_t i=0; i<count; ++i) steps.push_back(type == "float" ? (step[i] > 0 ? step[i] : 0.01f) : 1.0f);
                controls.push_back({{"id",controlId}, {"displayName",title[0] ? title : uniformName},
                    {"type",type == "bool" ? "boolean" : slider ? "slider" : "edit"},
                    {"dataType",type}, {"currentValue",values}, {"defaultValue",defaults.at(defaultKey)},
                    {"minValue",minimum}, {"maxValue",maximum}, {"stepSize",steps},
                    {"uiMinValueV1",minimum}, {"uiMaxValueV1",maximum}});
            });
            filters.push_back({{"id",id}, {"name",label[0] ? label : name}, {"effect",effect},
                {"enabled",r->get_technique_state(technique)}, {"controls",controls}});
        });
        return filters;
    }
    void enable(const std::string &id, bool enabled) override {
        if (enabled) runtime->set_effects_state(true);
        runtime->set_technique_state(techniques.at(id), enabled);
        if (runtime->get_technique_state(techniques.at(id)) != enabled ||
            (enabled && !runtime->get_effects_state()))
            throw Failure(409,"RUNTIME_REJECTED","ReShade did not apply the requested effect state");
    }
    void value(const std::string &id, const std::string &control, const Json &values) override {
        const auto uniform = uniforms.at(id + "\n" + control);
        api::format format; runtime->get_uniform_variable_type(uniform, &format);
        const auto count = values.size();
        if (format == api::format::r32_float) { float v[4]{}; for(size_t i=0;i<count;++i) v[i]=values[i].get<float>(); runtime->set_uniform_value_float(uniform, v, count); }
        else if (format == api::format::r32_sint) { int32_t v[4]{}; for(size_t i=0;i<count;++i) v[i]=values[i].get<int32_t>(); runtime->set_uniform_value_int(uniform, v, count); }
        else if (format == api::format::r32_uint) { uint32_t v[4]{}; for(size_t i=0;i<count;++i) v[i]=values[i].get<uint32_t>(); runtime->set_uniform_value_uint(uniform, v, count); }
        else { bool v[4]{}; for(size_t i=0;i<count;++i) v[i]=values[i].get<bool>(); runtime->set_uniform_value_bool(uniform, v, count); }
    }
    Json read_value(const std::string &id, const std::string &control) override {
        const auto uniform = uniforms.at(id + "\n" + control);
        api::format format; uint32_t rows = 0, columns = 0, length = 0;
        runtime->get_uniform_variable_type(uniform, &format, &rows, &columns, &length);
        const size_t count = static_cast<size_t>(rows) * columns;
        if (!count || count > 4 || length > 1 || columns > 1)
            throw Failure(409, "RUNTIME_REJECTED", "Uniform shape changed during write");
        Json result = Json::array();
        if (format == api::format::r32_float) {
            float values[4]{}; runtime->get_uniform_value_float(uniform, values, count);
            for (size_t i=0;i<count;++i) result.push_back(values[i]);
        } else if (format == api::format::r32_sint) {
            int32_t values[4]{}; runtime->get_uniform_value_int(uniform, values, count);
            for (size_t i=0;i<count;++i) result.push_back(values[i]);
        } else if (format == api::format::r32_uint) {
            uint32_t values[4]{}; runtime->get_uniform_value_uint(uniform, values, count);
            for (size_t i=0;i<count;++i) result.push_back(values[i]);
        } else if (format == api::format::r32_typeless) {
            bool values[4]{}; runtime->get_uniform_value_bool(uniform, values, count);
            for (size_t i=0;i<count;++i) result.push_back(values[i]);
        } else throw Failure(409, "RUNTIME_REJECTED", "Uniform type changed during write");
        return result;
    }
    void reorder(const std::vector<std::string> &ids) override {
        std::vector<api::effect_technique> order;
        for (const auto &id : ids) order.push_back(techniques.at(id));
        // ReShade requires a complete permutation, including disabled techniques.
        runtime->enumerate_techniques(nullptr, [&](auto *, api::effect_technique t) {
            if (std::none_of(order.begin(), order.end(), [&](auto existing) { return existing.handle == t.handle; })) order.push_back(t);
        });
        if (!order.empty()) runtime->reorder_techniques(order.size(), order.data());
    }
};

// Overlapped IO is serviced once per present. No pipe worker may retain a runtime
// pointer or call ReShade off-thread; a paused game simply times out at the client.
class Pipe {
    enum class Phase { Connect, Read, Write } phase_ = Phase::Connect;
    HANDLE pipe_ = INVALID_HANDLE_VALUE;
    OVERLAPPED io_{};
    bool pending_ = false;
    std::array<char, 16384> buffer_{};
    std::string input_, output_;
    ULONGLONG touched_ = 0;
    void reset() {
        if (pending_) { CancelIoEx(pipe_, &io_); DWORD ignored; GetOverlappedResult(pipe_, &io_, &ignored, TRUE); }
        pending_ = false; DisconnectNamedPipe(pipe_);
        phase_ = Phase::Connect; input_.clear(); output_.clear();
    }
public:
    explicit Pipe(unsigned serial) {
        const auto name = L"\\\\.\\pipe\\NVIDIAAnsel." + std::to_wstring(GetCurrentProcessId()) + L"." + std::to_wstring(serial);
        io_.hEvent = CreateEventW(nullptr, TRUE, FALSE, nullptr);
        pipe_ = CreateNamedPipeW(name.c_str(), PIPE_ACCESS_DUPLEX | FILE_FLAG_OVERLAPPED | FILE_FLAG_FIRST_PIPE_INSTANCE,
            PIPE_TYPE_BYTE | PIPE_READMODE_BYTE | PIPE_WAIT | PIPE_REJECT_REMOTE_CLIENTS, 1, 65536, 65536, 0, nullptr);
        if (pipe_ == INVALID_HANDLE_VALUE || !io_.hEvent) throw std::runtime_error("Cannot create Ansel named pipe");
    }
    ~Pipe() {
        if (pipe_ != INVALID_HANDLE_VALUE) { reset(); CloseHandle(pipe_); }
        if (io_.hEvent) CloseHandle(io_.hEvent);
    }
    template<class Dispatch> void tick(Dispatch dispatch) {
        if (pending_ && GetTickCount64() - touched_ > 3000) { reset(); return; }
        DWORD transferred = 0;
        if (pending_) {
            if (!GetOverlappedResult(pipe_, &io_, &transferred, FALSE)) {
                if (GetLastError() == ERROR_IO_INCOMPLETE) return;
                reset(); return;
            }
            pending_ = false;
        } else {
            ResetEvent(io_.hEvent);
            BOOL done;
            if (phase_ == Phase::Connect) {
                done = ConnectNamedPipe(pipe_, &io_);
                if (!done && GetLastError() == ERROR_PIPE_CONNECTED) done = TRUE;
            } else if (phase_ == Phase::Read) done = ReadFile(pipe_, buffer_.data(), static_cast<DWORD>(buffer_.size()), &transferred, &io_);
            else done = WriteFile(pipe_, output_.data(), static_cast<DWORD>(output_.size()), &transferred, &io_);
            if (!done) {
                if (GetLastError() == ERROR_IO_PENDING) { pending_ = true; touched_ = GetTickCount64(); return; }
                reset(); return;
            }
        }
        if (phase_ == Phase::Connect) phase_ = Phase::Read;
        else if (phase_ == Phase::Read) {
            if (!transferred) { reset(); return; }
            input_.append(buffer_.data(), transferred);
            if (input_.size() > MaxRequestWire) { reset(); return; }
            const auto end = input_.find('\n');
            if (end != std::string::npos) {
                try { output_ = dispatch(Json::parse(input_.substr(0, end))).dump(); }
                catch (...) { output_ = R"({"ok":false,"status":400,"code":"INVALID_JSON","message":"Invalid adapter command"})"; }
                if (output_.size() > MaxReplyWire) output_ = R"({"ok":false,"status":413,"code":"CATALOG_TOO_LARGE","message":"Effect catalog exceeds the 16 MiB response limit"})";
                output_ += '\n'; phase_ = Phase::Write;
            }
        } else {
            if (transferred < output_.size()) output_.erase(0, transferred);
            else { phase_ = Phase::Read; input_.clear(); output_.clear(); }
        }
    }
};

struct Context {
    ReShadeRuntime source;
    FilterSession session;
    Pipe pipe;
    Context(api::effect_runtime *runtime, unsigned serial)
        : source(runtime), session(source, GetCurrentProcessId(), std::to_string(GetCurrentProcessId()) + ":" +
            std::to_string(GetTickCount64()) + ":" + std::to_string(serial)), pipe(serial) {}
};
std::mutex contextsMutex;
std::map<api::effect_runtime *, std::unique_ptr<Context>> contexts;
unsigned nextSerial = 0;
bool registered = false;

void Present(api::effect_runtime *runtime) {
    if (runtime->get_device()->get_api() != api::device_api::d3d11) return;
    std::lock_guard lock(contextsMutex);
    try {
        auto &context = contexts[runtime];
        if (!context) context = std::make_unique<Context>(runtime, ++nextSerial);
        const auto dispatch = [&](const Json &request) {
            auto reply = context->session.dispatch(request);
            if (reply.value("ok", false)) {
                auto &state = reply["state"];
                const HWND window = static_cast<HWND>(runtime->get_hwnd());
                state["foreground"] = GetAncestor(GetForegroundWindow(), GA_ROOT) == GetAncestor(window, GA_ROOT);
                state["api"] = "D3D11";
                state["effectsEnabled"] = runtime->get_effects_state();
                wchar_t executable[32768]{}; GetModuleFileNameW(nullptr, executable, 32768);
                state["executable"] = std::filesystem::path(executable).u8string();
            }
            return reply;
        };
        // Advance ready overlapped phases within this frame, instead of adding
        // a whole display interval for connect, read and reply individually.
        // The fixed budget keeps an active editor from monopolizing rendering.
        for (unsigned ioStep=0; ioStep<4; ++ioStep) context->pipe.tick(dispatch);
    } catch (const std::exception &error) { reshade::log_message(reshade::log_level::error, error.what()); }
}
void Destroy(api::effect_runtime *runtime) { std::lock_guard lock(contextsMutex); contexts.erase(runtime); }
void Reload(api::effect_runtime *runtime) {
    std::lock_guard lock(contextsMutex);
    if (auto found = contexts.find(runtime); found != contexts.end()) {
        found->second->source.defaults.clear();
        found->second->session.reload();
    }
}
}

extern "C" __declspec(dllexport) BOOL WINAPI StartNVIDIAAnselAdapter(HMODULE module) {
    if (registered) return TRUE;
    if (!reshade::register_addon(module)) return FALSE;
    reshade::register_event<reshade::addon_event::reshade_present>(&Present);
    reshade::register_event<reshade::addon_event::destroy_effect_runtime>(&Destroy);
    reshade::register_event<reshade::addon_event::reshade_reloaded_effects>(&Reload);
    // The injected adapter has process lifetime. No thread or callback can run
    // out of an unloaded DLL; runtime resources still close on destroy events.
    HMODULE pinned = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_PIN,
        reinterpret_cast<LPCWSTR>(&StartNVIDIAAnselAdapter), &pinned);
    registered = true;
    reshade::log_message(reshade::log_level::info, "Ansel Adapter: live D3D11 filter bridge registered (protocol 1).");
    return TRUE;
}
extern "C" __declspec(dllexport) const char *NAME = "NVIDIA Ansel Adapter";
extern "C" __declspec(dllexport) const char *DESCRIPTION = "OSC Game Filter bridge for D3D11 ReShade runtimes";
BOOL WINAPI DllMain(HMODULE module, DWORD reason, LPVOID) {
    if (reason == DLL_PROCESS_ATTACH) {
        DisableThreadLibraryCalls(module);
        // ReShade's supported add-on initialization path. No worker threads,
        // waits, runtime enumeration or GPU work run under the loader lock.
        return StartNVIDIAAnselAdapter(module);
    }
    return TRUE;
}
