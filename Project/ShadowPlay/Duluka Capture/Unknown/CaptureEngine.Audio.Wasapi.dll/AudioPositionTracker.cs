// AudioPositionTracker.cs — P13.2: the PURE, Linux-testable half of the
// position design (no COM, no threads, deterministic).
//
// ★ P13.4c (field evidence, OWNER runs 2026-08-28/29): the timeline moved
// from qpcPosition deltas to DevicePositionFrames deltas. The OWNER's
// endpoint fired 933 BACKWARDS qpc stamps in one 38s session and the
// qpc-delta timeline lost 5.65s of device time (track 32.93s vs session
// 38.56s) — qpcPosition measures WHEN the position was SAMPLED, not where
// the content is; sampling jitter (and lying drivers: USB/BT/DSP) makes
// qpc deltas useless as a content clock. DevicePositionFrames is the
// RENDER CURSOR — it advances with rendered content (silence included),
// which is exactly the content timeline this tracker exists to build.
// qpcPosition is demoted to the WALL ANCHOR (FirstQpc100ns → SyncMath)
// plus an anomaly counter (QpcAnomalies — evidence, no timeline impact).
//
// Consumes (frames, devicePositionFrames, qpcPosition100ns) per packet:
//
//   lastDevPosEnd = devPos + frames        (render cursor, END of content)
//   hole100ns     = (devPos − lastDevPosEnd) × 10⁷ / rate   (CONTENT time!)
//   bufferDur     = frames × 10⁷ / rate
//   lastEnd100ns  = FirstQpc + (lastDevPosEnd − firstDevPos) × 10⁷ / rate
//                   (content end mapped into the QPC wall domain)
//
// ★ PHASE A (OWNER-approved 2026-08-31) — the frozen-cursor rule. P13.4b
// field evidence: while nothing plays, the loopback endpoint delivers
// NOTHING and its render cursor FREEZES (devPos == prev end), so case-1
// sees hole = 0 and the whole idle span migrated to the track tail
// ([voice][voice][long tail silence]). The OWNER's refined gap policy:
//
//   devPos = MASTER TIMELINE, qpc = WALL-CLOCK ANCHOR / GAP DETECTOR
//
//   1) virtualPos > virtual end        → cursor-PROVEN hole (content time).
//      Unchanged P13.4c behavior — always padded, qpc never consulted.
//   2) cursor made NO forward progress (devPos ≤ prev raw end) AND the
//      wall proves elapsed time beyond the previous packet's own span
//      plus a jitter tolerance → IDLE gap: reconstruct silence from
//      (wallGap − prevDur) at THIS position. qpcDelta is never the raw
//      length (OWNER: "ห้ามเอา qpcDelta มาใช้เป็นระยะ silence ตรง ๆ") —
//      the packet's own duration is subtracted first.
//   3) otherwise → legacy semantics: continuity (hole=0) or overlap
//      max-policy (violation, never rewind) — P13.2 contracts preserved.
//
//   TimestampError packets judge NO gap at all (OWNER rule): continuity
//   placement, no wall-base update (their stamps are known-error),
//   evidence counter only.
//
// Why a monotonic wall base: the OWNER machine fired 933 BACKWARDS qpc
// stamps per 38s session. wallGap is measured against max(all qpc seen)
// so backwards noise yields wallGap ≤ 0 → no idle fabrication. The
// residual exposure is a huge FORWARD stamp lie under a frozen cursor —
// physically implausible (QPC is monotonic hardware) and bounded by the
// tap's 3600s cap; backwards noise — the documented real failure — is
// fully immune.
//
// Rebase: an idle pad advances the VIRTUAL timeline past the device's
// (frozen) cursor domain. _rebaseFrames accumulates the divergence so
// every later raw devPos maps back onto the virtual timeline — after the
// episode, normal cursor-delta math resumes seamlessly (proven by the
// Timeline-C freeze→resume test).
//
// Hardening (doc §5 Risks, adapted to devPos):
//   Risk #2 — devicePositionFrames == 0 mid-stream ("no cursor"): assume
//   continuity (devPos = lastDevPosEnd), flag it, never fabricate gaps.
//   Backwards devPos (device re-init / endpoint switch): flagged as
//   monotonicity violations; timeline NEVER rewinds (max policy).
//   Re-anchor rule: if the ANCHOR packet had devPos == 0, the first REAL
//   cursor re-anchors with no hole (pre-reanchor absolute positions were
//   never known — a measured gap there would be stream-sized fiction).
//
// All arithmetic is integer 100ns/frames — no drift accumulates because
// the hole recomputes from each packet's OWN cursor, never by summing.

using System;

namespace CaptureEngine.Audio.Wasapi
{
    /// <summary>Outcome of feeding one packet to the tracker.</summary>
    public struct AudioGapReport
    {
        /// <summary>True for the first packet: it anchors the timeline.</summary>
        public bool AnchoredNow;

        /// <summary>CONTENT gap between the previous packet's cursor end and
        /// this packet's cursor start, in 100ns (0 when continuous). Consumers
        /// apply policy (AudioTap pads every hole; logs only > 50ms).</summary>
        public long Hole100ns;

        /// <summary>Duration of THIS packet, in 100ns.</summary>
        public long BufferDur100ns;

        /// <summary>This packet's cursor was missing (0) and continuity was
        /// assumed instead (doc §5 Risk #2 fallback).</summary>
        public bool StampFallbackUsed;

        /// <summary>This packet's cursor started before the previous content
        /// ended (overlap/re-init). Timeline did NOT rewind.</summary>
        public bool MonotonicViolation;

        /// <summary>A real cursor arrived after a fallback (zero-cursor)
        /// anchor: the timeline re-anchored onto it. No gap is measurable
        /// across a re-anchor.</summary>
        public bool ReAnchoredNow;

        /// <summary>The packet's qpcPosition went backwards vs the previous
        /// packet's — WALL-DOMAIN noise only (the OWNER machine: 933/session).
        /// Evidence counter; ZERO timeline impact since P13.4c.</summary>
        public bool QpcAnomaly;

        /// <summary>The gap before this packet was reconstructed from QPC
        /// wall evidence because the device cursor was frozen (Phase-A
        /// case 2). Cursor-proven gaps leave this false.</summary>
        public bool IdleGapUsedQpc;

        /// <summary>The packet carried TimestampError: by OWNER rule it was
        /// NOT allowed to judge any gap (continuity placement, wall base
        /// untouched).</summary>
        public bool TimestampErrorSuppressed;

        /// <summary>Cursor END after absorbing this packet (frames).</summary>
        public long LastDevPosEnd;

        /// <summary>Content end mapped into the QPC wall domain (100ns).</summary>
        public long LastEnd100ns;
    }

    /// <summary>Packet-timeline tracker over the device render cursor
    /// (DevicePositionFrames) — pure logic, serially fed.</summary>
    public sealed class AudioPositionTracker
    {
        /// <summary>Wall evidence must exceed the previous packet's span by
        /// more than this before an idle gap is believed (50ms — the same
        /// magnitude as the proven MinLogGapSec noise floor; qpc sampling
        /// jitter is ms-scale, real idle gaps are seconds).</summary>
        public const long IdleEvidenceTolerance100ns = 500_000;

        private readonly long _sampleRate;
        private long _firstDevPos;
        private long _lastDevPosEnd;
        private long _firstQpc;
        private long _lastQpc;
        private bool _anchored;
        private bool _anchorWasFallback;
        private long _framesSinceAnchor;
        private bool _sessionPrimed;
        private long _sessionStartQpc;

        // ── Phase-A state (frozen-cursor idle reconstruction) ──────────
        /// <summary>raw→virtual cursor offset: accumulates the divergence
        /// created by idle pads (device cursor frozen while the virtual
        /// timeline advances). virtualPos = devPos + _rebaseFrames.</summary>
        private long _rebaseFrames;
        /// <summary>Raw device-domain END of the last real-cursor packet —
        /// the frozen-cursor comparator (devPos ≤ this ⇒ no forward motion).</summary>
        private long _lastRawDevPosEnd;
        /// <summary>Frames of the last accepted packet (bounds the
        /// far-backwards violation evidence in the idle branch).</summary>
        private long _lastFrames;
        /// <summary>Duration of the last accepted packet (100ns) — the idle
        /// gap is wallGap MINUS this (the packet's own content time).</summary>
        private long _lastDur100ns;
        /// <summary>Monotonic max of trusted qpc stamps — the wall-gap base.
        /// max() absorbs the documented BACKWARDS stamp noise entirely.</summary>
        private long _wallGapBaseQpc;

        /// <summary>Sample rate of the device mix format (frames → time).</summary>
        public int SampleRate { get { return (int)_sampleRate; } }

        /// <summary>
        /// Primes the tracker with the session's wall-clock origin before the
        /// first packet arrives. The first packet can therefore contribute a
        /// measured initial silent lead instead of becoming the track origin.
        /// </summary>
        public void PrimeSessionStart(long sessionStartQpc100ns)
        {
            if (sessionStartQpc100ns <= 0 || _anchored)
                return;
            _sessionPrimed = true;
            _sessionStartQpc = sessionStartQpc100ns;
        }

        /// <summary>True once the first packet anchored the timeline.</summary>
        public bool Anchored { get { return _anchored; } }

        /// <summary>Wall anchor: the anchor packet's qpcPosition (100ns).
        /// 0 while the anchor is a zero-cursor fallback awaiting re-anchor.
        /// SyncMath maps video t0 against this.</summary>
        public long FirstQpc100ns { get { return _firstQpc; } }

        /// <summary>Cursor of the anchor packet (frames). Deltas are taken
        /// from here — absolute cursor values are meaningless, only spans.</summary>
        public long FirstDevPos { get { return _firstDevPos; } }

        /// <summary>END of the last absorbed packet's cursor span (frames).</summary>
        public long LastDevPosEnd { get { return _lastDevPosEnd; } }

        /// <summary>Content end mapped into the QPC wall domain (100ns):
        /// FirstQpc + (LastDevPosEnd − FirstDevPos) × 10⁷ / rate.</summary>
        public long LastEnd100ns { get; private set; }

        /// <summary>Total packets fed.</summary>
        public long Packets { get; private set; }

        /// <summary>Total frames fed.</summary>
        public long Frames { get; private set; }

        /// <summary>Packets that reported a hole > 0.</summary>
        public long GapPackets { get; private set; }

        /// <summary>Sum of all measured holes, 100ns.</summary>
        public long TotalHole100ns { get; private set; }

        /// <summary>Packets that needed the zero-cursor fallback (Risk #2).</summary>
        public long StampFallbacks { get; private set; }

        /// <summary>Packets whose CURSOR violated monotonicity (overlap or
        /// device re-init). Timeline never rewinds.</summary>
        public long MonotonicViolations { get; private set; }

        /// <summary>Packets whose qpcPosition went backwards — wall-domain
        /// sampling noise. Evidence only (P13.4c: the timeline no longer
        /// consumes qpc deltas at all).</summary>
        public long QpcAnomalies { get; private set; }

        /// <summary>Gaps reconstructed from QPC wall evidence while the
        /// device cursor was frozen (Phase-A case 2).</summary>
        public long IdleGapPackets { get; private set; }

        /// <summary>Packets whose gap judgment was suppressed because they
        /// carried TimestampError (Phase-A: a known-error stamp judges
        /// nothing).</summary>
        public long TimestampErrorPackets { get; private set; }

        public AudioPositionTracker(int sampleRate)
        {
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate),
                    "sampleRate must be > 0 (got " + sampleRate + ")");
            _sampleRate = sampleRate;
        }

        /// <summary>frames × 10⁷ / sampleRate, floored. Exact for real
        /// packets (480 @ 48000 = 100,000). Sub-100ns remainders floor;
        /// consumer gap thresholds absorb the ≤1-tick jitter.</summary>
        public long Duration100ns(int frames)
        {
            if (frames <= 0) return 0;
            return frames * 10_000_000L / _sampleRate;
        }

        /// <summary>cursorFrames × 10⁷ / sampleRate — content span in 100ns.
        /// Positive cursor deltas can be huge (36,000,000 frames/h); the
        /// intermediate product fits Int64 up to ~9.2e18 (≈ 4.4e13 frames).</summary>
        private long FramesTo100ns(long frames)
        {
            return frames / _sampleRate * 10_000_000L
                 + frames % _sampleRate * 10_000_000L / _sampleRate;
        }

        /// <summary>Feed one packet. Pure and thread-unsafe by design —
        /// the owner of the packet stream calls this serially.
        /// flags: AUDCLNT_BUFFERFLAGS bits (WasapiPacketFlags) — optional;
        /// only TimestampError changes policy (the packet judges no gap).
        /// Legacy 3-arg callers default to flags = 0.</summary>
        public AudioGapReport Feed(int frames, long devicePositionFrames, long qpcPosition100ns, int flags = 0)
        {
            if (frames < 0)
                throw new ArgumentOutOfRangeException(nameof(frames),
                    "frames must be >= 0 (got " + frames + ") — a negative " +
                    "count would silently corrupt the evidence counters");

            var r = new AudioGapReport();
            long dur = Duration100ns(frames);
            r.BufferDur100ns = dur;
            Packets++;
            Frames += frames;

            // qpc anomaly evidence — wall-domain noise, no timeline effect.
            if (Packets > 1 && qpcPosition100ns < _lastQpc)
            {
                r.QpcAnomaly = true;
                QpcAnomalies++;
            }
            if (qpcPosition100ns > 0) _lastQpc = qpcPosition100ns;

            if (!_anchored)
            {
                // First packet: normally anchor to the packet stamp. When the
                // session origin was primed, preserve the real wall lead-in:
                // [session start ... packet start) is measured silence, not
                // the beginning of the audio track.
                long anchorQpc = qpcPosition100ns;
                long initialHole100ns = 0;
                long initialHoleFrames = 0;

                if (_sessionPrimed && qpcPosition100ns > _sessionStartQpc)
                {
                    long elapsed100ns = qpcPosition100ns - _sessionStartQpc;
                    initialHole100ns = elapsed100ns > dur ? elapsed100ns - dur : 0;
                    initialHoleFrames = (initialHole100ns * _sampleRate) / 10_000_000L;
                    if (initialHoleFrames > devicePositionFrames)
                        initialHoleFrames = devicePositionFrames;
                    anchorQpc = _sessionStartQpc;
                }

                _anchored = true;
                _firstDevPos = devicePositionFrames - initialHoleFrames;
                _firstQpc = anchorQpc;
                if (devicePositionFrames == 0)
                {
                    // Pathological: first packet has no cursor. The qpc is
                    // still the true wall position of this content — anchor
                    // on it; the cursor base is backfilled on the first
                    // real cursor (the re-anchor branch below).
                    r.StampFallbackUsed = true;
                    StampFallbacks++;
                    _anchorWasFallback = true;
                    _framesSinceAnchor = frames;
                }
                _lastDevPosEnd = devicePositionFrames + frames;
                LastEnd100ns = _firstQpc + initialHole100ns + dur;
                r.AnchoredNow = true;
                r.Hole100ns = initialHole100ns;
                if (initialHole100ns > 0)
                {
                    GapPackets++;
                    TotalHole100ns += initialHole100ns;
                }
                r.LastDevPosEnd = _lastDevPosEnd;
                r.LastEnd100ns = LastEnd100ns;
                // Phase-A bookkeeping: the anchor is a trusted packet.
                _lastRawDevPosEnd = devicePositionFrames + frames;
                _lastFrames = frames;
                _lastDur100ns = dur;
                if (qpcPosition100ns > 0) _wallGapBaseQpc = qpcPosition100ns;
                return r;
            }

            if (devicePositionFrames == 0)
            {
                // Risk #2: "no cursor". Assume continuity — this packet's
                // content starts exactly where the last one ended.
                // Phase-A: the raw cursor bookkeeping is left untouched
                // (there IS no cursor); the wall base still advances — the
                // stamp itself is real, only the position is missing.
                r.StampFallbackUsed = true;
                StampFallbacks++;
                r.Hole100ns = 0;
                _lastDevPosEnd += frames;
                LastEnd100ns += dur;
                _framesSinceAnchor += frames;
                _lastFrames = frames;
                _lastDur100ns = dur;
                if (qpcPosition100ns > _wallGapBaseQpc) _wallGapBaseQpc = qpcPosition100ns;
                r.LastDevPosEnd = _lastDevPosEnd;
                r.LastEnd100ns = LastEnd100ns;
                return r;
            }

            bool forceContinuity = false;
            if (_anchorWasFallback)
            {
                // First REAL cursor after a cursorless anchor. The anchor's
                // qpc IS the true wall position of the first content (the
                // packet existed — only its cursor was missing), so the
                // anchor stays; the cursor base is BACKFILLED from the
                // first real cursor assuming continuity:
                //   impliedFirstDevPos = devPos − Σ(cursorless frames)
                // After the backfill the normal cursor-delta logic runs —
                // an honest driver yields hole=0 here; a driver that jumped
                // yields a REAL hole that MUST be padded.
                _anchorWasFallback = false;
                long implied = devicePositionFrames - _framesSinceAnchor;
                _firstDevPos = implied >= 0 ? implied : 0;
                // Rebase the continuity-maintained end onto the real cursor
                // domain, or the delta below would fabricate an `implied`
                // sized hole. Continuity is the assumption here — the cursor
                // was absent, so no gap is measurable across the streak.
                // Phase-A: rawEnd is set to the packet START — the fall-
                // through continuity path completes it with `frames`.
                _lastDevPosEnd = devicePositionFrames;
                _lastRawDevPosEnd = devicePositionFrames;
                r.ReAnchoredNow = true;
                r.Hole100ns = 0;
                // ★ Phase-A: no gap is measurable across a re-anchor — the
                // cursorless streak was already absorbed by continuity. The
                // wall gap here (streak length) must NOT trigger an idle pad.
                forceContinuity = true;
                // fall through to the gap policy below
            }

            // ── Phase-A gap policy ──────────────────────────────────────
            bool tsError = (flags & WasapiPacketFlags.TimestampError) != 0;
            if (tsError)
            {
                // OWNER rule: a known-error stamp judges NO gap. Continuity
                // placement (the content is real, its timing is not); the
                // wall base is NOT advanced (the stamp is untrusted) and the
                // raw cursor bookkeeping is NOT rewritten (devPos untrusted).
                TimestampErrorPackets++;
                r.TimestampErrorSuppressed = true;
                r.Hole100ns = 0;
                _lastDevPosEnd += frames;
                LastEnd100ns += dur;
                _lastFrames = frames;
                _lastDur100ns = dur;
                r.LastDevPosEnd = _lastDevPosEnd;
                r.LastEnd100ns = LastEnd100ns;
                return r;
            }

            long virtualPos = devicePositionFrames + _rebaseFrames;
            long contentHoleFrames = virtualPos - _lastDevPosEnd;

            if (contentHoleFrames > 0)
            {
                // Case 1 — cursor-PROVEN gap (content time, master evidence).
                // P13.4c behavior unchanged: padded, qpc never consulted.
                r.Hole100ns = FramesTo100ns(contentHoleFrames);
                GapPackets++;
                TotalHole100ns += r.Hole100ns;
                _lastDevPosEnd = virtualPos + frames;
                LastEnd100ns = _firstQpc + FramesTo100ns(_lastDevPosEnd - _firstDevPos);
                _lastRawDevPosEnd = devicePositionFrames + frames;
            }
            else
            {
                // The cursor gave no forward progress. Consult the WALL —
                // but only against TRUSTED stamps (monotonic max base) and
                // only when the elapsed time exceeds the previous packet's
                // own span plus the jitter tolerance.
                long wallGap = -1L;
                if (_wallGapBaseQpc > 0 && qpcPosition100ns > _wallGapBaseQpc)
                    wallGap = qpcPosition100ns - _wallGapBaseQpc;

                if (!forceContinuity && wallGap - _lastDur100ns > IdleEvidenceTolerance100ns)
                {
                    // Case 2 — IDLE gap: the endpoint froze (P13.4b) while
                    // wall time passed. Reconstruct (wallGap − prevDur) of
                    // silence at THIS position, advance the virtual timeline,
                    // and rebase the raw cursor domain onto it.
                    long idle100ns = wallGap - _lastDur100ns;
                    r.Hole100ns = idle100ns;
                    r.IdleGapUsedQpc = true;
                    IdleGapPackets++;
                    TotalHole100ns += idle100ns;
                    long idleFrames = idle100ns * _sampleRate / 10_000_000L;
                    _lastDevPosEnd += idleFrames + frames;
                    LastEnd100ns = _firstQpc + FramesTo100ns(_lastDevPosEnd - _firstDevPos);
                    _rebaseFrames = _lastDevPosEnd - (devicePositionFrames + frames);
                    // A far-backwards cursor (beyond one packet) is still
                    // anomaly evidence — but the idle reconstruction stands
                    // (a frozen/re-inited cursor does not un-spend the time).
                    if (devicePositionFrames < _lastRawDevPosEnd - _lastFrames)
                    {
                        r.MonotonicViolation = true;
                        MonotonicViolations++;
                    }
                    _lastRawDevPosEnd = devicePositionFrames + frames;
                }
                else if (devicePositionFrames < _lastRawDevPosEnd)
                {
                    // Case 3a — backwards/overlapping cursor (device re-init):
                    // report it, never rewind — max policy on the virtual end.
                    r.MonotonicViolation = true;
                    MonotonicViolations++;
                    r.Hole100ns = 0;
                    long newEnd = virtualPos + frames;
                    if (newEnd > _lastDevPosEnd) _lastDevPosEnd = newEnd;
                    LastEnd100ns = _firstQpc + FramesTo100ns(_lastDevPosEnd - _firstDevPos);
                    _lastRawDevPosEnd = devicePositionFrames + frames;
                }
                else
                {
                    // Case 3b — continuity (no trustworthy elapsed evidence,
                    // or the wall accounted for exactly the packet span).
                    r.Hole100ns = 0;
                    _lastDevPosEnd = virtualPos + frames;
                    LastEnd100ns = _firstQpc + FramesTo100ns(_lastDevPosEnd - _firstDevPos);
                    _lastRawDevPosEnd = devicePositionFrames + frames;
                }
            }

            _lastFrames = frames;
            _lastDur100ns = dur;
            if (qpcPosition100ns > _wallGapBaseQpc) _wallGapBaseQpc = qpcPosition100ns;
            r.LastDevPosEnd = _lastDevPosEnd;
            r.LastEnd100ns = LastEnd100ns;
            return r;
        }

        /// <summary>Forget the timeline (device switch / hot-unplug —
        /// doc §3.2: "log it, don't bridge it"). Counters stay for evidence.
        /// Phase-A state is cleared with it — a new timeline starts clean.</summary>
        public void Reset()
        {
            _anchored = false;
            _firstDevPos = 0;
            _lastDevPosEnd = 0;
            _firstQpc = 0;
            _anchorWasFallback = false;
            _framesSinceAnchor = 0;
            _rebaseFrames = 0;
            _lastRawDevPosEnd = 0;
            _lastFrames = 0;
            _lastDur100ns = 0;
            _wallGapBaseQpc = 0;
        }
    }
}
