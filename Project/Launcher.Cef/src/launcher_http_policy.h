// Launcher local static UI HTTP request policy. Portable for isolated tests.
// No filesystem or socket access in this header.
#ifndef LAUNCHER_HTTP_POLICY_H_
#define LAUNCHER_HTTP_POLICY_H_
#include <cctype>
#include <string>

namespace launcher_http_policy {
inline std::string LowerAscii(std::string text) {
  for (char& c : text)
    if (c >= 'A' && c <= 'Z') c = static_cast<char>(c - 'A' + 'a');
  return text;
}
inline std::string Trim(const std::string& text) {
  const auto first = text.find_first_not_of(" \t");
  if (first == std::string::npos) return {};
  const auto last = text.find_last_not_of(" \t");
  return text.substr(first, last - first + 1);
}
// Status 200: valid GET; 400: malformed/foreign host; 405: other method.
// Keep the request-target path validation in launcher_http_server.cpp.
// Reject duplicate Host even with identical values to avoid ambiguity.
inline int ValidateHeaders(const std::string& raw, unsigned port,
                           std::string* target) {
  if (raw.empty() || raw.size() > 16 * 1024 ||
      raw.find('\0') != std::string::npos ||
      raw.find("\r\n\r\n") == std::string::npos) return 400;
  const auto first_end = raw.find("\r\n");
  if (first_end == std::string::npos) return 400;
  const std::string line = raw.substr(0, first_end);
  const auto a = line.find(' ');
  const auto b = a == std::string::npos ? a : line.find(' ', a + 1);
  if (a == std::string::npos || b == std::string::npos ||
      line.find(' ', b + 1) != std::string::npos) return 400;
  const auto method = line.substr(0, a);
  const auto uri = line.substr(a + 1, b - a - 1);
  const auto version = line.substr(b + 1);
  if (method.empty() || uri.empty() || version != "HTTP/1.1") return 400;
  if (method != "GET") return 405;

  bool has_host = false;
  std::size_t start = first_end + 2;
  const auto headers_end = raw.find("\r\n\r\n");
  while (start < headers_end) {
    const auto end = raw.find("\r\n", start);
    if (end == std::string::npos || end > headers_end) return 400;
    const auto header = raw.substr(start, end - start);
    const auto colon = header.find(':');
    if (colon == std::string::npos || colon == 0) return 400;
    const auto name = LowerAscii(header.substr(0, colon));
    if (name == "host") {
      if (has_host) return 400;
      has_host = true;
      const auto value = LowerAscii(Trim(header.substr(colon + 1)));
      const auto suffix = ":" + std::to_string(port);
      if (value != "127.0.0.1" + suffix &&
          value != "localhost" + suffix) return 400;
    }
    start = end + 2;
  }
  if (!has_host) return 400;
  *target = uri;
  return 200;
}
}  // namespace launcher_http_policy
#endif
