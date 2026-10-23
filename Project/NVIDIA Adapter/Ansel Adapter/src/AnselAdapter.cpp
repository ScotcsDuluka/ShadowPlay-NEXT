#include <windows.h>
#include <reshade.hpp>
#include "FilterSession.h"
#include "ReShadeUiMetadata.h"
#include "GamePresetRoot.h"
#include <array>
#include <cstring>
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
    mutable std::filesystem::path cachedGameRoot;
    explicit ReShadeRuntime(api::effect_runtime *r) : runtime(r) {}
    std::filesystem::path executable_directory() const {
        wchar_t executable[32768]{};
        const DWORD len = GetModuleFileNameW(nullptr, executable, 32768);
        if (!len || len >= 32768)
            throw Failure(500, "GAME_PATH_UNAVAILABLE", "Cannot identify the game's executable folder");
        return std::filesystem::path(executable).parent_path();
    }
    std::filesystem::path game_directory() const {
        if (!cachedGameRoot.empty()) return cachedGameRoot;
        wchar_t executable[32768]{};
        const DWORD len = GetModuleFileNameW(nullptr, executable, 32768);
        if (!len || len >= 32768)
            throw Failure(500, "GAME_PATH_UNAVAILABLE", "Cannot identify the game installation");
        const auto resolved = ansel::game_presets::resolve_root(std::filesystem::path(executable));
        if (resolved.empty())
            throw Failure(409, "GAME_ROOT_UNRESOLVED", "Cannot resolve a safe Game Root for ReShade presets");
        cachedGameRoot = resolved; // Determined once, not once per present.
        return cachedGameRoot;
    }
    std::filesystem::path preset_file(unsigned slot) const {
        if (slot < 1 || slot > 3)
            throw Failure(400, "INVALID_ARGUMENT", "Preset slot must be 1, 2 or 3");
        return game_directory() / (L"NVIDIA-GameFilter-Preset" + std::to_wstring(slot) + L".ini");
    }
    std::filesystem::path active_preset_file() const {
        size_t bytes = 0;
        runtime->get_current_preset_path(nullptr, &bytes);
        if (bytes <= 1 || bytes > 32768) return {};
        std::string buffer(bytes, '\0');
        runtime->get_current_preset_path(buffer.data(), &bytes);
        buffer.resize(std::strlen(buffer.c_str()));
        return ansel::game_presets::resolve_preset_path(
            std::filesystem::u8path(buffer), game_directory(), executable_directory());
    }
    Json preset_info() const override {
        const auto current = active_preset_file();
        // active_preset_file resolves relative paths against the installation,
        // not whatever working directory the game launcher happened to set.
        const auto normalized = current.lexically_normal().wstring();
        Json items = Json::array();
        unsigned selected = 0;
        for (unsigned slot = 1; slot <= 3; ++slot) {
            const auto path = preset_file(slot);
            const auto expected = path.lexically_normal().wstring();
            if (!current.empty() && CompareStringOrdinal(normalized.c_str(), -1,
                expected.c_str(), -1, TRUE) == CSTR_EQUAL) selected = slot;
            items.push_back({{"slot", slot}, {"filename", path.filename().u8string()}});
        }
        return {{"supported", true}, {"activeSlot", selected}, {"directory", game_directory().u8string()},
                {"currentPath", current.u8string()}, {"files", items}};
    }
    void select_preset(unsigned slot) override {
        const auto target = preset_file(slot);
        const auto source = active_preset_file();
        std::error_code ec;
        const bool exists = std::filesystem::exists(target, ec);
        if (ec) throw Failure(403, "PRESET_IO_ERROR", "Cannot inspect the game's preset directory");
        if (exists && !std::filesystem::is_regular_file(target, ec))
            throw Failure(403, "PRESET_IO_ERROR", "Preset target is not a regular INI file");
        if (ec) throw Failure(403, "PRESET_IO_ERROR", "Cannot inspect the preset target");
        if (exists && !source.empty()) {
            std::error_code sameError;
            if (std::filesystem::equivalent(source, target, sameError) && !sameError) {
                runtime->set_effects_state(true);
                return;
            }
        }
        // A user may already have created Preset 1/2/3 at Game Root before
        // ReShade has selected any current preset. That file remains usable.
        if (!exists) {
            if (source.empty())
                throw Failure(409, "PRESET_UNAVAILABLE", "No ReShade preset exists to initialize this empty slot");
            // Preserve legacy presets stored next to a nested shipping EXE.
            // Never overwrite the corresponding INI already at Game Root,
            // and never remove the legacy original.
            const auto legacy = executable_directory() / target.filename();
            std::error_code legacyError;
            const bool hasLegacy = legacy != target &&
                std::filesystem::is_regular_file(legacy, legacyError) && !legacyError;
            // ReShade serializes the active shader settings including uniforms
            // OSC cannot represent, before any copy or preset switch.
            runtime->save_current_preset();
            const auto seed = hasLegacy ? legacy : source;
            if (!std::filesystem::is_regular_file(seed, ec) || ec)
                throw Failure(409, "PRESET_UNAVAILABLE", "Current ReShade preset is not a readable INI file");
            std::filesystem::copy_file(seed, target, std::filesystem::copy_options::none, ec);
            if (ec) throw Failure(403, "PRESET_IO_ERROR", "Cannot create preset INI at Game Root");
        }
        runtime->set_current_preset_path(target.u8string().c_str());
        runtime->set_effects_state(true);
        std::error_code verifyError;
        if (!std::filesystem::equivalent(active_preset_file(), target, verifyError) || verifyError)
            throw Failure(409, "PRESET_SWITCH_FAILED", "ReShade did not activate the requested preset");
    }
    void toggle_preset_effects(bool enabled) override {
        runtime->set_effects_state(enabled);
    }
    bool preset_effects_enabled() const override {
        return runtime->get_effects_state();
    }
    void save_preset() override {
        const auto info = preset_info();
        if (!info.value("activeSlot", 0u))
            throw Failure(409, "PRESET_NOT_SELECTED", "Select a game-local preset before saving");
        runtime->save_current_preset();
    }
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
                const std::string kind = uiType;
                const bool slider = bounded && (kind == "slider" || kind == "drag");
                float step[4]{0.01f,0.01f,0.01f,0.01f};
                owner->get_annotation_float_from_uniform_variable(uniform, "ui_step", step, count);
                Json steps = Json::array();
                for (size_t i=0; i<count; ++i) steps.push_back(type == "float" ? (step[i] > 0 ? step[i] : 0.01f) : 1.0f);
                Json control = {{"id",controlId}, {"displayName",title[0] ? title : uniformName},
                    {"type",type == "bool" ? "boolean" : slider ? "slider" : "edit"},
                    {"dataType",type}, {"currentValue",values}, {"defaultValue",defaults.at(defaultKey)},
                    {"minValue",minimum}, {"maxValue",maximum}, {"stepSize",steps},
                    {"uiMinValueV1",minimum}, {"uiMaxValueV1",maximum}, {"uiType",kind}};
                char tooltip[2048]{}, category[256]{};
                owner->get_annotation_string_from_uniform_variable(uniform, "ui_tooltip", tooltip);
                owner->get_annotation_string_from_uniform_variable(uniform, "ui_category", category);
                if (tooltip[0]) control["tooltip"] = tooltip;
                if (category[0]) control["category"] = category;
                if ((kind == "combo" || kind == "radio") && count == 1 &&
                    (type == "int" || type == "uint")) {
                    // ui_items is a NUL-delimited annotation. Read its full
                    // byte length rather than stopping at the first label.
                    size_t bytes = 0;
                    if (owner->get_annotation_string_from_uniform_variable(uniform, "ui_items", nullptr, &bytes) &&
                        bytes > 1 && bytes <= 8192) {
                        std::string raw(bytes, '\0');
                        size_t actual = bytes;
                        if (owner->get_annotation_string_from_uniform_variable(uniform, "ui_items", raw.data(), &actual) &&
                            actual > 0 && actual <= bytes) {
                            raw.resize(actual);
                            reshade_ui::Choices choices;
                            choices.labels = reshade_ui::split_items(raw);
                            for (size_t i=0; i<choices.labels.size(); ++i)
                                choices.values.push_back(static_cast<int64_t>(i));
                            const int64_t selected = values[0].get<int64_t>();
                            const int64_t original = defaults.at(defaultKey)[0].get<int64_t>();
                            // Unknown enumeration values must remain editable
                            // numerically, never silently become the first item.
                            if (choices.valid() && choices.index_of(selected) >= 0 &&
                                choices.index_of(original) >= 0) {
                                control["type"] = "pulldown";
                                control["controlDisplayName"] = choices.labels;
                                control["currentId"] = selected;
                                control["defaultId"] = original;
                            }
                        }
                    }
                }
                controls.push_back(std::move(control));
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
