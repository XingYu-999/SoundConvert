#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../uab_crypto.h"
static unsigned checks;
#define CHECK(x) do{checks++;assert(x);}while(0)
static void hex(const char*s,uint8_t*out){for(unsigned i=0;s[i*2];i++){unsigned x;sscanf(s+i*2,"%2x",&x);out[i]=(uint8_t)x;}}
int main(void){
 uint8_t actual[32],expected[32];uab_sha256("abc",3,actual);hex("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",expected);CHECK(uab_equal(actual,expected,32));
 expected[31]^=1;CHECK(!uab_equal(actual,expected,32));CHECK(uab_equal(actual,expected,0));
 printf("PASS %u stable identity/request digest checks\n",checks);return 0;
}
