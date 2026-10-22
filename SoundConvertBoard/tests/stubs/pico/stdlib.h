#include <stdbool.h>
#include <stdint.h>
uint32_t time_us_32(void);
uint64_t time_us_64(void);
static inline uint32_t save_and_disable_interrupts(void) { return 0; }
static inline void restore_interrupts(uint32_t irq) { (void)irq; }
