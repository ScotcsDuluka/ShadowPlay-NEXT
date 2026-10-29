// Regression: GPU_SHARED frameNumber must reflect new CEF content, not
// metadata, periodic timer retries, or desktop idle Presents.
#include "../src/OscFramePublisher/Common/OscPublishCadence.h"
#include "../src/OscFramePublisher/Common/OscD3D9CpuMirrorPolicy.h"
#include "../src/OscFramePublisher/Common/OscHookTransportPolicy.h"
#include "../src/OscDesktopFps.h"
#include "../src/OscModeFrameBoost.h"
#ifdef NDEBUG
#error "Cadence assertions must be enabled in Release tests"
#endif
#include <cassert>
#include <cstdint>
#include <iostream>
int main() {
  osc::ModeFrameBoost boost;
  assert(!boost.Active(0));
  assert(boost.DesktopPresentLimit(0,75)==75);
  assert(boost.CefLimit(0,30)==30);
  boost.Start(100);
  assert(boost.Active(100) && boost.Active(5099));
  assert(!boost.Active(5100)); // exactly five seconds, not the old 10s burst
  assert(boost.DesktopPresentLimit(5099,75)==240);
  assert(boost.CefLimit(5099,15)==60);
  assert(boost.CefLimit(5099,75,true)==240);
  assert(boost.CefLimit(5100,75,true)==75);
  assert(osc::ModeFrameBoost::PaintDelayMs(true)==4);
  assert(osc::ModeFrameBoost::PaintDelayMs(false)==16);
  assert(boost.DesktopPresentLimit(5100,75)==75);
  assert(boost.CefLimit(5100,15)==15);
  boost.Start(1000);
  boost.Start(2000); // a new mode change restarts the five-second window
  assert(boost.Active(6999) && !boost.Active(7000));
  boost.Stop();
  assert(!boost.Active(2001));
  assert(boost.DesktopPresentLimit(2001,240)==240); // preserve Desktop setting
  for (const int fps : {15,30,60,75,120,144,165,240}) {
      assert(osc::CefDesktopFps(fps,true)==fps);
      assert(osc::CefDesktopFps(fps,false)==(fps<60?fps:60));
  }
  assert(osc::CefHookFps(true)==240);
  assert(osc::CefHookFps(false)==60);
  assert(osc::CefDesktopFps(0,true)==60);
  assert(osc::CefDesktopFps(241,true)==60);
  // D3D9 GPU is an opt-in WINDOWED DWM compositor, not D3D9Ex sharing
  // or a false promise about Fullscreen Exclusive.
  const auto* d3d9=osc::hook_transport::Find("d3d9");
  const auto* d3d9ex=osc::hook_transport::Find("d3d9ex");
  assert(d3d9 && d3d9->rendererImplemented && d3d9->gpuModeAvailable);
  assert(osc::hook_transport::Allowed(*d3d9,"auto"));
  assert(osc::hook_transport::Allowed(*d3d9,"cpu"));
  assert(osc::hook_transport::Allowed(*d3d9,"gpu"));
  assert(d3d9ex && !osc::hook_transport::Allowed(*d3d9ex,"gpu"));
  // CPU v1 mapping must NOT need the unrelated keyed D3D11 GPU mirror.
  assert(osc::d3d9_cpu_mirror::PublishCpuBridgeWithoutNtGpuMirror(
      osc_frame_bridge::ConsumerRenderApiD3D9));
  assert(!osc::d3d9_cpu_mirror::PublishCpuBridgeWithoutNtGpuMirror(
      osc_frame_bridge::ConsumerRenderApiD3D11));
  osc::frame_cadence::FrameCadence c{};
  std::uint64_t frame=0;
  auto metadata=[&](std::uint64_t source, bool visible, bool copied){
      frame=c.Next(frame,source,visible,copied);
      return frame;
  };
  assert(metadata(0,true,false)==0);
  for (int i=0;i<120;++i) assert(metadata(0,true,false)==0);
  assert(metadata(1,true,true)==1); // first CEF paint
  for (int i=0;i<200;++i) assert(metadata(1,true,true)==1);
  assert(metadata(2,true,false)==1); // GPU Acquire/Copy failed
  assert(metadata(1,true,true)==1); // stale / out of order
  assert(metadata(2,true,true)==2); // successful retry
  for (int i=0;i<200;++i) assert(metadata(2,true,true)==2);
  assert(metadata(2,false,false)==0); // hide clears Hook cache
  assert(metadata(2,true,false)==0);  // visible alone cannot fake pixels
  assert(metadata(3,true,true)==3);   // new CEF pixels after re-show
  assert(metadata(3,true,false)==3);  // 1 Hz idle Present
  assert(metadata(3,true,true)==3);   // forced full re-read, same source
  assert(metadata(4,false,true)==0);  // hidden frame is not published
  assert(metadata(4,true,true)==4);   // eligible when visible again
  assert(metadata(5,true,true)==5);
  assert(metadata(9,true,true)==6); // coalesced CEF paints, one publish
  for(int i=0;i<10000;++i) assert(metadata(9,true,true)==6);
  assert(c.lastPublishedSourceSerial==9);
  assert(c.latestIssued==6);
  // DesktopFpsLimiter paces from the Present START on a deadline grid so the
  // present submission, Commit and wake-up stay inside the interval.
  {
    osc::DesktopFpsLimiter lim;
    assert(lim.RemainingUs(12345)==0);        // first present is immediate
    lim.MarkPresented(1000,240);              // deadline 1000+4167
    assert(lim.RemainingUs(1000)==4167);
    assert(lim.RemainingUs(3000)==2167);
    assert(lim.RemainingUs(5167)==0);
    lim.MarkPresented(5200,240);              // 33us late keeps the grid (9334)
    assert(lim.RemainingUs(5200)==4134);
    lim.MarkPresented(12000,240);             // late < interval: grid holds
    assert(lim.RemainingUs(12000)==1501);     // next beat 13501
    lim.MarkPresented(20000,240);             // stalled past the grid: re-anchor
    assert(lim.RemainingUs(20000)==4167);     // deadline 24167, no burst
  }
  {
    osc::DesktopFpsLimiter lim;
    lim.MarkPresented(0,60);
    assert(lim.RemainingUs(0)==16667);
    assert(lim.RemainingUs(16667)==0);
    lim.MarkPresented(16667,60);
    assert(lim.RemainingUs(16667)==16667);
    osc::DesktopFpsLimiter bad;
    bad.MarkPresented(0,999);                 // invalid rate falls back to 60
    assert(bad.RemainingUs(0)==16667);
  }
  std::cout<<"GPU_SHARED_PUBLISH_CADENCE_PASS\n";
  return 0;
}
