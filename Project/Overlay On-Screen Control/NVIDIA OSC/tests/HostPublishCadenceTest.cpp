// Regression: GPU_SHARED frameNumber must reflect new CEF content, not
// metadata, periodic timer retries, or desktop idle Presents.
#include "../src/OscFramePublisher/Common/OscPublishCadence.h"
#include "../src/OscFramePublisher/Common/OscD3D9CpuMirrorPolicy.h"
#include "../src/OscFramePublisher/Common/OscHookTransportPolicy.h"
#ifdef NDEBUG
#error "Cadence assertions must be enabled in Release tests"
#endif
#include <cassert>
#include <cstdint>
#include <iostream>
int main() {
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
  std::cout<<"GPU_SHARED_PUBLISH_CADENCE_PASS\n";
  return 0;
}
