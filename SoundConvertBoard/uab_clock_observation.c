#include "uab_clock_observation.h"
#include <string.h>

static inline uint16_t r16(const uint8_t *p) { return (uint16_t)((p[0] << 8) | p[1]); }
static inline uint32_t r32(const uint8_t *p) { return ((uint32_t)r16(p) << 16) | r16(p + 2); }
static inline uint64_t r64(const uint8_t *p) { return ((uint64_t)r32(p) << 32) | r32(p + 4); }
static inline void w16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)(v >> 8); p[1] = (uint8_t)v; }
static inline void w32(uint8_t *p, uint32_t v) { w16(p, (uint16_t)(v >> 16)); w16(p + 2, (uint16_t)v); }
static inline void w64(uint8_t *p, uint64_t v) { w32(p, (uint32_t)(v >> 32)); w32(p + 4, (uint32_t)v); }
static inline bool same(const uab_clock_identity_t *a, const uab_clock_identity_t *b) {
    return a->boot_id == b->boot_id && a->token == b->token && a->stream_id == b->stream_id && a->epoch == b->epoch;
}
void uab_clock_observation_reset_stats(uab_clock_observation_t *s) {
    memset(&s->requests, 0, sizeof(*s) - offsetof(uab_clock_observation_t, requests));
}
void UAB_CLOCK_RAM(uab_clock_observation_disable)(uab_clock_observation_t *s) {
    if (s->enabled) ++s->revoked;
    s->enabled = s->pending = false;
    s->preflight_used = false;
    memset(&s->identity, 0, sizeof(s->identity));
    s->last_request_id = s->next_allowed_us = 0;
}
bool uab_clock_observation_enable(uab_clock_observation_t *s, const uab_clock_identity_t *identity) {
    if (!identity->boot_id || !identity->token || !identity->stream_id || !identity->epoch) return false;
    if (s->budget_latched) {
        if (same(&s->budget_identity, identity)) return false;
        // The network owner already validated that this is the actual new
        // stream identity. Toggling enable or resetting counters is insufficient.
        s->budget_latched = false; s->budget_latched_us = 0; s->budget_latched_phase = UAB_CLOCK_BUDGET_UNKNOWN;
        memset(&s->budget_identity, 0, sizeof(s->budget_identity));
    }
    // A duplicate successful enable does not reset replay/rate limits.
    if (s->enabled && same(&s->identity, identity)) return true;
    uab_clock_observation_disable(s);
    s->identity = *identity;
    s->enabled = true;
    return true;
}
bool UAB_CLOCK_RAM(uab_clock_observation_record_work_phase)(uab_clock_observation_t *s, uint32_t elapsed, uab_clock_budget_phase_t phase) {
    if (elapsed > s->processing_max_us) s->processing_max_us = elapsed;
    if (elapsed <= UAB_CLOCK_SERVICE_BUDGET_US || !s->enabled) return !s->budget_latched;
    s->budget_identity = s->identity;
    s->budget_latched = true; s->budget_latched_us = elapsed; s->budget_latched_phase = phase;
    ++s->service_budget_breaches;
    uab_clock_observation_disable(s);
    return false;
}
bool UAB_CLOCK_RAM(uab_clock_observation_record_work)(uab_clock_observation_t *s, uint32_t elapsed) {
    return uab_clock_observation_record_work_phase(s, elapsed, UAB_CLOCK_BUDGET_UNKNOWN);
}
bool UAB_CLOCK_RAM(uab_clock_observation_matches)(const uab_clock_observation_t *s, const uab_clock_identity_t *identity) {
    return s->enabled && same(&s->identity, identity);
}
bool UAB_CLOCK_RAM(uab_clock_observation_expire)(uab_clock_observation_t *s, uint64_t now) {
    if (!s->pending || (now >= s->received_us && now - s->received_us <= UAB_CLOCK_PENDING_US)) return false;
    s->pending = false; ++s->expired; return true;
}
bool UAB_CLOCK_RAM(uab_clock_observation_receive)(uab_clock_observation_t *s, const uint8_t *p, size_t bytes, uint64_t received) {
    if (!s->enabled) { ++s->identity_rejected; return false; }
    if (bytes != UAB_CLOCK_PACKET_BYTES || p[0] != 'U' || p[1] != 'A' || p[2] != 'B' || p[3] != 'T' || p[4] != 1 || p[5] != 1 || r16(p + 6) != UAB_CLOCK_PACKET_BYTES) {
        ++s->invalid; return false;
    }
    uint32_t reserved = 0;
    for (size_t i = 48; i < UAB_CLOCK_PACKET_BYTES; i += 4) reserved |= r32(p + i);
    if (reserved) { ++s->invalid; return false; }
    uab_clock_identity_t identity = { r64(p + 8), r64(p + 24), r32(p + 16), r32(p + 20) };
    if (!same(&identity, &s->identity)) { ++s->identity_rejected; return false; }
    uint64_t request = r64(p + 32);
    if (!request || request <= s->last_request_id) { ++s->replay_rejected; return false; }
    s->last_request_id = request;
    uab_clock_observation_expire(s, received);
    if (received < s->next_allowed_us) { ++s->limited; return false; }
    if (s->pending) { ++s->busy; return false; }
    s->next_allowed_us = received > UINT64_MAX - UAB_CLOCK_REQUEST_INTERVAL_US ? UINT64_MAX : received + UAB_CLOCK_REQUEST_INTERVAL_US;
    s->pending = true; s->request_id = request; s->p1 = r64(p + 40); s->received_us = received; ++s->requests;
    return true;
}
bool UAB_CLOCK_RAM(uab_clock_observation_receive_from)(uab_clock_observation_t *s, const uint8_t *p, size_t bytes, uint64_t received, bool current_peer, bool live_owner) {
    if (!current_peer || !live_owner) { ++s->identity_rejected; return false; }
    return uab_clock_observation_receive(s, p, bytes, received);
}
bool UAB_CLOCK_RAM(uab_clock_observation_audio_budget)(uab_clock_observation_t *s, const uab_clock_anchor_t *a, uint64_t now, bool audio_pending, bool fec_pending, unsigned queued, bool streaming) {
    if (!s->enabled || !s->pending || uab_clock_observation_expire(s, now)) return false;
    if (audio_pending || fec_pending || queued ||
        (streaming && (!a || !a->valid || a->generation != s->identity.epoch || now < a->extract_us || now - a->extract_us > 500))) {
        ++s->skipped; return false;
    }
    return true;
}
bool UAB_CLOCK_RAM(uab_clock_observation_preflight_slot)(uab_clock_observation_t *s, const uab_clock_anchor_t *a, uint64_t now, bool streaming, uint32_t loop_id) {
    if (!s->enabled || !s->pending || uab_clock_observation_expire(s, now)) return false;
    if (streaming) {
        if (!a || !a->valid || a->generation != s->identity.epoch || now < a->extract_us || now - a->extract_us > 500) return false;
        if (now - a->extract_us < UAB_CLOCK_PREFLIGHT_MIN_AGE_US) { ++s->preflight_phase_waits; return false; }
        if (s->preflight_used && s->preflight_streaming && s->preflight_generation == a->generation &&
            s->preflight_sample == a->sample_index && s->preflight_extract_us == a->extract_us) { ++s->preflight_deferred; return false; }
        s->preflight_generation = a->generation; s->preflight_sample = a->sample_index; s->preflight_extract_us = a->extract_us;
    } else if (s->preflight_used && !s->preflight_streaming && s->preflight_loop == loop_id) { ++s->preflight_deferred; return false; }
    s->preflight_used = true; s->preflight_streaming = streaming; s->preflight_loop = loop_id;
    ++s->preflight_attempts; return true;
}
bool UAB_CLOCK_RAM(uab_clock_observation_reply)(uab_clock_observation_t *s, const uab_clock_anchor_t *a, uint64_t submitted, uint8_t p[UAB_CLOCK_PACKET_BYTES]) {
    uab_clock_observation_expire(s, submitted);
    if (!s->enabled || !s->pending) return false;
    // This path runs in RAM. Avoid a compiler/libc Flash memset on sparse
    // replies; the fixed 104-byte clear remains inside the service budget.
    volatile uint8_t *zero = p;
    for (unsigned i = 0; i < UAB_CLOCK_PACKET_BYTES; ++i) zero[i] = 0;
    p[0] = 'U'; p[1] = 'A'; p[2] = 'B'; p[3] = 'T'; p[4] = 1; p[5] = 2; w16(p + 6, UAB_CLOCK_PACKET_BYTES);
    w64(p + 8, s->identity.boot_id); w32(p + 16, s->identity.stream_id); w32(p + 20, s->identity.epoch);
    w64(p + 24, s->identity.token); w64(p + 32, s->request_id); w64(p + 40, s->p1);
    w64(p + 48, s->received_us); w64(p + 56, submitted);
    if (a && a->valid && a->generation == s->identity.epoch && a->frames && a->sample_rate &&
        a->extract_us <= submitted && submitted - a->extract_us <= UAB_CLOCK_ANCHOR_MAX_AGE_US) {
        w64(p + 64, a->sample_index); w64(p + 72, a->extract_us); w32(p + 80, a->generation);
        w32(p + 84, a->sample_rate); w16(p + 88, a->frames); w16(p + 90, 1);
    }
    // Pending is cleared by the network owner only after actual submission.
    // A busy retry must call this function again with a fresh B3.
    return true;
}
