// launcher_http_server.h - loopback static server for the launcher UI
// bundle (production model parity: the page loads over localhost HTTP,
// never file:// — same contract as Overlay.Cef share_http_server.cpp).
// Extensionless paths (REST-ish) answer 503 so page services degrade,
// never hang.
#ifndef LAUNCHER_HTTP_SERVER_H_
#define LAUNCHER_HTTP_SERVER_H_

#include <atomic>
#include <condition_variable>
#include <mutex>
#include <string>
#include <thread>

#include <winsock2.h>

class LauncherHttpServer {
 public:
  LauncherHttpServer();
  ~LauncherHttpServer();
  bool Start(const std::string& ui_root, std::string* err);
  void Stop();
  int port() const { return port_.load(); }

 private:
  void AcceptLoop();
  void HandleConnection(SOCKET s);
  struct WorkItem { LauncherHttpServer* server; SOCKET socket; };
  static unsigned __stdcall WorkerTramp(void* work);

  std::string ui_root_;
  std::atomic<int> port_;
  std::atomic<bool> stop_;
  SOCKET listen_sock_ = INVALID_SOCKET;
  std::thread accept_thread_;
  std::mutex workers_mutex_;
  std::condition_variable workers_done_;
  unsigned active_workers_ = 0;
  static constexpr unsigned kMaxConcurrentConnections = 16;
};

#endif  // LAUNCHER_HTTP_SERVER_H_
