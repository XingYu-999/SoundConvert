#include "uab_crypto.h"
#include "mbedtls/sha256.h"
/* Hashing is retained for stable device identity and duplicate-request detection. */
void uab_sha256(const void *d,size_t n,uint8_t out[32]) { mbedtls_sha256(d,n,out,0); }
int uab_equal(const uint8_t *a,const uint8_t *b,size_t n){uint8_t diff=0;while(n--)diff|=*a++^*b++;return diff==0;}
