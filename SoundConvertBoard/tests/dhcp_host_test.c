#include <assert.h>
#include <stdio.h>
#include "../uab_dhcp.c"
static uint64_t clock_us;static uint8_t rx_packet[1024],tx_packet[1024],dest[4];static int rx_len,tx_len;static uint8_t socket_state;static int assigns,updates,checks;
#define CHECK(x) do{checks++;assert(x);}while(0)
uint64_t time_us_64(void){return clock_us;}uint32_t get_rand_32(void){static uint32_t r=100;return ++r;}
void getSHAR(uint8_t*p){uint8_t m[]={2,3,4,5,6,7};memcpy(p,m,6);}uint8_t getSn_SR(uint8_t s){return socket_state;}
int8_t socket(uint8_t s,uint8_t p,uint16_t port,uint8_t f){socket_state=SOCK_UDP;return s;}int8_t close(uint8_t s){socket_state=0;return 1;}
uint8_t getSn_IR(uint8_t s){return Sn_IR_SENDOK;}void setSn_IR(uint8_t s,uint8_t f){}uint8_t getSn_CR(uint8_t s){return 0;}
uint16_t getSn_TX_RD(uint8_t s){return 0;}uint16_t getSn_TX_WR(uint8_t s){return 0;}uint16_t getSn_TX_FSR(uint8_t s){return 1024;}
void setSn_DIPR(uint8_t s,uint8_t*p){memcpy(dest,p,4);}void setSn_DPORT(uint8_t s,uint16_t p){assert(p==67);}
void wiz_send_data(uint8_t s,uint8_t*p,uint16_t n){memcpy(tx_packet,p,n);tx_len=n;}void setSn_CR(uint8_t s,uint8_t c){}
uint16_t getSn_RX_RSR(uint8_t s){return rx_len;}int8_t getsockopt(uint8_t s,int opt,void*p){*(uint8_t*)p=0;return 1;}
int32_t recvfrom(uint8_t s,uint8_t*p,uint16_t cap,uint8_t*from,uint16_t*port){int n=rx_len;memcpy(p,rx_packet,n);rx_len=0;*port=67;memcpy(from,"\xc0\xa8\x08\x01",4);return n;}
static void assigned_test(void){assigns++;}static void updated_test(void){updates++;}
static void check_hostname(void){unsigned found=0;for(unsigned p=240;p<(unsigned)tx_len;){uint8_t opt=tx_packet[p++];if(opt==255)break;if(!opt)continue;CHECK(p<(unsigned)tx_len);unsigned len=tx_packet[p++];CHECK(p+len<=(unsigned)tx_len);if(opt==12){CHECK(len==sizeof(UAB_NETWORK_HOSTNAME)-1);CHECK(!memcmp(tx_packet+p,UAB_NETWORK_HOSTNAME,len));found++;}p+=len;}CHECK(found==1);}
static void reply(uint8_t type,uint32_t seconds){memset(rx_packet,0,sizeof(rx_packet));rx_packet[0]=2;rx_packet[1]=1;rx_packet[2]=6;wr32(rx_packet+4,xid);memcpy(rx_packet+28,macaddr,6);wr32(rx_packet+236,0x63825363);memcpy(rx_packet+16,"\xc0\xa8\x08\x49",4);unsigned p=240;rx_packet[p++]=53;rx_packet[p++]=1;rx_packet[p++]=type;rx_packet[p++]=54;rx_packet[p++]=4;memcpy(rx_packet+p,"\xc0\xa8\x08\x01",4);p+=4;rx_packet[p++]=1;rx_packet[p++]=4;memcpy(rx_packet+p,"\xff\xff\xff\x00",4);p+=4;rx_packet[p++]=51;rx_packet[p++]=4;wr32(rx_packet+p,seconds);p+=4;rx_packet[p++]=255;rx_len=p;}
int main(void){uint8_t storage[1024];reg_dhcp_cbfunc(assigned_test,updated_test,NULL);DHCP_init(3,storage);CHECK(DHCP_run()==DHCP_RUNNING);CHECK(tx_len>=300&&tx_packet[242]==1&&dest[0]==255);CHECK(assigns==0);check_hostname();reply(2,80);DHCP_run();CHECK(state==REQUESTING&&tx_packet[242]==3);check_hostname();reply(5,80);CHECK(DHCP_run()==DHCP_IP_LEASED);CHECK(assigns==1&&lease==80&&state==BOUND);CHECK(t1==40&&t2==70);
 clock_us=41000000;DHCP_run();CHECK(state==RENEWING&&dest[0]==192);check_hostname();reply(5,100);DHCP_run();CHECK(updates==1&&lease==100&&state==BOUND);CHECK(acquired==41000);
 clock_us=92000000;DHCP_run();CHECK(state==RENEWING);clock_us=129000000;DHCP_run();CHECK(state==REBINDING&&dest[0]==255);check_hostname();reply(6,0);CHECK(DHCP_run()==DHCP_FAILED&&state==OFF);
 DHCP_init(3,storage);DHCP_run();reply(2,10);DHCP_run();reply(5,10);DHCP_run();clock_us+=11000000;CHECK(DHCP_run()==DHCP_FAILED&&state==OFF);
 DHCP_init(3,storage);DHCP_run();reply(2,80);rx_packet[245]=255;CHECK(DHCP_run()==DHCP_RUNNING&&state==DISCOVERING);reply(2,80);DHCP_run();reply(5,0xffffffffu);DHCP_run();clock_us+=0xffffffffULL*1000000ULL;CHECK(DHCP_run()==DHCP_IP_LEASED&&state==BOUND);
 DHCP_stop();CHECK(DHCP_run()==DHCP_STOPPED);printf("PASS %d DHCP state/length/renewal/NAK/expiry checks\n",checks);return 0;}
