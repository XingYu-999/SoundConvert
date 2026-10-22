#ifndef UAB_NETWORK_NAME_H
#define UAB_NETWORK_NAME_H

#include <stddef.h>
#include <stdint.h>
#include <string.h>

#define UAB_NETWORK_HOSTNAME "W5500-EVB-Pico2"
#define UAB_NBNS_PORT 137
_Static_assert(sizeof(UAB_NETWORK_HOSTNAME) - 1 <= 15, "NetBIOS name exceeds 15 bytes");

static inline uint16_t uab_name_r16(const uint8_t *p) { return (uint16_t)((p[0] << 8) | p[1]); }
static inline void uab_name_w16(uint8_t *p, uint16_t v) { p[0] = (uint8_t)(v >> 8); p[1] = (uint8_t)v; }
static inline uint8_t uab_name_upper(uint8_t c) { return c >= 'a' && c <= 'z' ? (uint8_t)(c - 'a' + 'A') : c; }

/* RFC 1002: one unscoped, first-level encoded question. No compressed input,
 * registration, session service, or names belonging to other devices. */
static inline size_t uab_nbns_reply(const uint8_t *query, size_t length,
                                  const uint8_t ip[4], const uint8_t mac[6],
                                  uint8_t *reply, size_t capacity) {
    if (length != 50 || query[12] != 32 || query[45] != 0 ||
        (uab_name_r16(query + 2) & ~0x0110u) != 0 ||
        uab_name_r16(query + 4) != 1 || uab_name_r16(query + 6) != 0 ||
        uab_name_r16(query + 8) != 0 || uab_name_r16(query + 10) != 0 ||
        uab_name_r16(query + 48) != 1) return 0;
    uint16_t type = uab_name_r16(query + 46);
    if (type != 0x20 && type != 0x21) return 0;
    uint8_t name[16];
    for (unsigned i = 0; i < 16; ++i) {
        uint8_t hi = query[13 + 2 * i], lo = query[14 + 2 * i];
        if (hi < 'A' || hi > 'P' || lo < 'A' || lo > 'P') return 0;
        name[i] = (uint8_t)(((hi - 'A') << 4) | (lo - 'A'));
    }
    int own = name[15] == 0, wildcard = type == 0x21 && name[0] == '*';
    for (unsigned i = 0; i < 15; ++i) {
        uint8_t expected = i < sizeof(UAB_NETWORK_HOSTNAME) - 1 ? (uint8_t)UAB_NETWORK_HOSTNAME[i] : ' ';
        if (uab_name_upper(name[i]) != uab_name_upper(expected)) own = 0;
        if (i && name[i] != 0) wildcard = 0;
    }
    if (name[15] != 0) wildcard = 0;
    if (!own && !wildcard) return 0;
    size_t data_length = type == 0x21 ? 65 : 6;
    size_t total = 56 + data_length;
    if (capacity < total) return 0;
    memset(reply, 0, total);
    memcpy(reply, query, 2);
    uab_name_w16(reply + 2, (uint16_t)(0x8400 | (type == 0x20 ? (uab_name_r16(query + 2) & 0x0100) : 0)));
    uab_name_w16(reply + 6, 1);
    memcpy(reply + 12, query + 12, 34);
    uab_name_w16(reply + 46, type);
    uab_name_w16(reply + 48, 1);
    if (type == 0x20) reply[53] = 60;
    uab_name_w16(reply + 54, (uint16_t)data_length);
    if (type == 0x21) {
        reply[56] = 1;
        memset(reply + 57, ' ', 15);
        memcpy(reply + 57, UAB_NETWORK_HOSTNAME, sizeof(UAB_NETWORK_HOSTNAME) - 1);
        uab_name_w16(reply + 73, 0x0400); /* unique, active workstation name */
        memcpy(reply + 75, mac, 6); /* remaining 40 statistics bytes stay zero */
    } else memcpy(reply + 58, ip, 4);
    return total;
}
#endif
