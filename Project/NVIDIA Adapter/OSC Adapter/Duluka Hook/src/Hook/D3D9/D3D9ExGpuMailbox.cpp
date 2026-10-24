// Opt-in, trusted D3D9Ex GPU mailbox receiver. The actual game D3D9Ex
// device never crosses IPC. This is deliberately NOT called by Present.
#include "../../../include/d3d9ex_gpu_mailbox.h"
#include "../../Renderer/D3D9/D3D9Renderer.h"
#include <atomic>
#include <cstdint>
#include <mutex>

namespace duluka::d3d9ex_mailbox {
namespace {
    std::mutex g_mutex;
    HANDLE g_mapping = nullptr;
    Mailbox *g_wire = nullptr;
    HANDLE g_producerProcess = nullptr;
    LONG g_producerPid = 0;
    IDirect3DDevice9Ex *g_activeDevice = nullptr; // non-owning
    LONG64 g_committed = 0;
    LONG g_committedProducerPid = 0;

    LONG Read32(volatile LONG *p) { return InterlockedCompareExchange(p, 0, 0); }
    LONG64 Read64(volatile LONG64 *p) {
        return InterlockedCompareExchange64(p, 0, 0);
    }
    LONG64 Packed(const LUID& luid) {
        return static_cast<LONG64>(
            (static_cast<std::uint64_t>(
                static_cast<std::uint32_t>(luid.HighPart)) << 32) |
            luid.LowPart);
    }

    bool OptedIn() {
        char value[8]{};
        const DWORD length = GetEnvironmentVariableA(
            "NVIDIA_OSC_ADAPTER_D3D9EX_MAILBOX_RECEIVER_PREVIEW",
            value, sizeof(value));
        return length == 1 && value[0] == '1';
    }

    void CloseProducer() {
        if (g_producerProcess) CloseHandle(g_producerProcess);
        g_producerProcess = nullptr;
        g_producerPid = 0;
    }
    void CloseMapping() {
        if (g_wire) UnmapViewOfFile(g_wire);
        if (g_mapping) CloseHandle(g_mapping);
        g_wire = nullptr;
        g_mapping = nullptr;
    }
    bool Connect() {
        if (g_wire) return g_wire->magic == Magic && g_wire->version == Version;
        wchar_t overrideName[128]{};
        const DWORD n = GetEnvironmentVariableW(NameEnvironment,
            overrideName, static_cast<DWORD>(_countof(overrideName)));
        if (n >= _countof(overrideName)) return false;
        const wchar_t *name = n ? overrideName : Name;
        g_mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE,
            FALSE, name);
        if (!g_mapping) return false;
        g_wire = static_cast<Mailbox *>(MapViewOfFile(g_mapping,
            FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(Mailbox)));
        if (!g_wire || g_wire->magic != Magic || g_wire->version != Version) {
            CloseMapping();
            return false;
        }
        return true;
    }
    bool AliveOwner(LONG pid) {
        if (pid <= 0 || pid == static_cast<LONG>(GetCurrentProcessId()))
            return false;
        if (g_producerPid != pid || !g_producerProcess) {
            CloseProducer();
            g_producerProcess = OpenProcess(SYNCHRONIZE, FALSE,
                static_cast<DWORD>(pid));
            g_producerPid = g_producerProcess ? pid : 0;
        }
        return g_producerProcess &&
            WaitForSingleObject(g_producerProcess, 0) == WAIT_TIMEOUT;
    }
    bool GameLuid(IDirect3DDevice9Ex *device, LONG64 &packed) {
        D3DDEVICE_CREATION_PARAMETERS params{};
        if (FAILED(device->GetCreationParameters(&params))) return false;
        IDirect3D9 *owner = nullptr;
        if (FAILED(device->GetDirect3D(&owner)) || !owner) return false;
        IDirect3D9Ex *ownerEx = nullptr;
        const HRESULT queried = owner->QueryInterface(__uuidof(IDirect3D9Ex),
            reinterpret_cast<void **>(&ownerEx));
        owner->Release();
        if (FAILED(queried) || !ownerEx) return false;
        LUID luid{};
        const HRESULT result = ownerEx->GetAdapterLUID(params.AdapterOrdinal,
            &luid);
        ownerEx->Release();
        if (FAILED(result)) return false;
        packed = Packed(luid);
        return packed != 0;
    }

    struct Offer {
        LONG producerPid = 0;
        LONG64 luid = 0;
        LONG64 generation = 0;
        LONG64 ready = 0;
        std::uint64_t sharedHandle = 0;
        std::uint32_t width = 0, height = 0, format = 0;
    };
    bool Snapshot(Offer &offer) {
        if (!g_wire) return false;
        // Host owns the seqlock, even value means stable metadata.
        const LONG seq1 = Read32(&g_wire->sequence);
        if (seq1 & 1) return false;
        MemoryBarrier();
        offer.producerPid = Read32(&g_wire->producerPid);
        offer.luid = Read64(&g_wire->targetAdapterLuid);
        offer.generation = Read64(&g_wire->generation);
        offer.ready = Read64(&g_wire->readyGeneration);
        offer.sharedHandle = g_wire->legacySharedHandle;
        offer.width = g_wire->width;
        offer.height = g_wire->height;
        offer.format = g_wire->format;
        MemoryBarrier();
        const LONG seq2 = Read32(&g_wire->sequence);
        return seq1 == seq2 && !(seq2 & 1);
    }
    // Release ACK is a resource-lifetime guarantee, not a best-effort
    // notification. Never report retirement before the Hook's alias has
    // actually been dropped (or another reset already retired it).
    bool ReleaseCurrent(bool acknowledge = true) {
        if (g_activeDevice) {
            if (!d3d9::UnbindExSharedGpuTexture(g_activeDevice)) {
                // A device Reset or renderer replacement may have already
                // released the alias. The published bound flag is lock-free.
                std::uint64_t luid = 0, skips = 0;
                bool isEx = false, bound = false;
                d3d9::GetAdapterStatus(luid, isEx, bound, skips);
                if (bound)
                    return false; // alias release unverified: never ACK
            }
            g_activeDevice = nullptr;
        }
        // On producer restart, old ACKs must NOT pollute the new owner's
        // generation namespace (which can restart from 1).
        if (acknowledge && g_wire && g_committed)
            InterlockedExchange64(&g_wire->releasedGeneration, g_committed);
        g_committed = 0;
        g_committedProducerPid = 0;
        return true;
    }
    void DisconnectConsumer() {
        ReleaseCurrent();
        if (g_wire && Read32(&g_wire->consumerPid) ==
                static_cast<LONG>(GetCurrentProcessId())) {
            InterlockedExchange(&g_wire->consumerCapabilities, 0);
            InterlockedExchange64(&g_wire->consumerAdapterLuid, 0);
            InterlockedExchange(&g_wire->consumerPid, 0);
        }
        CloseProducer();
        CloseMapping();
    }
    bool Register(LONG64 gameLuid) {
        if (!g_wire) return false;
        const LONG me = static_cast<LONG>(GetCurrentProcessId());
        const LONG previous = Read32(&g_wire->consumerPid);
        if (previous != 0 && previous != me)
            return false; // do not steal another registered game consumer
        InterlockedExchange64(&g_wire->consumerAdapterLuid, gameLuid);
        InterlockedExchange(&g_wire->consumerCapabilities, StageCommitCapability);
        MemoryBarrier();
        InterlockedExchange(&g_wire->consumerPid, me);
        return true;
    }
    bool Valid(const Offer &o, LONG64 gameLuid) {
        if (o.luid != gameLuid || !o.generation ||
            o.ready != o.generation || !o.sharedHandle ||
            o.format != Bgra8Format ||
            !o.width || !o.height || o.width > 8192 || o.height > 8192 ||
            static_cast<std::uint64_t>(o.width) * o.height * 4 >
                64ull * 1024 * 1024)
            return false;
#if defined(_M_IX86)
        if (o.sharedHandle >> 32) return false;
#endif
        return true;
    }
}

bool Pump(IDirect3DDevice9Ex *device) {
    if (!device || !OptedIn()) return false;
    // Two pumping threads must not be allowed to issue D3D9Ex calls
    // simultaneously. Try-lock instead of waiting in the device thread.
    std::unique_lock<std::mutex> lock(g_mutex, std::try_to_lock);
    if (!lock.owns_lock()) return false;
    LONG64 gameLuid = 0;
    if (!GameLuid(device, gameLuid)) return false;
    if (!Connect()) {
        if (g_wire) DisconnectConsumer();
        return false;
    }
    Offer offer{};
    if (!Snapshot(offer)) return false;
    // Reject an attempted owner replacement while the previous Host
    // still exists. Once it exits, retire the old alias without ACKing
    // the old generation in a NEW producer's mailbox epoch.
    if (g_committed && g_committedProducerPid != offer.producerPid) {
        if (g_producerProcess &&
            WaitForSingleObject(g_producerProcess, 0) == WAIT_TIMEOUT)
            return false;
        if (!ReleaseCurrent(false)) return false;
        CloseProducer();
    }
    if (!AliveOwner(offer.producerPid)) {
        DisconnectConsumer();
        return false;
    }
    if (g_activeDevice && g_activeDevice != device)
        return false; // caller must Reset/Unbind on the owning device
    if (!Register(gameLuid)) return false;

    if (!offer.generation) {
        if (!g_committed) return false;
        return ReleaseCurrent(); // only ACK after alias release
    }
    if (!Valid(offer, gameLuid) || offer.generation == g_committed)
        return false;

    const auto handle = reinterpret_cast<HANDLE>(
        static_cast<std::uintptr_t>(offer.sharedHandle));
    if (!d3d9::StageExSharedGpuTexture(device, handle,
            offer.width, offer.height,
            static_cast<std::uint64_t>(offer.generation)))
        return false;
    Offer confirm{};
    if (!Snapshot(confirm) ||
        confirm.producerPid != offer.producerPid ||
        confirm.generation != offer.generation ||
        confirm.ready != offer.generation ||
        confirm.sharedHandle != offer.sharedHandle ||
        !AliveOwner(offer.producerPid)) {
        d3d9::DiscardExPendingGpuTexture(device);
        return false;
    }
    InterlockedExchange64(&g_wire->stagedGeneration, offer.generation);
    // The Host's readyGeneration is the assertion that its D3D11 query
    // completed. Do not wait for any GPU completion inside the Hook.
    if (!d3d9::CommitExSharedGpuTexture(device,
            static_cast<std::uint64_t>(offer.generation))) {
        d3d9::DiscardExPendingGpuTexture(device);
        return false;
    }
    const LONG64 old = g_committed;
    g_committed = offer.generation;
    g_committedProducerPid = offer.producerPid;
    g_activeDevice = device;
    InterlockedExchange64(&g_wire->committedGeneration, g_committed);
    // Commit releases the previous alias before returning.
    if (old) InterlockedExchange64(&g_wire->releasedGeneration, old);
    return true;
}

void OnDeviceReset(IDirect3DDevice9 *device) {
    std::lock_guard<std::mutex> lock(g_mutex);
    if (g_activeDevice && (!device || g_activeDevice == device)) {
        ReleaseCurrent();
    }
}
void Shutdown() {
    std::lock_guard<std::mutex> lock(g_mutex);
    DisconnectConsumer();
}
}
