// Isolated HTTP policy regression; no sockets, processes or real services.
#include "../src/launcher_http_policy.h"
#include <cassert>
#include <iostream>
#include <string>

int main() {
  using launcher_http_policy::ValidateHeaders;
  const unsigned port = 49001;
  std::string path;
  auto check = [&](const std::string& request, int expected) {
    std::string target = "unset";
    assert(ValidateHeaders(request, port, &target) == expected);
    if (expected != 200) assert(target == "unset");
    return target;
  };
  assert(check("GET /index.html HTTP/1.1\r\nHost: 127.0.0.1:49001\r\n\r\n", 200) == "/index.html");
  assert(check("GET /x.js HTTP/1.1\r\nhOsT: localhost:49001\r\nAccept: */*\r\n\r\n", 200) == "/x.js");
  check("GET /x HTTP/1.1\r\n\r\n", 400);  // Missing host
  check("GET /x HTTP/1.1\r\nHost: evil.example:49001\r\n\r\n", 400);
  check("GET /x HTTP/1.1\r\nHost: 127.0.0.1:49002\r\n\r\n", 400);
  check("GET /x HTTP/1.1\r\nHost: 127.0.0.1:49001\r\nHost: localhost:49001\r\n\r\n", 400);
  check("GET /x HTTP/1.1\r\nHost: 127.0.0.1:49001\r\n", 400); // Truncated
  check("POST /x HTTP/1.1\r\nHost: 127.0.0.1:49001\r\n\r\n", 405);
  check("GET /x HTTP/1.0\r\nHost: 127.0.0.1:49001\r\n\r\n", 400);
  check("GET /x HTTP/1.1\r\nHost: 127.0.0.1:49001\r\n\r\n" + std::string(17000, 'x'), 400);
  check("GET /x HTTP/1.1\r\nHost: 127.0.0.1:49001\r\nSomething Bad\r\n\r\n", 400);
  std::cout << "PASS: Launcher HTTP policy regression (11 cases)\n";
}
