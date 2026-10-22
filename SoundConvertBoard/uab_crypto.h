#pragma once
#include <stdint.h>
#include <stddef.h>
void uab_sha256(const void *data,size_t n,uint8_t out[32]);
int uab_equal(const uint8_t *a,const uint8_t *b,size_t n);
