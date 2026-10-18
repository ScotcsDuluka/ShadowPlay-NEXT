'use strict'
// JS shim for the genuine native addon (node v11 ABI — replaced).
const make = require('./generic-addon.js');

// The OSC page reads info.GPU[0] at boot — an empty shape crashes it
// (TypeError: Cannot read property '0' of undefined). Return the exact
// shape the genuine node answers on this machine (GTX 1080 Ti).
const HW_INFO = {
  GPU: [{
    LongGPUName: 'NVIDIA GeForce GTX 1080 Ti',
    ActualVRAMSize: '11264',
    GPURAMType: 'GDDR5X',
    VBIOSVersion: '86.02.39.00.71',
    IsQuadro: '0',
    DeviceId: '1b06',
    VendorId: '10de',
    SubSystemId: '120f',
    SubVendorId: '120f',
    SystemType: 'DESKTOP',
    BrandType: '1',
    PhysicalGPUHandle: '0000000000000100',
    GPUArchitecture: '304',
    GPUArchRevision: '161',
    GPUArchVersion: '65552',
    GPUArchImplementation: '2',
    IsPrimary: '1',
  }],
};

module.exports = make({
  GetHardwareInformation: function (doReply) {
    doReply(null, HW_INFO);
  },
});
