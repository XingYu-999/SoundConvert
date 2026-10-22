#pragma once
#include <stdbool.h>

// Capture owns all of its existing early-return paths. The optional observer
// runs only after that service has returned, using fresh Core-1 state. Constant
// callbacks inline to ordinary calls on device; there is no thread or loop.
static inline void uab_clock_capture_service(void(*capture)(void),const bool *enabled,const bool *pending,void(*clock)(void)){
 capture();
 if(*enabled&&*pending)clock();
}
