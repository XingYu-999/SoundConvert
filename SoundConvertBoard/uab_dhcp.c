/* Bounded DHCPv4 client using WIZnet hardware UDP. No blocking sendto path.
 * Supports initial DORA, T1 unicast renewal, T2 broadcast rebinding, NAK,
 * expiry and infinite leases. DHCP_TIME is a monotonic SDK clock. */
#include <stdint.h>
#include "dhcp.h"
#include "socket.h"
#include "pico/stdlib.h"
#include "pico/rand.h"
#include <string.h>
#include "uab_network_name.h"
static uint8_t sn,*buf,macaddr[6],ipaddr[4],mask[4],gateway[4],dnsaddr[4],server[4];
static uint32_t xid,lease,t1,t2;static uint64_t acquired,due;static unsigned attempts;
static enum {OFF,DISCOVERING,REQUESTING,BOUND,RENEWING,REBINDING} state;
static void(*assigned)(void),(*updated)(void),(*conflicted)(void);
static uint16_t rd16(const uint8_t*p){return ((uint16_t)p[0]<<8)|p[1];}
static uint32_t rd32(const uint8_t*p){return ((uint32_t)rd16(p)<<16)|rd16(p+2);}
static void wr32(uint8_t*p,uint32_t x){p[0]=x>>24;p[1]=x>>16;p[2]=x>>8;p[3]=x;}
static uint64_t milliseconds(void){return time_us_64()/1000;}
void reg_dhcp_cbfunc(void(*a)(void),void(*u)(void),void(*c)(void)){assigned=a;updated=u;conflicted=c;}
void DHCP_init(uint8_t socket_number,uint8_t*buffer){sn=socket_number;buf=buffer;getSHAR(macaddr);memset(ipaddr,0,4);memset(server,0,4);xid=get_rand_32();state=DISCOVERING;due=0;attempts=0;lease=0;}
void DHCP_stop(void){if(state!=OFF)close(sn);state=OFF;}
void DHCP_time_handler(void){}
void getIPfromDHCP(uint8_t*p){memcpy(p,ipaddr,4);}void getSNfromDHCP(uint8_t*p){memcpy(p,mask,4);}void getGWfromDHCP(uint8_t*p){memcpy(p,gateway,4);}void getDNSfromDHCP(uint8_t*p){memcpy(p,dnsaddr,4);}uint32_t getDHCPLeasetime(void){return lease;}
static bool submit(void){
 uint8_t ir=getSn_IR(sn);if(ir&Sn_IR_TIMEOUT){close(sn);return false;}if(ir&Sn_IR_SENDOK)setSn_IR(sn,Sn_IR_SENDOK);
 if(getSn_CR(sn)||getSn_TX_RD(sn)!=getSn_TX_WR(sn)||getSn_TX_FSR(sn)<350)return false;
 memset(buf,0,350);buf[0]=1;buf[1]=1;buf[2]=6;wr32(buf+4,xid);if(state!=RENEWING)buf[10]=0x80;
 if(state==RENEWING||state==REBINDING)memcpy(buf+12,ipaddr,4);memcpy(buf+28,macaddr,6);wr32(buf+236,0x63825363);
 unsigned p=240;buf[p++]=53;buf[p++]=1;buf[p++]=state==DISCOVERING?1:3;buf[p++]=61;buf[p++]=7;buf[p++]=1;memcpy(buf+p,macaddr,6);p+=6;
 if(state==REQUESTING){buf[p++]=50;buf[p++]=4;memcpy(buf+p,ipaddr,4);p+=4;buf[p++]=54;buf[p++]=4;memcpy(buf+p,server,4);p+=4;}
 buf[p++]=12;buf[p++]=sizeof(UAB_NETWORK_HOSTNAME)-1;memcpy(buf+p,UAB_NETWORK_HOSTNAME,sizeof(UAB_NETWORK_HOSTNAME)-1);p+=sizeof(UAB_NETWORK_HOSTNAME)-1;
 buf[p++]=55;buf[p++]=6;buf[p++]=1;buf[p++]=3;buf[p++]=6;buf[p++]=51;buf[p++]=58;buf[p++]=59;
 buf[p++]=57;buf[p++]=2;buf[p++]=4;buf[p++]=0;buf[p++]=255;if(p<300)p=300;
 uint8_t broadcast[4]={255,255,255,255};setSn_DIPR(sn,state==RENEWING?server:broadcast);setSn_DPORT(sn,67);wiz_send_data(sn,buf,p);setSn_CR(sn,Sn_CR_SEND);return true;
}
static bool usable(const uint8_t*a){return a[0]!=0&&a[0]!=127&&a[0]<224&&!(a[0]==169&&a[1]==254);}
uint8_t DHCP_run(void){
 if(state==OFF)return DHCP_STOPPED;uint64_t now=milliseconds();
 if(getSn_SR(sn)!=SOCK_UDP)socket(sn,Sn_MR_UDP,68,SF_IO_NONBLOCK);
 if(state>=BOUND&&lease!=0xffffffffu){uint64_t age=(now-acquired)/1000;if(age>=lease){state=OFF;close(sn);return DHCP_FAILED;}if(age>=t2&&state!=REBINDING){state=REBINDING;due=0;attempts=0;xid=get_rand_32();}else if(age>=t1&&state==BOUND){state=RENEWING;due=0;attempts=0;xid=get_rand_32();}}
 if(getSn_RX_RSR(sn)){
  uint8_t from[4];uint16_t port;int n=recvfrom(sn,buf,1024,from,&port);uint8_t pack=0;getsockopt(sn,SO_PACKINFO,&pack);if(pack&PACK_REMAINED){close(sn);return DHCP_RUNNING;}
  if(n>=240&&port==67&&buf[0]==2&&buf[1]==1&&buf[2]==6&&rd32(buf+4)==xid&&!memcmp(buf+28,macaddr,6)&&rd32(buf+236)==0x63825363){
   uint8_t type=0,server_new[4]={0},mask_new[4]={0},gw_new[4]={0},dns_new[4]={0};uint32_t lease_new=0,t1_new=0,t2_new=0;bool bad=false;uint8_t seen[32]={0};
   for(unsigned p=240;p<(unsigned)n;){uint8_t opt=buf[p++];if(opt==255)break;if(!opt)continue;if(p>=(unsigned)n){bad=true;break;}unsigned len=buf[p++];if(p+len>(unsigned)n||(seen[opt>>3]&(1u<<(opt&7)))){bad=true;break;}seen[opt>>3]|=1u<<(opt&7);uint8_t*v=buf+p;p+=len;
    if(opt==53){if(len!=1){bad=true;break;}type=v[0];}
    else if(opt==54||opt==1||opt==51||opt==58||opt==59){if(len!=4){bad=true;break;}if(opt==54)memcpy(server_new,v,4);else if(opt==1)memcpy(mask_new,v,4);else if(opt==51)lease_new=rd32(v);else if(opt==58)t1_new=rd32(v);else t2_new=rd32(v);}
    else if(opt==3||opt==6){if(len<4||len%4){bad=true;break;}memcpy(opt==3?gw_new:dns_new,v,4);}
   }
   if(!bad&&usable(server_new)){
    if(type==2&&state==DISCOVERING&&usable(buf+16)){memcpy(ipaddr,buf+16,4);memcpy(server,server_new,4);state=REQUESTING;attempts=0;due=0;}
    else if(type==6&&(state==REQUESTING||state==RENEWING||state==REBINDING)&&(state==REBINDING||!memcmp(server,server_new,4))){state=OFF;close(sn);return DHCP_FAILED;}
    else if(type==5&&(state==REQUESTING||state==RENEWING||state==REBINDING)&&(state==REBINDING||!memcmp(server,server_new,4))&&lease_new){
     bool renewing=state!=REQUESTING;uint8_t next[4];memcpy(next,usable(buf+16)?buf+16:ipaddr,4);uint32_t m=rd32(mask_new),inv=~m;uint32_t address=rd32(next);if(!usable(next)||!m||(inv&(inv+1))||!inv||(address&inv)==0||(address&inv)==inv)return DHCP_RUNNING;
     memcpy(ipaddr,next,4);memcpy(mask,mask_new,4);memcpy(gateway,gw_new,4);memcpy(dnsaddr,dns_new,4);memcpy(server,server_new,4);lease=lease_new;t1=t1_new?t1_new:lease/2;t2=t2_new?t2_new:(uint32_t)((uint64_t)lease*7/8);if(t1>=t2||t2>=lease){t1=lease/2;t2=(uint32_t)((uint64_t)lease*7/8);}acquired=now;state=BOUND;attempts=0;if(renewing){if(updated)updated();}else if(assigned)assigned();return DHCP_IP_LEASED;
    }
   }
  }
 }
 if(state!=BOUND&&now>=due){if(attempts>=6&&state<=REQUESTING){state=OFF;close(sn);return DHCP_FAILED;}if(submit()){attempts++;due=now+(1000u<<(attempts<5?attempts-1:4))+(get_rand_32()%500);}}
 return state>=BOUND?DHCP_IP_LEASED:DHCP_RUNNING;
}
