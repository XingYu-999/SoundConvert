// Production bounded socket-1 datagram reader with simulated W5500 registers
// and a 2 KB RX ring. No SPI/board timing acceptance is claimed by this check.
#include <assert.h>
#include <stdio.h>
#include <string.h>
#include "../uab_clock_udp_rx.h"
#include "../uab_udp_completion.h"
#include "../uab_clock_observation.c"
static unsigned checks;
#define CHECK(x) do {checks++;assert(x);} while(0)
typedef struct {
 uint8_t ring[2048];uint16_t pointer,size[4];unsigned size_at,reads,commits,ready_calls;
 bool ready;uint32_t now;
} fixture_t;
static uint16_t size(void*ctx){fixture_t*f=ctx;f->now++;unsigned i=f->size_at++;return f->size[i<4?i:3];}
static uint16_t pointer(void*ctx){fixture_t*f=ctx;f->now++;return f->pointer;}
static bool command_ready(void*ctx){fixture_t*f=ctx;f->now++;f->ready_calls++;return f->ready;}
static void read(uint16_t at,uint8_t*data,uint16_t bytes,void*ctx){fixture_t*f=ctx;f->now++;f->reads++;for(unsigned i=0;i<bytes;i++)data[i]=f->ring[((uint16_t)(at+i))%2048];}
static void commit(uint16_t at,void*ctx){fixture_t*f=ctx;f->now++;f->pointer=at;f->commits++;}
static uint32_t now(void*ctx){return ((fixture_t*)ctx)->now;}
static uab_clock_udp_rx_ops_t ops(fixture_t*f){uab_clock_udp_rx_ops_t o={size,pointer,command_ready,read,commit,now,f};return o;}
static void put(fixture_t*f,uint16_t at,const uint8_t*data,unsigned bytes){for(unsigned i=0;i<bytes;i++)f->ring[((uint16_t)(at+i))%2048]=data[i];}
static fixture_t datagram(uint16_t at,uint16_t bytes,const uint8_t*payload){fixture_t f={0};f.pointer=at;f.ready=true;for(unsigned i=0;i<4;i++)f.size[i]=(uint16_t)(bytes+8);uint8_t header[8]={192,168,8,74,0xc0,0x0a,(uint8_t)(bytes>>8),(uint8_t)bytes};put(&f,at,header,8);if(payload)put(&f,(uint16_t)(at+8),payload,bytes);return f;}
int main(void){
 uint8_t payload[104]={0},data[104],peer[4];uint16_t port;uab_clock_udp_rx_t state={0};uab_clock_udp_rx_timing_t timing;
 memcpy(payload,"UABT",4);payload[4]=1;payload[5]=1;w16(payload+6,104);w64(payload+8,1);w32(payload+16,2);w32(payload+20,3);w64(payload+24,4);w64(payload+32,1);w64(payload+40,1000);
 fixture_t f=datagram(2000,104,payload);uab_clock_udp_rx_ops_t o=ops(&f);
 CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==104);CHECK(!memcmp(data,payload,104));CHECK(peer[0]==192&&peer[3]==74&&port==49162);
 CHECK(f.pointer==2112&&f.commits==1&&f.reads==1&&state.recv_pending);CHECK(timing.header_read&&timing.header_us>0&&timing.read_us>0);
 // One deferred RECV completion check. Busy does not read/change data or spin.
 f.ready=false;unsigned calls=f.ready_calls;CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==0);CHECK(f.ready_calls==calls+1&&f.commits==1&&state.recv_pending);
 f.ready=true;f.size[0]=f.size[1]=f.size[2]=f.size[3]=0;f.size_at=0;CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==0);CHECK(!state.recv_pending&&f.commits==1);
 // Both 2 KB physical-ring and 16-bit RX pointer wrap preserve exact bytes.
 f=datagram(0xfff0,104,payload);o=ops(&f);CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==104);CHECK(f.pointer==96&&!memcmp(data,payload,104));
 // Header-only / partially available packets are never consumed.
 for(unsigned available=0;available<112;available++){f=datagram(77,104,payload);for(unsigned i=0;i<4;i++)f.size[i]=(uint16_t)available;o=ops(&f);state.recv_pending=false;CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==0);CHECK(f.commits==0&&f.pointer==77);}
 // A changing size yields after precisely two samples, with no RX pointer read.
 f=datagram(99,104,payload);f.size[1]=113;o=ops(&f);state.recv_pending=false;CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==0);CHECK(f.size_at==2&&f.reads==0&&f.commits==0);
 // Every non-104 length falls back before touching RX_RD or vendor remaining
 // state. A subsequent recvfrom can still consume its original UDP header.
 const uint16_t lengths[]={0,1,48,103,105,1600};
 for(unsigned i=0;i<sizeof(lengths)/sizeof(lengths[0]);i++){f=datagram(123,lengths[i],NULL);o=ops(&f);CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==UAB_CLOCK_RX_FALLBACK);CHECK(f.pointer==123&&f.commits==0&&f.reads==1&&!state.recv_pending);}
 // Hardware reading establishes no authorization. An invalid identity is
 // consumed exactly once and rejected by the existing owner-bound parser.
 f=datagram(456,104,payload);o=ops(&f);CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==104);uab_clock_observation_t observation={0};uab_clock_identity_t id={1,4,2,3};CHECK(uab_clock_observation_enable(&observation,&id));data[24]^=1;
 CHECK(!uab_clock_observation_receive_from(&observation,data,104,2000,true,true));CHECK(observation.identity_rejected==1&&f.commits==1&&!observation.pending);
 // Valid packet, existing rate protection, and unchanged 100/101 us latch.
 CHECK(uab_clock_observation_receive_from(&observation,payload,104,2000,true,true));w64(payload+32,2);CHECK(!uab_clock_observation_receive_from(&observation,payload,104,2100,true,true));CHECK(observation.limited==1&&observation.requests==1);
 CHECK(uab_clock_observation_record_work(&observation,100));CHECK(!uab_clock_observation_record_work(&observation,101));CHECK(observation.budget_latched&&!observation.enabled&&!uab_clock_observation_enable(&observation,&id));
 // Clock admission services software audio first without reading hardware or
 // consuming a datagram. The same queued UABT remains available afterward.
 for(unsigned combination=1;combination<8;combination++){
  f=datagram(700,104,payload);o=ops(&f);state.recv_pending=false;
  bool admitted=uab_clock_observation_rx_admitted((combination&1)!=0,(combination&2)!=0,(combination&4)?1:0);
  int result=admitted?uab_clock_udp_rx(&state,&o,data,peer,&port,&timing):0;
  CHECK(!admitted&&result==0&&f.reads==0&&f.size_at==0&&f.commits==0&&f.pointer==700&&!state.recv_pending);
  CHECK(uab_clock_observation_rx_admitted(false,false,0));CHECK(uab_clock_udp_rx(&state,&o,data,peer,&port,&timing)==104&&f.commits==1&&f.pointer==812);
 }
 // Hardware SEND ownership is deliberately excluded from admission. Normal
 // grace/SENDOK processing remains responsible for when a reply can submit.
 uab_udp_completion_t hardware={0};uab_udp_completion_submit_kind(&hardware,1,UAB_UDP_AUDIO);CHECK(hardware.pending&&uab_clock_observation_rx_admitted(false,false,0));
 printf("PASS: %u bounded clock UDP RX header/length/available/wrap/vendor-fallback/identity/budget checks (host simulation only)\n",checks);return 0;
}
