#include <assert.h>
#include <stdio.h>
#include "../uab_udp_ports.h"
static unsigned calls;
static bool free_port(uint16_t port,void*context){calls++;return false;}
static bool occupied(uint16_t port,void*context){calls++;return port==*(uint16_t*)context;}
static bool all_busy(uint16_t port,void*context){calls++;return true;}
int main(void){
 unsigned checks=0;
 #define CHECK(x) do{checks++;assert(x);}while(0)
 CHECK(uab_udp_port_select(1023,0,free_port,0)==0);
 CHECK(uab_udp_port_select(-1,0,free_port,0)==0);
 CHECK(uab_udp_port_select(65536,0,free_port,0)==0);
 CHECK(uab_udp_port_select(49160,0,free_port,0)==0);
 CHECK(uab_udp_port_select(49161,0,free_port,0)==0);
 CHECK(uab_udp_port_select(49162,0,free_port,0)==49162);
 CHECK(uab_udp_port_select(65535,0,free_port,0)==65535);
 uint16_t port=49162;CHECK(uab_udp_port_select(49162,0,occupied,&port)==0);
 CHECK(uab_udp_port_select(0,10,occupied,&port)==49163);
 CHECK(uab_udp_port_select(0,8,free_port,0)==49162);
 CHECK(uab_udp_port_select(0,16383,free_port,0)==65535);
 port=65535;CHECK(uab_udp_port_select(0,16383,occupied,&port)==49152);
 calls=0;CHECK(uab_udp_port_select(0,0,all_busy,0)==0);CHECK(calls<=32);
 printf("PASS: %u UDP port allocation checks (host model; no W5500 or board acceptance)\n",checks);return 0;
}
