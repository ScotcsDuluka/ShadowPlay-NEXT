#pragma once
// Platform-neutral implementation used by the actual Hook wrappers and the
// CPU-only tests. Clock reads and thread IDs are supplied by the caller.
#include <atomic>
#include <cstring>
#include <limits>
#include "d3d9_present_samples.h"

namespace duluka::diagnostics
{
    struct D3D9PresentSampleRecorderTestAccess;

    class D3D9PresentSampleRecorder
    {
    public:
        using Clock = std::uint64_t (*)() noexcept;
        enum class Result
        {
            Success, InvalidParameter, Busy, InvalidState,
            EpochExhausted, InsufficientBuffer
        };
        struct CallToken
        {
            std::uint64_t entryQpc = 0;
            std::uint32_t epoch = 0;
        };

        explicit D3D9PresentSampleRecorder(Clock clock) noexcept : clock_(clock) {}
        D3D9PresentSampleRecorder(const D3D9PresentSampleRecorder&) = delete;
        D3D9PresentSampleRecorder& operator=(const D3D9PresentSampleRecorder&) = delete;

        std::uint32_t ActiveEpoch() const noexcept
        {
            return activeEpoch_.load(std::memory_order_acquire);
        }

        // Call ActiveEpoch first: zero is the single-load disabled fast path.
        // A delayed Enter with an old epoch is harmless after Stop/restart.
        CallToken Enter(std::uint32_t epoch, std::uint64_t entryQpc) noexcept
        {
            if (!epoch || !Increment(attempts_, epoch)) return {};
            if (sampleDepth_)
            {
                Increment(reentrant_, epoch);
                return {};
            }
            ++sampleDepth_;
            return { entryQpc, epoch };
        }

        // Enter/Finish for a nonzero token must run on the same thread and
        // recorder. No lock is held between them, including during Original.
        void Finish(const CallToken& token, std::uint64_t beforeOriginalQpc,
            std::uint64_t afterOriginalQpc, std::int32_t result,
            std::uint32_t threadId, std::uint32_t presentKind) noexcept
        {
            if (!token.epoch) return;
            --sampleDepth_;
            if (ActiveEpoch() != token.epoch) return;
            TryLock lock(*this);
            if (!lock)
            {
                Increment(busyDrops_, token.epoch);
                return;
            }
            if (ActiveEpoch() != token.epoch) return;
            if (sampleCount_ >= snapshot_.capacity)
            {
                ++overflow_;
                return;
            }
            samples_[sampleCount_++] = { token.entryQpc, beforeOriginalQpc,
                afterOriginalQpc, threadId, presentKind, result, 0 };
        }

        Result Begin(std::uint32_t capacity, std::uint64_t frequency) noexcept
        {
            if (!clock_ || !frequency || !capacity || capacity > D3D9PresentSamplesCapacity)
                return Result::InvalidParameter;
            TryLock lock(*this);
            if (!lock || ActiveEpoch()) return Result::Busy;
            if (lastEpoch_ == MaxCount) return Result::EpochExhausted;
            const auto epoch = ++lastEpoch_;
            const auto word = static_cast<std::uint64_t>(epoch) << 32;
            attempts_.store(word, std::memory_order_relaxed);
            busyDrops_.store(word, std::memory_order_relaxed);
            reentrant_.store(word, std::memory_order_relaxed);
            sampleCount_ = 0;
            overflow_ = 0;
            snapshot_ = {};
            snapshot_.size = sizeof(snapshot_);
            snapshot_.version = D3D9PresentSamplesVersion;
            snapshot_.performanceFrequency = frequency;
            snapshot_.sessionId = epoch;
            snapshot_.capacity = capacity;
            snapshot_.beganQpc = clock_();
            activeEpoch_.store(epoch, std::memory_order_release);
            return Result::Success;
        }

        Result Stop() noexcept
        {
            TryLock lock(*this);
            if (!lock) return Result::Busy;
            if (!ActiveEpoch()) return Result::InvalidState;
            activeEpoch_.store(0, std::memory_order_release);
            // Clearing the packed epochs freezes these counters without
            // waiting for delayed Enter/Finish calls from this session.
            const auto attempts = Freeze(attempts_);
            const auto busy = Freeze(busyDrops_);
            const auto reentrant = Freeze(reentrant_);
            snapshot_.stoppedQpc = clock_();
            snapshot_.sampleCount = sampleCount_;
            snapshot_.attemptedCalls = attempts;
            snapshot_.busyDrops = busy;
            snapshot_.overflowDrops = overflow_;
            snapshot_.reentrantExcluded = reentrant;
            const std::uint64_t accounted = sampleCount_ +
                static_cast<std::uint64_t>(busy) + overflow_ + reentrant;
            snapshot_.unfinishedOrStoppedCalls = attempts - accounted;
            if (attempts == MaxCount)
                snapshot_.flags |= D3D9PresentSamplesAttemptCounterSaturated;
            return Result::Success;
        }

        Result Read(D3D9PresentSamplesSnapshotV1* output, std::uint32_t outputBytes,
            D3D9PresentSampleV1* samples, std::uint32_t sampleBytes) noexcept
        {
            if (!output || outputBytes != sizeof(snapshot_) ||
                sampleBytes % sizeof(D3D9PresentSampleV1) || (sampleBytes && !samples))
                return Result::InvalidParameter;
            TryLock lock(*this);
            if (!lock || ActiveEpoch()) return Result::Busy;
            if (!snapshot_.sessionId || !snapshot_.stoppedQpc)
                return Result::InvalidState;
            const auto required = sampleCount_ * sizeof(D3D9PresentSampleV1);
            if (sampleBytes < required) return Result::InsufficientBuffer;
            if (required) std::memcpy(samples, samples_, required);
            *output = snapshot_;
            return Result::Success;
        }

    private:
        // Test access only controls this same lock for deterministic CPU-only
        // contention tests; it adds no exports, callbacks or Present work.
        friend struct D3D9PresentSampleRecorderTestAccess;
        static_assert(std::atomic<std::uint64_t>::is_always_lock_free);
        static constexpr auto MaxCount = (std::numeric_limits<std::uint32_t>::max)();
        class TryLock
        {
        public:
            explicit TryLock(D3D9PresentSampleRecorder& owner) noexcept
                : owner_(owner), acquired_(!owner.lock_.test_and_set(std::memory_order_acquire)) {}
            ~TryLock() { if (acquired_) owner_.lock_.clear(std::memory_order_release); }
            explicit operator bool() const noexcept { return acquired_; }
            TryLock(const TryLock&) = delete;
            TryLock& operator=(const TryLock&) = delete;
        private:
            D3D9PresentSampleRecorder& owner_;
            bool acquired_;
        };

        static bool Increment(std::atomic<std::uint64_t>& counter,
            std::uint32_t epoch) noexcept
        {
            auto value = counter.load(std::memory_order_relaxed);
            while (static_cast<std::uint32_t>(value >> 32) == epoch &&
                static_cast<std::uint32_t>(value) != MaxCount)
            {
                // Lock-free retry under counter contention. No OS/lock wait;
                // unlike the buffer try-lock, this has no bounded latency guarantee.
                if (counter.compare_exchange_weak(value, value + 1,
                        std::memory_order_relaxed)) return true;
            }
            return false;
        }
        static std::uint32_t Freeze(std::atomic<std::uint64_t>& counter) noexcept
        {
            return static_cast<std::uint32_t>(counter.exchange(0, std::memory_order_relaxed));
        }

        Clock clock_;
        std::atomic<std::uint32_t> activeEpoch_{ 0 };
        std::atomic_flag lock_ = ATOMIC_FLAG_INIT;
        // Epoch in high 32 bits prevents stale updates after session restart.
        std::atomic<std::uint64_t> attempts_{ 0 }, busyDrops_{ 0 }, reentrant_{ 0 };
        D3D9PresentSampleV1 samples_[D3D9PresentSamplesCapacity]{};
        D3D9PresentSamplesSnapshotV1 snapshot_{};
        std::uint32_t lastEpoch_ = 0, sampleCount_ = 0;
        std::uint64_t overflow_ = 0;
        // There is one production recorder. TLS deliberately spans its full
        // Enter-to-Finish scope, including a Stop/restart while in Original.
        inline static thread_local unsigned sampleDepth_ = 0;
    };
}
