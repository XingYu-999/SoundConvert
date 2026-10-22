#pragma once
#include "uab_config_store.h"
// Core 0 calls this after boot configuration loading and before Core 1 launch.
void uab_network_prepare(const uab_config_t *config);
void uab_network_main(void);
