#include "../src/OscVisibilityConvergence.h"
#include "../src/OscToggleRequestGuard.h"
#ifdef NDEBUG
#error "Visibility regression assertions must be enabled"
#endif
#include <cassert>
#include <iostream>
#include <thread>
#include <vector>
#include <atomic>

int main(int argc, char** argv) {
    if (argc == 4 && std::string(argv[1]) == "--emit-js") {
        std::cout << osc::VisibilityPageScript({std::stoull(argv[2]), std::string(argv[3]) == "open", true});
        return 0;
    }
    osc::VisibilityConvergence state;
    assert(!state.Snapshot().open && !state.Snapshot().pending);
    auto first = state.Request(true); // browser/injector is not ready
    auto close = state.Request(false);
    assert(!state.IsCurrent(first) && state.IsCurrent(close));
    assert(!state.Observe(true, first.generation));
    assert(!state.Observe(true, close.generation)); // late async original open
    assert(!state.Acknowledge(true, first.generation));
    assert(!state.Acknowledge(false, first.generation));
    assert(state.Snapshot().pending && !state.Snapshot().open);
    assert(state.Observe(false, close.generation));
    assert(state.Snapshot().pending); // route alone is not asynchronous-service completion
    assert(state.Acknowledge(false, close.generation));
    assert(!state.ObserveRoute(true, false, close.generation)); // same-gen queued old route after ACK
    assert(!state.Snapshot().open);
    auto open = state.Request(true);
    assert(state.Acknowledge(true, open.generation));
    assert(state.Observe(false, open.generation)); // genuine Escape after acknowledgement
    assert(!state.Snapshot().open && !state.Snapshot().pending);
    assert(!state.IsCurrent(open));
    auto noop1 = state.Request(false), noop2 = state.Request(false);
    assert(noop2.generation > noop1.generation && !state.IsCurrent(noop1));
    state.Stop();
    assert(!state.IsCurrent(noop2) && !state.Snapshot().pending);
    const auto unavailable = state.Request(true, 100);
    assert(!state.Expired(6099) && state.Expired(6100)); // no cefQuery needed
    assert(!state.Fail(unavailable.generation - 1));
    assert(state.Fail(unavailable.generation));
    const auto recovery = state.Snapshot();
    assert(recovery.recovering && !recovery.open && recovery.pending);
    assert(!state.Expired(100000) && !state.Fail(recovery.generation));
    assert(!state.Observe(true, unavailable.generation));
    assert(!state.Observe(true, recovery.generation));
    const auto retry = state.Request(true);
    assert(!retry.recovering && state.IsCurrent(retry));
    for (int cycle = 0; cycle < 2000; ++cycle) {
        const auto pending = state.Request(cycle % 2 == 0);
        assert(!state.Observe(!pending.open, pending.generation));
        assert(!state.Acknowledge(pending.open, pending.generation - 1));
        assert(state.Snapshot().open == pending.open);
        assert(state.Acknowledge(pending.open, pending.generation));
    }
    using Guard = osc::ToggleRequestGuard;
    Guard guard;
    assert(Guard::Header("POST /toggle HTTP/1.1\r\nx-osc-request-id: safe-id \r\n\r\n", "X-OSC-Request-Id") == "safe-id");
    assert(!Guard::Header("POST /toggle HTTP/1.1\r\nX-OSC-Request-Id:\r\nX-OSC-Request-Id: second\r\n\r\n", "X-OSC-Request-Id"));
    assert(!Guard::Header("POST /toggle HTTP/1.1\r\nX-OSC-Created-At: 1000\r\nx-osc-created-at: 1001\r\n\r\n", "X-OSC-Created-At"));
    assert(!Guard::SafeId("bad\r\nheader") && Guard::SafeId("helper-42:osc_toggle.1"));
    assert(guard.Queue({"expired", 1000, 2}, 3001).status == Guard::Status::Stale);
    assert(guard.Queue({"future", 4001, 2}, 3000).status == Guard::Status::Stale);
    auto expired = guard.Queue({"expires-in-queue", 1000, 2}, 2000);
    assert(!guard.Consume(expired.token, 3001));
    auto canceled = guard.Queue({"timed-out", 5000, 2}, 5000);
    guard.Cancel(canceled.token);
    assert(!guard.Consume(canceled.token, 5001)); // delayed SendMessage cannot apply
    std::atomic<int> queued{0}, busy{0};
    std::atomic<std::uint64_t> token{0};
    std::vector<std::thread> threads;
    for (int i = 0; i < 8; ++i) threads.emplace_back([&] {
        const auto ticket = guard.Queue({"same-real-keypress", 6000, 2}, 6001);
        if (ticket.status == Guard::Status::Queued) { ++queued; token = ticket.token; }
        if (ticket.status == Guard::Status::Busy) ++busy;
    });
    for (auto& thread : threads) thread.join();
    assert(queued == 1 && busy == 7);
    assert(guard.Consume(token, 6002));
    assert(!guard.Consume(token, 6002));
    assert(guard.Queue({"same-real-keypress", 6000, 2}, 6003).status == Guard::Status::Duplicate);
    for (int i = 0; i < 20; ++i) {
        const auto ticket = guard.Queue({"jitter-" + std::to_string(i), 7000u + i * 37u, 2}, 7000u + i * 37u + (i % 4) * 11u);
        assert(ticket.status == Guard::Status::Queued);
        assert(guard.Consume(ticket.token, 7000u + i * 37u + 50));
    }
    std::cout << "OSC_VISIBILITY_AND_REQUEST_GUARD_PASS cycles=2000 jitter_toggles=20 concurrent_duplicate=8\n";
}
