#pragma once
#include <stdbool.h>
#include <stddef.h>
#include <stdint.h>

// A 1-2 Hz observer must meet the same budget on its first cold-XIP request.
// Bound RAM placement to these small clock routines; no heap or cache warming.
#if defined(PICO_ON_DEVICE) && PICO_ON_DEVICE
#include "pico.h"
#define UAB_CLOCK_RAM(name) __no_inline_not_in_flash_func(name)
#else
#define UAB_CLOCK_RAM(name) name
#endif

#define UAB_CLOCK_PACKET_BYTES 104u
#define UAB_CLOCK_REQUEST_INTERVAL_US 250000u
#define UAB_CLOCK_PENDING_US 2000u
#define UAB_CLOCK_ANCHOR_MAX_AGE_US 4000u
#define UAB_CLOCK_SERVICE_BUDGET_US 100u
// Conservative scheduling, within the unchanged first-500-us eligibility:
// do not spend an anchor's single SPI attempt immediately after PCM SEND.
#define UAB_CLOCK_PREFLIGHT_MIN_AGE_US 200u
typedef struct { uint64_t boot_id, token; uint32_t stream_id, epoch; } uab_clock_identity_t;
typedef enum { UAB_CLOCK_BUDGET_UNKNOWN, UAB_CLOCK_BUDGET_RX, UAB_CLOCK_BUDGET_TX_PREPARE, UAB_CLOCK_BUDGET_TX_PRECOMMIT, UAB_CLOCK_BUDGET_TX_COMPLETE } uab_clock_budget_phase_t;
typedef struct {
    uint64_t sample_index, extract_us;
    uint32_t generation, sample_rate;
    uint16_t frames;
    bool valid;
} uab_clock_anchor_t;
typedef struct {
    bool enabled, pending, budget_latched;
    uab_clock_identity_t identity, budget_identity;
    // Safety latch and offending duration survive RESET_STATS and disable.
    uint32_t budget_latched_us;
    uab_clock_budget_phase_t budget_latched_phase;
    bool preflight_used,preflight_streaming;
    uint32_t preflight_generation,preflight_loop;
    uint64_t preflight_sample,preflight_extract_us;
    uint64_t last_request_id, next_allowed_us, request_id, p1, received_us;
    uint32_t requests, invalid, identity_rejected, replay_rejected, limited, busy;
    uint32_t skipped, expired, submitted, completed, timeouts, revoked, processing_max_us, service_budget_breaches;
    uint32_t rx_header_max_us,rx_read_max_us,rx_validate_max_us,tx_prepare_max_us,tx_submit_max_us;
    uint32_t preflight_attempts,preflight_deferred,preflight_phase_waits;
    uint32_t preflight_audio_tx_waits,last_preflight_pending_kind,last_preflight_submitted_age_us;
    uint32_t rx_audio_priority_deferrals;
    uint32_t last_busy_pending_kind,last_busy_submitted_age_us;
    uint32_t last_busy_stage; // 1=completion/CR, 2=final submit preflight
    bool last_preflight_pending,last_busy_pending;
} uab_clock_observation_t;

// Core 1 owns this entire state. No ISR, heap, spin, or network calls occur here.
void uab_clock_observation_disable(uab_clock_observation_t *state);
bool uab_clock_observation_enable(uab_clock_observation_t *state, const uab_clock_identity_t *identity);
bool uab_clock_observation_matches(const uab_clock_observation_t *state, const uab_clock_identity_t *identity);
bool uab_clock_observation_receive(uab_clock_observation_t *state, const uint8_t *packet, size_t bytes, uint64_t received_us);
bool uab_clock_observation_receive_from(uab_clock_observation_t *state, const uint8_t *packet, size_t bytes, uint64_t received_us, bool current_peer, bool live_owner);
bool uab_clock_observation_expire(uab_clock_observation_t *state, uint64_t now_us);
bool uab_clock_observation_audio_budget(uab_clock_observation_t *state, const uab_clock_anchor_t *anchor, uint64_t now_us, bool audio_pending, bool fec_pending, unsigned queued_blocks, bool usb_streaming);
bool uab_clock_observation_reply(uab_clock_observation_t *state, const uab_clock_anchor_t *anchor, uint64_t submit_us, uint8_t packet[UAB_CLOCK_PACKET_BYTES]);
void uab_clock_observation_reset_stats(uab_clock_observation_t *state);
bool uab_clock_observation_record_work(uab_clock_observation_t *state, uint32_t elapsed_us);
bool uab_clock_observation_record_work_phase(uab_clock_observation_t *state, uint32_t elapsed_us, uab_clock_budget_phase_t phase);
bool uab_clock_observation_preflight_slot(uab_clock_observation_t *state, const uab_clock_anchor_t *anchor, uint64_t now_us, bool usb_streaming, uint32_t loop_id);
// Admission is intentionally about software audio work only. A hardware
// pending bit can remain true after SENDOK and must not permanently starve RX.
static inline bool uab_clock_observation_rx_admitted(bool audio_pending,bool fec_pending,unsigned queued_blocks){return !audio_pending&&!fec_pending&&!queued_blocks;}
