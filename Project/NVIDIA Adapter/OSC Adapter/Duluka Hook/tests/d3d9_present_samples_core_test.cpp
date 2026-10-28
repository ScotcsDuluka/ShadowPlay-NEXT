// Exercises the actual recorder used by Hook wrappers. No Windows, DLL load,
// graphics device, bridge mapping or desktop operations.
#include "../include/d3d9_present_sample_recorder.h"
#include <barrier>
#include <iostream>
#include <latch>
#include <memory>
#include <thread>
#include <vector>

using namespace duluka::diagnostics;
using Recorder = D3D9PresentSampleRecorder;
using Result = Recorder::Result;
namespace duluka::diagnostics {
struct D3D9PresentSampleRecorderTestAccess {
    static bool Lock(Recorder& recorder) { return !recorder.lock_.test_and_set(std::memory_order_acquire); }
    static void Unlock(Recorder& recorder) { recorder.lock_.clear(std::memory_order_release); }
};
}
namespace {
std::atomic<std::uint64_t> clockValue{1};
std::uint64_t Clock() noexcept { return clockValue.fetch_add(10); }
unsigned failures = 0, checks = 0;
void Check(bool condition, const char* label) {
    ++checks;
    if (!condition) { ++failures; std::cerr << "FAIL " << label << '\n'; }
}
bool Accounted(const D3D9PresentSamplesSnapshotV1& snapshot) {
    return snapshot.attemptedCalls == snapshot.sampleCount + snapshot.busyDrops +
        snapshot.overflowDrops + snapshot.reentrantExcluded + snapshot.unfinishedOrStoppedCalls;
}
void Finish(Recorder& recorder, const Recorder::CallToken& token, std::uint32_t thread=1) {
    const auto before=Clock();
    const auto after=Clock();
    recorder.Finish(token, before, after, 0, thread, D3D9PresentKind);
}
}
int main() {
    auto storage = std::make_unique<Recorder>(Clock);
    auto& recorder = *storage;
    std::vector<D3D9PresentSampleV1> samples(D3D9PresentSamplesCapacity);
    const auto bytes = static_cast<std::uint32_t>(samples.size()*sizeof(samples[0]));
    D3D9PresentSamplesSnapshotV1 snapshot{};
    auto Read = [&] { return recorder.Read(&snapshot,sizeof(snapshot),samples.data(),bytes); };
    Check(recorder.ActiveEpoch()==0 && recorder.Stop()==Result::InvalidState &&
        Read()==Result::InvalidState, "disabled state has no fabricated session");
    Check(recorder.Begin(0,1000)==Result::InvalidParameter &&
        recorder.Begin(1,0)==Result::InvalidParameter &&
        recorder.Begin(D3D9PresentSamplesCapacity+1,1000)==Result::InvalidParameter,
        "invalid capacity and frequency");
    Check(recorder.Begin(2,1000)==Result::Success, "begin");
    auto epoch=recorder.ActiveEpoch();
    Check(Read()==Result::Busy && recorder.Begin(2,1000)==Result::Busy,"active control calls rejected");
    auto outer=recorder.Enter(epoch,Clock());
    auto inner=recorder.Enter(epoch,Clock());
    Check(outer.epoch && !inner.epoch,"nested call excluded");
    Finish(recorder,inner); Finish(recorder,outer);
    Finish(recorder,recorder.Enter(epoch,Clock()));
    Finish(recorder,recorder.Enter(epoch,Clock()));
    Check(recorder.Stop()==Result::Success && Read()==Result::Success,"stop/read");
    Check(Accounted(snapshot) && snapshot.attemptedCalls==4 && snapshot.sampleCount==2 &&
        snapshot.reentrantExcluded==1 && snapshot.overflowDrops==1,"overflow and reentrant accounting");
    const auto saved=snapshot;
    Check(recorder.Read(&snapshot,sizeof(snapshot)-1,samples.data(),bytes)==Result::InvalidParameter &&
        recorder.Read(&snapshot,sizeof(snapshot),nullptr,bytes)==Result::InvalidParameter &&
        recorder.Read(&snapshot,sizeof(snapshot),samples.data(),1)==Result::InvalidParameter &&
        recorder.Read(&snapshot,sizeof(snapshot),samples.data(),0)==Result::InsufficientBuffer,
        "buffer validation");
    Finish(recorder,recorder.Enter(0,Clock()));
    Check(Read()==Result::Success && std::memcmp(&saved,&snapshot,sizeof(saved))==0,"stopped data frozen");

    Check(recorder.Begin(8,1000)==Result::Success,"begin inflight session");
    const auto oldEpoch=recorder.ActiveEpoch();
    std::latch entered{1}, release{1};
    std::thread inflight([&] {
        auto token=recorder.Enter(oldEpoch,Clock());
        entered.count_down(); release.wait(); Finish(recorder,token,2);
    });
    entered.wait();
    Check(recorder.Stop()==Result::Success && Read()==Result::Success &&
        snapshot.unfinishedOrStoppedCalls==1 && Accounted(snapshot),"stop never waits for inflight call");
    Check(recorder.Begin(8,1000)==Result::Success,"restart while old call remains inflight");
    const auto newEpoch=recorder.ActiveEpoch();
    release.count_down(); inflight.join();
    Check(!recorder.Enter(oldEpoch,Clock()).epoch,"stale delayed entry rejected");
    Finish(recorder,recorder.Enter(newEpoch,Clock()));
    Check(recorder.Stop()==Result::Success && Read()==Result::Success &&
        snapshot.sessionId>oldEpoch && snapshot.attemptedCalls==1 && snapshot.sampleCount==1 &&
        Accounted(snapshot),"stale completion cannot enter restarted session");

    Check(recorder.Begin(8,1000)==Result::Success,"begin deterministic contention");
    auto token=recorder.Enter(recorder.ActiveEpoch(),Clock());
    Check(D3D9PresentSampleRecorderTestAccess::Lock(recorder),"hold recorder control lock");
    Check(recorder.Stop()==Result::Busy && Read()==Result::Busy,"control contention returns without waiting");
    Finish(recorder,token);
    D3D9PresentSampleRecorderTestAccess::Unlock(recorder);
    Check(recorder.Stop()==Result::Success && Read()==Result::Success && snapshot.busyDrops==1 &&
        snapshot.sampleCount==0 && Accounted(snapshot),"writer contention counted without waiting");

    for(unsigned round=0;round<10;++round) {
        Check(recorder.Begin(32,1000)==Result::Success,"begin concurrent session");
        const auto concurrentEpoch=recorder.ActiveEpoch();
        std::barrier start{9};
        std::vector<std::thread> writers;
        for(unsigned worker=0;worker<8;++worker) writers.emplace_back([&,worker] {
            start.arrive_and_wait();
            for(unsigned call=0;call<100;++call)
                Finish(recorder,recorder.Enter(concurrentEpoch,Clock()),worker+1);
        });
        start.arrive_and_wait();
        for(auto& writer:writers) writer.join();
        Check(recorder.Stop()==Result::Success && Read()==Result::Success &&
            snapshot.attemptedCalls==800 && snapshot.sampleCount>0 && snapshot.sampleCount<=32 &&
            !snapshot.unfinishedOrStoppedCalls && Accounted(snapshot),"concurrent writers conserve accounting");
    }
    std::cout << "CPU_RECORDER checks=" << checks << " failures=" << failures << '\n';
    std::cout << "No GPU/Hook DLL/game operations. Runtime performance remains UNVERIFIED.\n";
    return failures ? 1 : 0;
}
