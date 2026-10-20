// Include the transport implementation so integration tests exercise the same
// overlapped pipe used in the game, with an explicitly fake effect runtime.
#include "../src/AnselAdapter.cpp"
#include <iostream>

struct FakeRuntime final : ansel::Runtime {
    ansel::Json filters = ansel::Json::array({
        {{"id","test/filter-a"},{"name","Test A"},{"enabled",false},{"controls",ansel::Json::array({
            {{"id","Strength"},{"displayName","Strength"},{"type","slider"},{"dataType","float"},
             {"currentValue",{0.5}},{"defaultValue",{0.5}},{"minValue",{0.0}},{"maxValue",{1.0}},{"stepSize",{0.01}}}
        })}},
        {{"id","test/filter-b"},{"name","Test B"},{"enabled",false},{"controls",ansel::Json::array()}}
    });
    int writes = 0;
    bool rejectValue = false;
    ansel::Json catalog() override { return filters; }
    void enable(const std::string &id, bool enabled) override { ++writes; for(auto &f: filters) if(f["id"] == id) f["enabled"] = enabled; }
    void value(const std::string &id, const std::string &control, const ansel::Json &v) override {
        ++writes; if (rejectValue) return;
        for(auto &f: filters) if(f["id"] == id) for(auto &c:f["controls"]) if(c["id"] == control) c["currentValue"] = v;
    }
    void reorder(const std::vector<std::string> &) override { ++writes; }
};
void require(bool condition, const char *message) { if(!condition) throw std::runtime_error(message); }

int main(int argc, char **argv) {
    try {
        FakeRuntime runtime;
        ansel::FilterSession session(runtime, GetCurrentProcessId(), "native-test-session");
        auto request = [&](const std::string &op, ansel::Json body) {
            const auto s = session.snapshot();
            const auto deadline = std::chrono::duration_cast<std::chrono::milliseconds>(std::chrono::system_clock::now().time_since_epoch()).count() + 5000;
            return ansel::Json{{"protocol",1},{"deadline",deadline},{"session",s["session"]},{"generation",s["generation"]},{"op",op},{"body",body}};
        };
        if (argc > 1 && std::string(argv[1]) == "--serve") {
            Pipe pipe(1);
            std::cout << "READY " << GetCurrentProcessId() << std::endl;
            const auto until = GetTickCount64() + 90000;
            while (GetTickCount64() < until) {
                pipe.tick([&](const ansel::Json &r) {
                    if (r.value("op", std::string()) == "testReload") {
                        session.reload(); auto reply = ansel::Json{{"ok",true},{"state",session.snapshot()}};
                        reply["state"]["foreground"] = true; return reply;
                    }
                    auto reply = session.dispatch(r);
                    if(reply.value("ok",false)) {
                        reply["state"]["foreground"] = true;
                        reply["state"]["executable"] = "test-only-fake-runtime.exe";
                    }
                    return reply;
                });
                Sleep(2);
            }
            return 0;
        }
        require(session.dispatch(request("select",{{"type","test/filter-a"},{"stackIdx",0}}))["ok"], "select failed");
        auto attribute = request("attribute",{{"id","test/filter-a"},{"controlId","Strength"},{"value",{0.75}},{"stackIdx",0}});
        require(session.dispatch(attribute)["ok"], "attribute failed");
        require(runtime.filters[0]["controls"][0]["currentValue"][0] == 0.75, "runtime did not receive uniform write");
        auto invalid = attribute; invalid["body"]["value"] = {2.0};
        const auto before = runtime.writes;
        require(session.dispatch(invalid)["status"] == 400 && runtime.writes == before, "out-of-range request changed runtime");
        invalid = attribute; invalid["body"]["value"] = {0.1,0.2};
        require(session.dispatch(invalid)["status"] == 400, "invalid dimensions accepted");
        invalid = attribute; invalid["deadline"] = 1;
        require(session.dispatch(invalid)["code"] == "EXPIRED_COMMAND", "expired command accepted");
        invalid = attribute; invalid["body"]["value"] = {0.2}; runtime.rejectValue = true;
        require(session.dispatch(invalid)["code"] == "RUNTIME_REJECTED", "silent runtime rejection reported success");
        runtime.rejectValue = false;
        runtime.filters[0]["controls"][0]["minValue"] = {0.10000000149011612};
        invalid = attribute; invalid["body"]["value"] = {0.1};
        require(session.dispatch(invalid)["ok"], "float32 boundary rounding was rejected");
        runtime.filters[0]["controls"][0]["minValue"] = {0.0};
        auto batch = request("replace", {{"filters", ansel::Json::array({
            {{"id","test/filter-b"},{"stackIdx",0},{"controls",ansel::Json::array()}},
            {{"id","test/filter-a"},{"stackIdx",1},{"controls",ansel::Json::array({{{"id","Strength"},{"value",{0.25}}}})}}
        })}});
        require(session.dispatch(batch)["ok"], "valid preset rejected");
        require(session.snapshot()["stack"][0] == "test/filter-b", "stack order was not applied");
        auto invalidBatch = batch; invalidBatch["body"]["filters"][1]["controls"][0]["value"] = {9.0};
        const auto beforeBatch = runtime.writes;
        require(session.dispatch(invalidBatch)["status"] == 400 && runtime.writes == beforeBatch, "invalid preset partially applied");
        require(session.dispatch(request("mods",{{"globalEnable",false}}))["ok"], "mods disable failed");
        require(runtime.filters[0]["enabled"] == false && runtime.filters[1]["enabled"] == false, "mods did not disable selected effects");
        require(session.dispatch(request("mods",{{"globalEnable",true}}))["ok"], "mods enable failed");
        require(session.dispatch(request("reset",{{"stackIdx",1}}))["ok"], "reset failed");
        require(runtime.filters[0]["controls"][0]["currentValue"][0] == 0.5, "reset did not restore session baseline");
        auto stale = request("clear",ansel::Json::object()); session.reload();
        require(session.dispatch(stale)["status"] == 409, "stale effect handles accepted after reload");
        require(session.dispatch(request("clear",ansel::Json::object()))["ok"], "clear failed");
        require(session.snapshot()["stack"].empty(), "stack did not clear");
        std::cout << "PASS: native catalog, uniforms, range validation, expiry, preset validation/order, toggle, reset and reload guards\n";
        return 0;
    } catch(const std::exception &error) { std::cerr << "FAIL: " << error.what() << '\n'; return 1; }
}
