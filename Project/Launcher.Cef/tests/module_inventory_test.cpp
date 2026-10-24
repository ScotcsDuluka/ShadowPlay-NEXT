// Isolated regression against the REAL supervisor Snapshot/ToJson code.
// All launcherutil lookups are in-memory fakes. No live process enumeration,
// HTTP, config/file reads, DLL loading, service start, or shutdown occurs.
#include "../src/launcher_supervisor.h"
#include "../src/launcher_util.h"

#include <iostream>
#include <set>
#include <stdexcept>
#include <string>
#include <utility>
#include <vector>

namespace {

unsigned assertions = 0;
unsigned scenarios = 0;

void Require(bool condition, const std::string& message) {
  ++assertions;
  if (!condition) throw std::runtime_error(message);
}

struct ProcessSpec {
  const char* id;
  const wchar_t* name;
  const wchar_t* relative_path;
};

// Expected public product layout, intentionally independent of the native
// implementation's helper calls and fields.
const ProcessSpec kProcesses[] = {
    {"Capture", L"NvCapture", L"NVIDIA Capture\\NvCapture.exe"},
    {"Graphics", L"NvGraphics", L"NVIDIA Graphics\\NvGraphics.exe"},
    {"Overlay", L"NVIDIA OSC",
     L"Overlay On-Screen Control\\NVIDIA OSC\\NVIDIA OSC.exe"},
    {"NodeApi", L"NVIDIA Web Helper",
     L"Overlay On-Screen Control\\NVIDIA NodeAPI\\NVIDIA Web Helper.exe"},
    {"Container", L"NvContainer", L"NVIDIA Container\\NvContainer.exe"},
    {"Adapter", L"NVIDIA OSC Adapter",
     L"NVIDIA Adapter\\NVIDIA OSC Adapter.exe"},
    {"Helper", L"nvsphelper", L"NVIDIA Container\\nvsphelper.exe"},
};

const char* kModuleIds[] = {
    "Capture", "Graphics", "Overlay", "NodeApi", "Container", "Adapter",
    "Hook", "Ansel", "Helper", "Ffmpeg", "Config",
};

namespace fake {

struct Process {
  std::wstring name;
  std::wstring path;
};

std::wstring root;
std::set<std::wstring> files;
std::vector<Process> processes;
std::vector<Process> process_queries;
std::vector<std::wstring> file_queries;
bool engine_overlay = false;
bool overlay_enabled = false;
bool http_ready = false;
unsigned config_queries = 0;
unsigned http_queries = 0;

void Reset() {
  root = L"Q:\\Fake ShadowPlay Runtime";
  files.clear();
  processes.clear();
  process_queries.clear();
  file_queries.clear();
  engine_overlay = false;
  overlay_enabled = false;
  http_ready = false;
  config_queries = 0;
  http_queries = 0;
}

std::wstring Path(const wchar_t* relative) {
  return root + L"\\" + relative;
}

void InstallAll() {
  for (const auto& spec : kProcesses) files.insert(Path(spec.relative_path));
  files.insert(Path(L"NVIDIA Adapter\\NVIDIA OSC Adapter Hook.dll"));
  files.insert(Path(L"NVIDIA Adapter\\NVIDIA Ansel Adapter.dll"));
  files.insert(Path(L"NVIDIA Capture\\FFmpeg\\ffmpeg.exe"));
  files.insert(Path(L"NVIDIA Capture\\FFmpeg\\ffprobe.exe"));
  files.insert(Path(L"NVIDIA Config\\config.json"));
}

void AddForeignProcesses() {
  for (const auto& spec : kProcesses) {
    processes.push_back({spec.name,
                         L"C:\\Program Files\\Other Product\\" +
                             std::wstring(spec.name) + L".exe"});
    // A similarly named sibling product root is still a foreign executable.
    processes.push_back({spec.name, root + L"-copy\\" + spec.relative_path});
  }
}

[[noreturn]] void Forbidden(const char* operation) {
  throw std::runtime_error(std::string("Unexpected side effect: ") + operation);
}

}  // namespace fake

bool TracksProcess(const char* id) {
  for (const auto& spec : kProcesses) {
    if (std::string(spec.id) == id) return true;
  }
  return false;
}

launcherjson::JVal Parse(const std::string& json) {
  launcherjson::JVal value;
  Require(launcherjson::Parser(json).Parse(&value), "State must be valid JSON");
  Require(value.type == launcherjson::JVal::kObj, "State must be an object");
  return value;
}

const launcherjson::JVal& Field(const launcherjson::JVal& value,
                               const char* key) {
  const auto* field = value.Find(key);
  Require(field != nullptr, std::string("Missing field: ") + key);
  return *field;
}

const launcherjson::JVal& Module(const launcherjson::JVal& value,
                                const char* id) {
  return Field(Field(value, "modules"), id);
}

bool Bool(const launcherjson::JVal& value, const char* key) {
  const auto& field = Field(value, key);
  Require(field.type == launcherjson::JVal::kBool,
          std::string("Boolean field required: ") + key);
  return field.b;
}

void ExpectModule(const launcherjson::JVal& value, const char* id,
                  bool installed, bool running) {
  const auto& module = Module(value, id);
  Require(module.type == launcherjson::JVal::kObj,
          std::string("Module must be an object: ") + id);
  Require(module.obj.size() == 3,
          std::string("Module must have exactly three public fields: ") + id);
  Require(Bool(module, "installed") == installed,
          std::string("Wrong package presence: ") + id);
  Require(Bool(module, "processTracking") == TracksProcess(id),
          std::string("Wrong process tracking policy: ") + id);
  Require(Bool(module, "running") == running,
          std::string("Wrong process presence: ") + id);
}

void Schema(const launcherjson::JVal& value) {
  const auto& modules = Field(value, "modules");
  Require(modules.type == launcherjson::JVal::kObj, "modules must be an object");
  Require(modules.obj.size() == 11, "Exactly eleven module IDs are required");
  for (const auto* id : kModuleIds) {
    const auto& module = Module(value, id);
    Bool(module, "installed");
    Bool(module, "running");
    Require(Bool(module, "processTracking") == TracksProcess(id),
            std::string("Wrong public tracking policy: ") + id);
  }
  // The additive schema must preserve the existing UI contract.
  Bool(value, "overlayEnabled");
  Bool(value, "engineOverlay");
  Bool(Field(value, "overlayApi"), "running");
  Bool(Field(value, "overlayApi"), "ready");
  Bool(Field(value, "notifierApi"), "running");
  Bool(Field(value, "nvApi"), "running");
  Bool(Field(value, "lanes"), "container");
  Bool(Field(value, "lanes"), "webHelper");
  Bool(Field(value, "lanes"), "cefOverlay");
}

launcherjson::JVal Snapshot() {
  auto json = Parse(LauncherSupervisor::Get()->Snapshot().ToJson());
  Schema(json);
  Require(fake::config_queries >= 2,
          "Existing config booleans must use the fake lookup");
  for (const auto& query : fake::process_queries) {
    Require(query.path.find(fake::root + L"\\") == 0,
            "Every process-path query must stay under the fake product root");
    Require(query.path.size() >= 4 &&
                query.path.substr(query.path.size() - 4) == L".exe",
            "DLLs and configuration must never be process-tracked");
  }
  for (const auto& path : fake::file_queries) {
    Require(path.find(fake::root + L"\\") == 0,
            "Every file query must stay under the fake product root");
  }
  return json;
}

template <typename Fn>
void Run(const std::string& name, Fn test) {
  fake::Reset();
  test();
  ++scenarios;
  std::cout << "PASS " << name << '\n';
}

}  // namespace

// These replace launcher_util.cpp at link time. None performs an OS lookup.
namespace launcherutil {

std::wstring GetRootDir() { return fake::root; }

std::wstring JoinPath(const std::wstring& a, const std::wstring& b) {
  if (a.empty()) return b;
  if (b.empty()) return a;
  return a + (a.back() == L'\\' ? L"" : L"\\") + b;
}

bool FileExists(const std::wstring& path) {
  fake::file_queries.push_back(path);
  return fake::files.find(path) != fake::files.end();
}

bool ProcessRunningFromPath(const wchar_t* name, const std::wstring& path) {
  fake::process_queries.push_back({name, path});
  for (const auto& process : fake::processes) {
    if (_wcsicmp(process.name.c_str(), name) == 0 &&
        _wcsicmp(process.path.c_str(), path.c_str()) == 0) return true;
  }
  return false;
}

bool ProcessRunning(const wchar_t* name) {
  // Only existing legacy service fields may perform name-only lookups.
  Require(std::wstring(name) == L"NVIDIA ShadowPlay" ||
              std::wstring(name) == L"NVIDIA Notifier",
          "New modules must not use name-only process lookups");
  return false;
}

bool ReadConfigBool(const wchar_t* section, const wchar_t* key, bool fallback) {
  ++fake::config_queries;
  Require(std::wstring(section) == L"Overlay", "Unexpected config section");
  if (std::wstring(key) == L"EngineOverlayMode") return fake::engine_overlay;
  if (std::wstring(key) == L"UseOverlayEnabled") return fake::overlay_enabled;
  Require(false, "Unexpected config key");
  return fallback;
}

bool HttpGetLocal(unsigned port, const std::wstring& path) {
  ++fake::http_queries;
  Require((port == 59011 || port == 59013) && path == L"/",
          "Unexpected fake HTTP readiness lookup");
  return fake::http_ready;
}

// Fail closed even if a later supervisor refactor accidentally invokes an
// action while taking a snapshot. These are never connected to live helpers.
std::string WideToUtf8(const std::wstring&) { fake::Forbidden("WideToUtf8"); }
void LogLine(const std::string&) { fake::Forbidden("LogLine"); }
bool StartProcess(const std::wstring&, const std::wstring&) {
  fake::Forbidden("StartProcess");
}
void StopOwnedProcessFromPath(const wchar_t*, const std::wstring&) {
  fake::Forbidden("StopOwnedProcessFromPath");
}
bool WriteConfigBool(const wchar_t*, const wchar_t*, bool) {
  fake::Forbidden("WriteConfigBool");
}
bool WaitForTcpPort(unsigned, unsigned) { fake::Forbidden("WaitForTcpPort"); }
bool IsTcpPortOpen(unsigned) { fake::Forbidden("IsTcpPortOpen"); }
bool HttpPostLocal(unsigned, const std::wstring&) {
  fake::Forbidden("HttpPostLocal");
}
bool SendHubLine(const std::string&) { fake::Forbidden("SendHubLine"); }

}  // namespace launcherutil

int main() {
  try {
    Run("default serialization and installed-only running guard", [] {
      LauncherState state;
      state.modules.hook.running = true;
      state.modules.ansel.running = true;
      state.modules.ffmpeg.running = true;
      state.modules.config.running = true;
      const auto json = Parse(state.ToJson());
      Schema(json);
      for (const auto* id : kModuleIds) ExpectModule(json, id, false, false);
      Require(fake::process_queries.empty() && fake::file_queries.empty() &&
                  fake::config_queries == 0 && fake::http_queries == 0,
              "Serialization must not perform any runtime lookups");
    });

    Run("empty package snapshot", [] {
      const auto json = Snapshot();
      for (const auto* id : kModuleIds) ExpectModule(json, id, false, false);
    });

    Run("installed packages do not imply running", [] {
      fake::InstallAll();
      const auto json = Snapshot();
      for (const auto* id : kModuleIds) ExpectModule(json, id, true, false);
    });

    Run("foreign same-name executables are excluded", [] {
      fake::InstallAll();
      fake::AddForeignProcesses();
      const auto json = Snapshot();
      for (const auto* id : kModuleIds) ExpectModule(json, id, true, false);
    });

    for (const auto& spec : kProcesses) {
      Run(std::string("exact product process: ") + spec.id, [&spec] {
        fake::InstallAll();
        fake::AddForeignProcesses();
        fake::processes.push_back({spec.name, fake::Path(spec.relative_path)});
        const auto json = Snapshot();
        for (const auto* id : kModuleIds) {
          ExpectModule(json, id, true, std::string(id) == spec.id);
        }
      });
    }

    Run("x86 Adapter and Hook deployment", [] {
      const auto adapter = fake::Path(
          L"NVIDIA Adapter\\x86\\NVIDIA OSC Adapter.exe");
      fake::files.insert(adapter);
      fake::files.insert(fake::Path(
          L"NVIDIA Adapter\\x86\\NVIDIA OSC Adapter Hook.dll"));
      fake::processes.push_back({L"NVIDIA OSC Adapter", adapter});
      const auto json = Snapshot();
      ExpectModule(json, "Adapter", true, true);
      ExpectModule(json, "Hook", true, false);
      ExpectModule(json, "Ansel", false, false);
    });

    Run("DLL and Config package presence is installed-only", [] {
      fake::files.insert(fake::Path(
          L"NVIDIA Adapter\\NVIDIA OSC Adapter Hook.dll"));
      fake::files.insert(fake::Path(
          L"NVIDIA Adapter\\NVIDIA Ansel Adapter.dll"));
      fake::files.insert(fake::Path(L"NVIDIA Config\\config.json"));
      fake::processes.push_back({L"NVIDIA OSC Adapter Hook", fake::Path(
          L"NVIDIA Adapter\\NVIDIA OSC Adapter Hook.dll")});
      fake::processes.push_back({L"NVIDIA Ansel Adapter", fake::Path(
          L"NVIDIA Adapter\\NVIDIA Ansel Adapter.dll")});
      const auto json = Snapshot();
      ExpectModule(json, "Hook", true, false);
      ExpectModule(json, "Ansel", true, false);
      ExpectModule(json, "Config", true, false);
      ExpectModule(json, "Adapter", false, false);
    });

    for (unsigned mask = 0; mask < 4; ++mask) {
      Run("FFmpeg required pair, presence mask " + std::to_string(mask), [mask] {
        const auto ffmpeg = fake::Path(L"NVIDIA Capture\\FFmpeg\\ffmpeg.exe");
        const auto ffprobe = fake::Path(L"NVIDIA Capture\\FFmpeg\\ffprobe.exe");
        if (mask & 1) fake::files.insert(ffmpeg);
        if (mask & 2) fake::files.insert(ffprobe);
        fake::processes.push_back({L"ffmpeg", ffmpeg});
        fake::processes.push_back({L"ffprobe", ffprobe});
        const auto json = Snapshot();
        ExpectModule(json, "Ffmpeg", mask == 3, false);
      });
    }

    Run("process inventory is independent of HTTP and recording readiness", [] {
      fake::InstallAll();
      fake::engine_overlay = true;
      fake::overlay_enabled = true;
      for (const auto& spec : kProcesses) {
        fake::processes.push_back({spec.name, fake::Path(spec.relative_path)});
      }
      fake::http_ready = false;
      const auto json = Snapshot();
      for (const auto& spec : kProcesses) {
        ExpectModule(json, spec.id, true, true);
      }
      Require(!Bool(Field(json, "lanes"), "cefOverlay") &&
                  !Bool(Field(json, "lanes"), "webHelper") &&
                  !Bool(Field(json, "overlayApi"), "ready"),
              "Legacy HTTP readiness must remain false while processes exist");
      Require(fake::http_queries == 2,
              "Only existing two readiness lookups may use HTTP");
    });

    Run("inventory follows resolved product root", [] {
      fake::InstallAll();
      for (const auto& spec : kProcesses) {
        fake::processes.push_back({spec.name, fake::Path(spec.relative_path)});
      }
      fake::root = L"R:\\Different Product Root";
      const auto json = Snapshot();
      for (const auto* id : kModuleIds) ExpectModule(json, id, false, false);
    });

    std::cout << "MODULE_INVENTORY_TEST_PASS " << scenarios
              << " scenarios, " << assertions << " assertions\n";
    return 0;
  } catch (const std::exception& error) {
    std::cerr << "MODULE_INVENTORY_TEST_FAIL: " << error.what() << '\n';
    return 1;
  }
}
