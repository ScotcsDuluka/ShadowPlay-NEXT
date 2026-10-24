// Pure identity policy tests: no dialogs, no processes, no file mutation.
#include "../src/launcher_module_actions.h"
#include <cassert>
#include <iostream>

int main() {
  using launchermodules::ValidModuleId;
  assert(ValidModuleId("forest"));
  assert(ValidModuleId("next-grid-demo"));
  assert(ValidModuleId("a"));
  assert(ValidModuleId("z0123456789"));
  assert(!ValidModuleId(""));
  assert(!ValidModuleId("default"));
  assert(!ValidModuleId("../outside"));
  assert(!ValidModuleId(".."));
  assert(!ValidModuleId("AUpper"));
  assert(!ValidModuleId("a b"));
  assert(!ValidModuleId("a\\b"));
  assert(!ValidModuleId("a:evil"));
  assert(!ValidModuleId(std::string(41,'a')));
  std::cout << "PASS native module id policy (13 cases)\n";
  return 0;
}
