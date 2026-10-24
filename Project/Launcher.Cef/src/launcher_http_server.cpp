// launcher_http_server.cpp - see launcher_http_server.h.
#include "launcher_http_server.h"

#include <stdio.h>
#include <process.h>
#include <new>

#include <sstream>
#include <vector>

#include "launcher_util.h"
#include "launcher_http_policy.h"

namespace {

struct WsaInit {
  WsaInit() {
    WSADATA data;
    WSAStartup(MAKEWORD(2, 2), &data);
  }
  ~WsaInit() { WSACleanup(); }
};

const char* MimeFor(const std::string& path_lower) {
  size_t dot = path_lower.find_last_of('.');
  if (dot == std::string::npos) return "application/octet-stream";
  const std::string ext = path_lower.substr(dot);
  if (ext == ".html" || ext == ".htm") return "text/html; charset=utf-8";
  if (ext == ".js") return "application/javascript; charset=utf-8";
  if (ext == ".json" || ext == ".map") return "application/json";
  if (ext == ".css") return "text/css; charset=utf-8";
  if (ext == ".png") return "image/png";
  if (ext == ".jpg" || ext == ".jpeg") return "image/jpeg";
  if (ext == ".gif") return "image/gif";
  if (ext == ".webp") return "image/webp";
  if (ext == ".svg") return "image/svg+xml";
  if (ext == ".woff") return "font/woff";
  if (ext == ".woff2") return "font/woff2";
  if (ext == ".ttf") return "font/ttf";
  if (ext == ".otf") return "font/otf";
  if (ext == ".ico") return "image/x-icon";
  if (ext == ".txt") return "text/plain; charset=utf-8";
  return "application/octet-stream";
}

int StatusReason(int code, std::string* reason) {
  switch (code) {
    case 200: *reason = "OK"; return 200;
    case 400: *reason = "Bad Request"; return 400;
    case 404: *reason = "Not Found"; return 404;
    case 405: *reason = "Method Not Allowed"; return 405;
    case 429: *reason = "Too Many Requests"; return 429;
    case 503: *reason = "Service Unavailable"; return 503;
    default: *reason = "Internal Server Error"; return 500;
  }
}

// URL-decodes %xx (+ left as-is: paths don't use form encoding).
std::string UrlDecode(const std::string& in) {
  std::string out;
  out.reserve(in.size());
  for (size_t i = 0; i < in.size(); ++i) {
    if (in[i] == '%' && i + 2 < in.size()) {
      int hi = in[i + 1], lo = in[i + 2];
      auto hv = [](int c) -> int {
        if (c >= '0' && c <= '9') return c - '0';
        if (c >= 'a' && c <= 'f') return c - 'a' + 10;
        if (c >= 'A' && c <= 'F') return c - 'A' + 10;
        return -1;
      };
      int h = hv(hi), l = hv(lo);
      if (h >= 0 && l >= 0) {
        out.push_back(static_cast<char>((h << 4) | l));
        i += 2;
        continue;
      }
    }
    out.push_back(in[i]);
  }
  return out;
}

void SendAll(SOCKET s, const char* data, int len) {
  int sent = 0;
  while (sent < len) {
    int n = send(s, data + sent, len - sent, 0);
    if (n <= 0) return;
    sent += n;
  }
}

void SendResponse(SOCKET s, int code, const std::string& content_type,
                  const std::string* body_text, const wchar_t* file_path) {
  std::string reason;
  StatusReason(code, &reason);
  std::ostringstream head;
  head << "HTTP/1.1 " << code << " " << reason << "\r\n"
       << "Content-Type: " << content_type << "\r\n"
       << "Connection: close\r\n"
       << "Cache-Control: no-cache\r\n"
       << "X-Content-Type-Options: nosniff\r\n"
       << "X-Frame-Options: DENY\r\n";
  if (body_text) {
    head << "Content-Length: " << body_text->size() << "\r\n\r\n";
    std::string h = head.str();
    SendAll(s, h.data(), (int)h.size());
    SendAll(s, body_text->data(), (int)body_text->size());
  } else {
    HANDLE f = CreateFileW(file_path, GENERIC_READ, FILE_SHARE_READ, NULL,
                           OPEN_EXISTING, FILE_ATTRIBUTE_NORMAL, NULL);
    if (f == INVALID_HANDLE_VALUE) {
      std::string body = "{\"error\":\"file_open_failed\"}";
      head << "Content-Length: " << body.size() << "\r\n\r\n";
      std::string h = head.str();
      SendAll(s, h.data(), (int)h.size());
      SendAll(s, body.data(), (int)body.size());
      return;
    }
    LARGE_INTEGER sz;
    GetFileSizeEx(f, &sz);
    head << "Content-Length: " << sz.QuadPart << "\r\n\r\n";
    std::string h = head.str();
    SendAll(s, h.data(), (int)h.size());
    char buf[65536];
    DWORD read = 0;
    LARGE_INTEGER off;
    off.QuadPart = 0;
    SetFilePointer(f, 0, NULL, FILE_BEGIN);
    while (ReadFile(f, buf, sizeof(buf), &read, NULL) && read > 0) {
      SendAll(s, buf, (int)read);
    }
    CloseHandle(f);
  }
}

const char* kNotFoundBody = "not found";

}  // namespace

LauncherHttpServer::LauncherHttpServer() : port_(0), stop_(false) {}

LauncherHttpServer::~LauncherHttpServer() { Stop(); }

bool LauncherHttpServer::Start(const std::string& ui_root, std::string* err) {
  static WsaInit wsa_init;  // process-wide, main thread before threads spawn
  ui_root_ = ui_root;

  listen_sock_ = socket(AF_INET, SOCK_STREAM, IPPROTO_TCP);
  if (listen_sock_ == INVALID_SOCKET) {
    *err = "socket() failed: " + std::to_string(WSAGetLastError());
    return false;
  }
  BOOL reuse = TRUE;
  setsockopt(listen_sock_, SOL_SOCKET, SO_REUSEADDR, (const char*)&reuse,
             sizeof(reuse));
  sockaddr_in addr;
  memset(&addr, 0, sizeof(addr));
  addr.sin_family = AF_INET;
  addr.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
  addr.sin_port = 0;  // ephemeral
  if (bind(listen_sock_, (sockaddr*)&addr, sizeof(addr)) == SOCKET_ERROR) {
    *err = "bind() failed: " + std::to_string(WSAGetLastError());
    return false;
  }
  if (listen(listen_sock_, 64) == SOCKET_ERROR) {
    *err = "listen() failed: " + std::to_string(WSAGetLastError());
    return false;
  }
  sockaddr_in bound;
  int blen = sizeof(bound);
  getsockname(listen_sock_, (sockaddr*)&bound, &blen);
  port_.store(ntohs(bound.sin_port));

  stop_.store(false);
  accept_thread_ = std::thread(&LauncherHttpServer::AcceptLoop, this);
  return true;
}

void LauncherHttpServer::AcceptLoop() {
  while (!stop_.load()) {
    SOCKET c = accept(listen_sock_, NULL, NULL);
    if (c == INVALID_SOCKET) {
      if (stop_.load()) break;
      continue;
    }
    // Bound worker count and join on Stop(); detached workers may NOT outlive
    // the server object's owning CEF process.
    {
      std::lock_guard<std::mutex> lock(workers_mutex_);
      if (stop_.load() || active_workers_ >= kMaxConcurrentConnections) {
        const std::string busy = "launcher UI busy";
        SendResponse(c, 429, "text/plain", &busy, NULL);
        closesocket(c);
        continue;
      }
      ++active_workers_;
    }
    WorkItem* job = new (std::nothrow) WorkItem{this, c};
    const uintptr_t worker = job
        ? _beginthreadex(NULL, 0, &LauncherHttpServer::WorkerTramp,
                         job, 0, NULL)
        : 0;
    if (!worker) {
      delete job;
      {
        std::lock_guard<std::mutex> lock(workers_mutex_);
        --active_workers_;
      }
      closesocket(c);
      workers_done_.notify_all();
    } else {
      CloseHandle(reinterpret_cast<HANDLE>(worker));
    }
  }
}

unsigned __stdcall LauncherHttpServer::WorkerTramp(void* work) {
  WorkItem* job = static_cast<WorkItem*>(work);
  LauncherHttpServer* server = job->server;
  const SOCKET socket = job->socket;
  delete job;
  server->HandleConnection(socket);
  {
    std::lock_guard<std::mutex> lock(server->workers_mutex_);
    --server->active_workers_;
  }
  server->workers_done_.notify_all();
  return 0;
}

void LauncherHttpServer::HandleConnection(SOCKET s) {
  std::string request;
  char buf[8192];
  // A stalled loopback client must never consume a worker indefinitely.
  // One global 3s deadline (not 3s per recv) and a strict 16 KiB limit.
  const DWORD timeout_ms = 750;
  setsockopt(s, SOL_SOCKET, SO_RCVTIMEO,
             reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms));
  setsockopt(s, SOL_SOCKET, SO_SNDTIMEO,
             reinterpret_cast<const char*>(&timeout_ms), sizeof(timeout_ms));
  const ULONGLONG deadline = GetTickCount64() + 3000;
  while (GetTickCount64() < deadline && request.size() <= 16 * 1024 &&
         request.find("\r\n\r\n") == std::string::npos) {
    const int n = recv(s, buf, sizeof(buf), 0);
    if (n <= 0) break;
    request.append(buf, n);
  }
  std::string target;
  const int result = launcher_http_policy::ValidateHeaders(
      request, static_cast<unsigned>(port_.load()), &target);
  if (result != 200) {
    const std::string body = result == 405 ? "method not allowed" : "bad request";
    SendResponse(s, result, "text/plain", &body, NULL);
    closesocket(s);
    return;
  }

  std::string path = target;
  size_t q = path.find('?');
  if (q != std::string::npos) path = path.substr(0, q);
  path = UrlDecode(path);
  if (path.empty() || path[0] != '/' || path.find("..") != std::string::npos ||
      path.find('\\') != std::string::npos ||
      path.find('\0') != std::string::npos) {
    launcherutil::LogLine("http rejected path=" + path);
    std::string body = "bad path";
    SendResponse(s, 400, "text/plain", &body, NULL);
    closesocket(s);
    return;
  }
  if (path == "/") path = "/index.html";

  // Rooted, separator-normalized path under the UI root.
  std::string rel = path.substr(1);
  for (size_t i = 0; i < rel.size(); ++i) {
    if (rel[i] == '/') rel[i] = '\\';
  }
  std::string root_lower = ui_root_, rel_lower = rel;
  for (size_t i = 0; i < root_lower.size(); ++i)
    if (root_lower[i] == '/') root_lower[i] = '\\';
  std::string full = root_lower + (root_lower[root_lower.size() - 1] == '\\'
                                       ? "" : "\\") + rel;
  std::string full_lower = launcherutil::LowerAscii(full);
  root_lower = launcherutil::LowerAscii(root_lower);

  std::string seg = rel_lower;
  size_t bslash = seg.find_last_of('\\');
  if (bslash != std::string::npos) seg = seg.substr(bslash + 1);
  bool looks_like_file = seg.find('.') != std::string::npos;

  if (looks_like_file) {
    std::wstring wide = launcherutil::Utf8ToWide(full);
    DWORD attr = GetFileAttributesW(wide.c_str());
    if (attr != INVALID_FILE_ATTRIBUTES && !(attr & FILE_ATTRIBUTE_DIRECTORY)) {
      SendResponse(s, 200, MimeFor(full_lower), NULL, wide.c_str());
      closesocket(s);
      return;
    }
    std::string nf = kNotFoundBody;
    SendResponse(s, 404, "text/plain", &nf, NULL);
    closesocket(s);
    return;
  }

  // Extensionless paths: the launcher host serves the UI bundle only.
  std::string body = "{\"error\":\"unsupported_in_launcher_host\"}";
  SendResponse(s, 503, "application/json", &body, NULL);
  closesocket(s);
}

void LauncherHttpServer::Stop() {
  bool expected = false;
  if (stop_.compare_exchange_strong(expected, true)) {
    if (listen_sock_ != INVALID_SOCKET) {
      closesocket(listen_sock_);
      listen_sock_ = INVALID_SOCKET;
    }
    if (accept_thread_.joinable()) accept_thread_.join();
    // Socket sends/receives above have finite timeouts; wait until all worker
    // lambdas have stopped touching this object before it is destroyed.
    std::unique_lock<std::mutex> lock(workers_mutex_);
    workers_done_.wait(lock, [this]() { return active_workers_ == 0; });
  }
}
