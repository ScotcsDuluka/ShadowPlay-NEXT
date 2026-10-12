// launcher_query_handler.cpp - see launcher_query_handler.h.
#include "launcher_query_handler.h"

#include <shellapi.h>

#include <sstream>
#include <thread>

#include "include/cef_browser.h"
#include "include/cef_frame.h"
#include "include/cef_task.h"

#include "launcher_json.h"
#include "launcher_supervisor.h"
#include "launcher_util.h"
#include "launcher_win.h"

namespace {

volatile LONG g_exit_all_started = 0;

// EXIT ALL runs the process shutdown in a worker and posts the final window
// close back to CEF's UI thread after the owned processes have been handled.
class QueryHandlerUiTask : public CefTask {
 public:
  explicit QueryHandlerUiTask(void (*callback)()) : callback_(callback) {}
  void Execute() override {
    if (callback_) callback_();
  }

 private:
  void (*callback_)();
  IMPLEMENT_REFCOUNTING(QueryHandlerUiTask);
};

void RespondOk(CefRefPtr<CefMessageRouterBrowserSide::Callback> callback,
               const std::string& response) {
  callback->Success(response);
}

void RespondFail(CefRefPtr<CefMessageRouterBrowserSide::Callback> callback,
                 int code, const std::string& message) {
  callback->Failure(code, message);
}

}  // namespace

LauncherQueryHandler::LauncherQueryHandler(HWND host_wnd)
    : host_wnd_(host_wnd) {}

LauncherQueryHandler::~LauncherQueryHandler() {}

void LauncherQueryHandler::FireInitialPush() {
  if (initial_push_cb_ && supervisor_) {
    initial_push_cb_(supervisor_->Snapshot().ToJson());
  }
}

bool LauncherQueryHandler::OnQuery(CefRefPtr<CefBrowser> browser,
                                   CefRefPtr<CefFrame> frame, int64 query_id,
                                   const CefString& request, bool persistent,
                                   CefRefPtr<Callback> callback) {
  const std::string request_str = request.ToString();

  // Malformed-request ladder — parity with ShareQueryHandler::OnQuery.
  launcherjson::JVal req;
  if (!launcherjson::Parse(request_str, &req)) {
    RespondFail(callback, -3, "bad request json");
    return true;
  }
  if (req.type != launcherjson::JVal::kObj) {
    RespondFail(callback, -3, "request not object");
    return true;
  }
  const std::string cmd = launcherjson::GetStr(req, "command");
  if (cmd.empty()) {
    RespondFail(callback, -3, "no command");
    return true;
  }

  if (cmd == "LAUNCHER_GET_STATE") {
    // Snapshot performs process and loopback-port probes. Keep that work off
    // the CEF UI thread so a slow Windows process/socket query cannot stall
    // painting or input.
    LauncherSupervisor* supervisor = supervisor_;
    std::thread([supervisor, callback]() {
      RespondOk(callback, supervisor ? supervisor->Snapshot().ToJson()
                                     : std::string("{}"));
    }).detach();
    return true;
  }

  if (cmd == "LAUNCHER_SET_OVERLAY") {
    // Power toggle for the complete selected overlay engine. The WinForm
    // hub or NvContainer owns starting/stopping its engine's service family.
    const bool value = launcherjson::GetBool(req, "value");
    const bool ok = launcherutil::WriteConfigBool(
        L"Overlay", L"UseOverlayEnabled", value);
    LauncherSupervisor* supervisor = supervisor_;
    if (ok && supervisor &&
        launcherutil::ReadConfigBool(L"Overlay", L"EngineOverlayMode", false)) {
      if (value) {
        supervisor->StartEngineOverlayChainAsync();
      } else {
        // A hung OSC HTTP handler must not block the CEF message loop.
        std::thread([supervisor]() { supervisor->StopCefOverlay(); }).detach();
      }
    }
    RespondOk(callback, ok ? "{\"ok\":true}" : "{\"ok\":false}");
    return true;
  }

  if (cmd == "LAUNCHER_SET_ENGINE_OVERLAY") {
    // Overlay Mode switch: false = WINFORM lane (hub-managed family),
    // true = CEF lane (NvContainer -> Web Helper :59011 -> NVIDIA OSC,
    // toggle :59013). NvContainer owns the helper and presenter; mode
    // changes prepare services but never show the overlay.
    const bool value = launcherjson::GetBool(req, "value");
    const bool ok = launcherutil::WriteConfigBool(
        L"Overlay", L"EngineOverlayMode", value);
    LauncherSupervisor* supervisor = supervisor_;
    if (ok && supervisor) {
      if (value &&
          launcherutil::ReadConfigBool(L"Overlay", L"UseOverlayEnabled", false)) {
        // Power is already on: prepare the newly selected engine without
        // showing the overlay. Power-off mode selection starts no services.
        supervisor->StartEngineOverlayChainAsync();
      } else if (!value) {
        // Keep a blocked OSC /hide handler from freezing the UI thread.
        std::thread([supervisor]() { supervisor->StopCefOverlay(); }).detach();
      }
    }
    RespondOk(callback, ok ? "{\"ok\":true}" : "{\"ok\":false}");
    return true;
  }

  if (cmd == "LAUNCHER_OPEN_OVERLAY") {
    auto* supervisor = supervisor_;
    std::thread([supervisor, callback]() {
      const bool ok = supervisor && supervisor->SendOpenOverlay();
      RespondOk(callback, ok ? "{\"ok\":true}" : "{\"ok\":false}");
    }).detach();
    return true;
  }

  if (cmd == "LAUNCHER_OPEN_OBT3") {
    // Legacy OBT3 banner link (Main.vb OBT3_Click).
    HINSTANCE r = ShellExecuteW(NULL, L"open",
                                L"https://scotcsduluka.github.io/"
                                L"NVIDIA-Shadowplay/obt3.html",
                                NULL, NULL, SW_SHOWNORMAL);
    RespondOk(callback, reinterpret_cast<INT_PTR>(r) > 32 ? "{\"ok\":true}"
                                                          : "{\"ok\":false}");
    return true;
  }

  if (cmd == "LAUNCHER_DRAG") {
    launcherwin::BeginWindowDrag(host_wnd_);
    RespondOk(callback, "{\"ok\":true}");
    return true;
  }

  if (cmd == "LAUNCHER_MINIMIZE") {
    launcherwin::Minimize(host_wnd_);
    RespondOk(callback, "{\"ok\":true}");
    return true;
  }

  if (cmd == "LAUNCHER_CLOSE") {
    // Plain close: processes keep running (Main.vb RadioButton1 contract).
    RespondOk(callback, "{\"ok\":true}");
    if (request_close_cb_) request_close_cb_();
    return true;
  }

  if (cmd == "LAUNCHER_EXIT_ALL") {
    // The browser can still receive input while cleanup runs; only start one
    // shutdown worker so repeated clicks cannot race multiple kill passes.
    if (InterlockedCompareExchange(&g_exit_all_started, 1, 0) != 0) {
      RespondOk(callback, "{\"ok\":true}");
      return true;
    }
    // Installer-mode exit: do process/file I/O away from the CEF UI thread,
    // then post the close back to CEF after cleanup has actually finished.
    LauncherSupervisor* supervisor = supervisor_;
    void (*close_callback)() = request_close_cb_;
    RespondOk(callback, "{\"ok\":true}");
    if (supervisor) {
      std::thread([supervisor, close_callback]() {
        // Stop the poll/watch thread first so it cannot respawn NvContainer
        // while InstallerExit is shutting down the owned process family.
        supervisor->Stop();
        supervisor->InstallerExit();
        if (close_callback) {
          CefPostTask(TID_UI, new QueryHandlerUiTask(close_callback));
        }
      }).detach();
    } else if (close_callback) {
      close_callback();
    }
    return true;
  }

  RespondFail(callback, -1, "unknown command: " + cmd);
  return true;
}
