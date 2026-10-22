#include <assert.h>
#include <stdio.h>
#include "../uab_network_name.h"

static unsigned checks;
#define CHECK(x) do { ++checks; assert(x); } while (0)
static void question(uint8_t q[50], const uint8_t name[16], uint16_t type) {
    memset(q, 0, 50);q[0]=0x12;q[1]=0x34;q[5]=1;q[12]=32;
    for(unsigned i=0;i<16;i++){q[13+2*i]=(uint8_t)('A'+(name[i]>>4));q[14+2*i]=(uint8_t)('A'+(name[i]&15));}
    uab_name_w16(q+46,type);q[49]=1;
}
int main(void) {
    uint8_t q[51],out[130],wildcard[16]={'*'},own[16]=UAB_NETWORK_HOSTNAME;
    const uint8_t ip[]={192,168,8,73},mac[]={2,3,4,5,6,7};
    question(q,wildcard,0x21);memset(out,0xaa,sizeof(out));
    CHECK(uab_nbns_reply(q,50,ip,mac,out,121)==121);
    CHECK(out[0]==0x12&&out[1]==0x34&&uab_name_r16(out+2)==0x8400);
    CHECK(uab_name_r16(out+4)==0&&uab_name_r16(out+6)==1);
    CHECK(!memcmp(q+12,out+12,34)&&uab_name_r16(out+46)==0x21&&uab_name_r16(out+48)==1);
    CHECK(uab_name_r16(out+54)==65&&out[56]==1&&!memcmp(out+57,UAB_NETWORK_HOSTNAME,15));
    CHECK(out[72]==0&&uab_name_r16(out+73)==0x0400&&!memcmp(out+75,mac,6));
    for(unsigned i=81;i<121;i++)CHECK(out[i]==0);
    CHECK(out[121]==0xaa);
    for(unsigned cap=0;cap<121;cap++)CHECK(uab_nbns_reply(q,50,ip,mac,out,cap)==0);
    for(unsigned n=0;n<50;n++)CHECK(uab_nbns_reply(q,n,ip,mac,out,sizeof(out))==0);
    CHECK(uab_nbns_reply(q,51,ip,mac,out,sizeof(out))==0);
    for(unsigned i=13;i<45;i++){uint8_t saved=q[i];q[i]='Q';CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);q[i]=saved;}
    for(unsigned i=0;i<16;i++){uab_name_w16(q+2,(uint16_t)(1u<<i));CHECK((uab_nbns_reply(q,50,ip,mac,out,sizeof(out))!=0)==(i==4||i==8));}
    question(q,own,0x20);q[2]=1;
    CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==62);
    CHECK(uab_name_r16(out+2)==0x8500&&uab_name_r16(out+54)==6&&out[53]==60&&!memcmp(out+58,ip,4));
    for(unsigned i=0;i<15;i++)own[i]=uab_name_upper(own[i]);question(q,own,0x21);
    CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==121);
    own[0]='X';question(q,own,0x20);CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);
    question(q,wildcard,0x20);CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);
    wildcard[1]='X';question(q,wildcard,0x21);CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);
    memcpy(own,UAB_NETWORK_HOSTNAME,16);
    for(unsigned i=4;i<=12;i++){question(q,own,0x21);q[i]=3;CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);}
    question(q,own,0x21);q[45]=1;CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);
    question(q,own,0x21);q[49]=2;CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);
    question(q,own,0x22);CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);
    own[15]=0x20;question(q,own,0x21);CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);
    question(q,(const uint8_t[16]){0},0x21);CHECK(uab_nbns_reply(q,50,ip,mac,out,sizeof(out))==0);
    printf("PASS %u network name encoding/bounds/filter checks\n",checks);return 0;
}
