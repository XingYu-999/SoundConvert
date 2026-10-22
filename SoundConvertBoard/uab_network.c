/* W5500 hardware sockets are exclusively owned by core 1. USB is core 0. */
#include "uab_network.h"
#include "uab_network_name.h"
#include "uab_udp_ports.h"
#include "uab_audio_packet.h"
#include "uab_audio_tx.h"
#include "uab_udp_completion.h"
#include "uab_clock_observation.h"
#include "uab_clock_udp_rx.h"
#include "uab_clock_spi_frame.h"
#include "uab_clock_capture_service.h"
#include "uab_audio_fec.h"
#include "uab_capture_reorder.h"
#include "uab_boot.h"
#include "uab_indicator.h"
#include "uab_crypto.h"
#include "uab_config_store.h"
#include "usb_control_notify.h"
#include "uab_json_print.h"
#include "uab_watchdog.h"
#include "usb_audio.h"
#include "usb_host_control.h"
#include "pico/stdlib.h"
#include "pico/rand.h"
#include "pico/unique_id.h"
#include "hardware/spi.h"
#include "hardware/uart.h"
#include "hardware/watchdog.h"
#include "wizchip_conf.h"
#include "socket.h"
#include "dhcp.h"
#include "cJSON.h"
#include <stdio.h>
#include <string.h>
#include <stdlib.h>
#include <inttypes.h>
#include <math.h>

#define DISCOVERY_PORT 49160
#define CONTROL_PORT 49161
#define AUDIO_PORT 49162
#define CAPTURE_PORT 49163
#define CAPTURE_SOCKET 5
static uint16_t audio_port=AUDIO_PORT,capture_port=CAPTURE_PORT;
#define FRAME_MAX 8256
#define MODEL UAB_NETWORK_HOSTNAME
#define VERSION UAB_FIRMWARE_VERSION
static uint8_t device_id[16],mac[6];static char device_hex[33];static uint64_t boot_id;
static wiz_NetInfo net;static bool ready,linked,dhcp_candidate,dhcp_conflict;
static uint32_t generation=1,dhcp_attempts,invalid_packets,audio_sent,audio_dropped,poll_max_us;
static uint32_t stream_poll_max_us,stream_control_max_us,stream_events_max_us,stream_raw_max_us;
static uab_audio_tx_t audio_tx;
static struct{bool prepared,running,probed,balanced,have_index;uint32_t id,generation,last_sequence;uint64_t token,keepalive,next_index;uint8_t ip[4];uint16_t port;} capture;
static uint32_t capture_received,capture_invalid,capture_stale,capture_gaps,capture_reordered,capture_poll_max_us;
static uab_capture_reorder_t capture_reorder;
static void capture_stop(void){usb_microphone_set_network_enabled(false);uab_capture_reorder_clear(&capture_reorder);capture.prepared=capture.running=capture.probed=false;capture.have_index=false;}
static uab_audio_fec_t audio_fec;static uint32_t fec_packets_sent,fec_packets_expired;
static uint32_t audio_busy_retries,audio_pending_expired;
static uint64_t config_revision=1,lease_started,lease_seconds,dhcp_retry_at;
static uint8_t dhcp_buffer[1024],udp_buffer[1600];static int owner=-1;static uint64_t owner_until;
static const char *network_state="LINK_DOWN";
static char uart_pending[256];static size_t uart_at,uart_length;
static uint32_t udp_completed[8],udp_timeouts[8],socket_rx[8],socket_tx[8];
static uab_udp_completion_t udp_completion[8];
static uab_udp_destination_t audio_destination;
static uab_clock_observation_t clock_observation;
static uab_clock_anchor_t clock_anchor;
static unsigned clock_receive_budget;
static uint32_t clock_service_loop;
static uab_clock_udp_rx_t clock_udp_rx;
// Core1-only fixed scratch. No heap or large per-call stack buffer; no other
// network service can re-enter while one clock SPI transaction holds CS.
static uint8_t clock_spi_tx[UAB_CLOCK_SPI_FRAME_BYTES],clock_spi_rx[UAB_CLOCK_SPI_FRAME_BYTES];
// Only the first three header bytes are ever changed. Keeping read dummy bytes
// separate from write payload removes repeated clearing and cold libc calls.
static uint8_t clock_spi_read_tx[UAB_CLOCK_SPI_FRAME_BYTES];
static uint32_t spi_actual_hz,udp_software_timeouts;
static uint32_t audio_encode_max_us,audio_pcm_bytes,audio_wire_bytes;
static uint64_t reboot_at;
static uab_config_t committed,active;
static struct {bool pending,applied,usb_changed,network_changed;char id[33];uab_config_t candidate;uint64_t deadline,apply_at;uint32_t usb_revision;const char*final_stage;} transaction;
static uint64_t static_probe_due;static unsigned static_probes;static bool probing,address_conflict,lease_probing;static wiz_NetInfo lease_candidate;
static void discovery_receive(const uint8_t*message,size_t n,const uint8_t*peer,uint16_t port);
static void discovery_replies(void);
static struct {bool prepared,probed,running,follow_usb,fec_enabled;uint8_t codec;uint32_t id,epoch,sequence,generation,sample_rate;uint8_t sample_format;uint64_t token,keepalive;uint8_t ip[4];uint16_t port;} stream;
static uab_clock_identity_t clock_identity(void){uab_clock_identity_t id={boot_id,stream.token,stream.id,stream.follow_usb?stream.generation:stream.epoch};return id;}
typedef struct {
 uint8_t socket,rx[FRAME_MAX+1],tx[FRAME_MAX],peer[4];
 size_t have,want,tx_len,tx_at;uint64_t rx_seq,tx_seq,last,connected_at,frame_at,tx_deadline;bool connected,closing;uint64_t close_at;
 uint32_t last_id,events,stats_interval;uint64_t stats_due;
 struct {uint32_t id;uint64_t expires;uint8_t digest[32];uint16_t size;uint8_t json[256];} cache[32];
 int cache_slot;
} session_t;
static session_t sessions[2];
static void audio_poll(void);static void capture_poll(void);static void clock_observation_poll(void);static uint32_t json_audio_service_at;
static uint8_t json_arena[32768];static size_t json_used;
static void json_audio_service(void*context){(void)context;if(ready&&(stream.running||capture.running)&&(uint32_t)(time_us_32()-json_audio_service_at)>=500){json_audio_service_at=time_us_32();audio_poll();capture_poll();audio_poll();}}
static void *json_alloc(size_t n){json_audio_service(NULL);n=(n+7)&~7u;if(n>sizeof(json_arena)-json_used)return NULL;void *p=json_arena+json_used;json_used+=n;return p;}
static void json_free(void *p){(void)p;}
static uint16_t r16(const uint8_t*p){return (p[0]<<8)|p[1];}
static uint32_t r32(const uint8_t*p){return ((uint32_t)r16(p)<<16)|r16(p+2);}
static uint64_t r64(const uint8_t*p){return ((uint64_t)r32(p)<<32)|r32(p+4);}
static void w16(uint8_t*p,uint16_t x){p[0]=x>>8;p[1]=x;}
static void w32(uint8_t*p,uint32_t x){w16(p,x>>16);w16(p+2,x);}
static void w64(uint8_t*p,uint64_t x){w32(p,x>>32);w32(p+4,x);}
static uint64_t now_ms(void){return time_us_64()/1000;}
static void random_bytes(uint8_t*p,size_t n){while(n){uint32_t x=get_rand_32();for(int i=0;i<4&&n;i++,n--)*p++=(uint8_t)(x>>(8*i));}}
static void cs_on(void){gpio_put(17,0);}static void cs_off(void){gpio_put(17,1);}
static uint8_t spi_read(void){uint8_t x;spi_read_blocking(spi0,0,&x,1);return x;}
static void spi_write(uint8_t x){spi_write_blocking(spi0,&x,1);}
static void spi_reads(uint8_t*p,uint16_t n){spi_read_blocking(spi0,0,p,n);}
static void spi_writes(uint8_t*p,uint16_t n){spi_write_blocking(spi0,p,n);}
static void stream_stop(void){stream.running=stream.prepared=stream.probed=false;uab_audio_tx_clear(&audio_tx);uab_audio_fec_clear(&audio_fec);uab_clock_observation_disable(&clock_observation);clock_anchor.valid=false;uab_udp_destination_reset(&audio_destination);}
static void reset_session(session_t*s){uint8_t sn=s->socket;if(owner==sn){owner=-1;owner_until=now_ms()+2000;uab_clock_observation_disable(&clock_observation);}memset(s,0,sizeof(*s));s->socket=sn;s->want=32;s->last=now_ms();s->cache_slot=-1;}
static void close_session(session_t*s){uint8_t sr=getSn_SR(s->socket);if(sr==SOCK_ESTABLISHED||sr==SOCK_CLOSE_WAIT){if(!s->closing){s->closing=true;s->close_at=now_ms()+500;setSn_CR(s->socket,Sn_CR_DISCON);}}else{close(s->socket);reset_session(s);}}
static void stop_business(void){ready=false;stream_stop();capture_stop();for(int i=0;i<2;i++){close(sessions[i].socket);reset_session(&sessions[i]);}close(1);close(4);close(7);uab_udp_completion_reset(&udp_completion[1]);uab_udp_completion_reset(&udp_completion[4]);uab_udp_completion_reset(&udp_completion[7]);}
static void clear_address(void){stop_business();probing=false;lease_probing=false;memset(net.ip,0,4);memset(net.gw,0,4);memset(net.sn,0,4);wizchip_setnetinfo(&net);generation++;lease_seconds=0;}
static void dhcp_assigned(void){dhcp_candidate=true;}
static void dhcp_conflicted(void){dhcp_conflict=true;}
static bool dhcp_tick(struct repeating_timer*t){(void)t;DHCP_time_handler();return true;}
static void start_dhcp(void){DHCP_stop();clear_address();address_conflict=false;DHCP_init(3,dhcp_buffer);reg_dhcp_cbfunc(dhcp_assigned,dhcp_assigned,dhcp_conflicted);dhcp_attempts++;network_state="DHCP_ACQUIRING";dhcp_retry_at=0;}
static void start_address_mode(void){probing=false;address_conflict=false;if(active.mode==0){start_dhcp();return;}DHCP_stop();clear_address();probing=true;static_probes=0;static_probe_due=now_ms()+(get_rand_32()%1000);network_state="STATIC_PROBING";}
static void address_poll(void){
 uint8_t link=0;ctlwizchip(CW_GET_PHYLINK,&link);
 if(!link){if(linked){DHCP_stop();clear_address();puts("NET LINK_DOWN");}linked=false;network_state="LINK_DOWN";return;}
 if(!linked){linked=true;puts("NET LINK_UP");start_address_mode();}
 if(active.mode)return;
 if(dhcp_retry_at){if(now_ms()<dhcp_retry_at)return;start_dhcp();}
 uint8_t status=DHCP_run();
 if(dhcp_conflict){dhcp_conflict=false;DHCP_stop();clear_address();network_state="ADDRESS_CONFLICT";dhcp_retry_at=now_ms()+10000;return;}
 if(dhcp_candidate){
  dhcp_candidate=false;wiz_NetInfo candidate=net;getIPfromDHCP(candidate.ip);getSNfromDHCP(candidate.sn);getGWfromDHCP(candidate.gw);getDNSfromDHCP(candidate.dns);
  bool changed=memcmp(candidate.ip,net.ip,4)||memcmp(candidate.sn,net.sn,4)||memcmp(candidate.gw,net.gw,4);
  if(changed){clear_address();lease_candidate=candidate;lease_probing=true;probing=true;static_probes=0;static_probe_due=now_ms()+(get_rand_32()%1000);network_state="DHCP_ACQUIRING";}
  else{ready=true;network_state="DHCP_BOUND";}
  lease_started=now_ms();lease_seconds=getDHCPLeasetime();
 }
 if(status==DHCP_FAILED){DHCP_stop();clear_address();network_state="DHCP_WAITING";dhcp_retry_at=now_ms()+10000;}
 if(ready&&lease_seconds!=0xffffffffu&&now_ms()-lease_started>=lease_seconds*1000){puts("NET LEASE_EXPIRED");start_dhcp();}
}

/* Completion retains the actual datagram kind until SENDOK/timeout. A sideband
 * timeout reopens only the hardware UDP socket and revokes observations; the
 * validated audio subscription/owner remain live. No wait/retry loop occurs. */
static bool udp_poll_completion(uint8_t sn){
 uint8_t state=getSn_SR(sn);if(state!=(sn==0?SOCK_MACRAW:SOCK_UDP)){uab_udp_completion_reset(&udp_completion[sn]);if(sn==1)uab_udp_destination_reset(&audio_destination);return false;}
 uint8_t ir=getSn_IR(sn);uab_udp_event_t event=uab_udp_completion_poll(&udp_completion[sn],(ir&Sn_IR_SENDOK)!=0,(ir&Sn_IR_TIMEOUT)!=0,time_us_32());
 if(ir&Sn_IR_SENDOK)setSn_IR(sn,Sn_IR_SENDOK);
 if(event.type==UAB_UDP_EVENT_COMPLETED){if(event.kind==UAB_UDP_CLOCK)clock_observation.completed++;else udp_completed[sn]++;}
 if(event.type==UAB_UDP_EVENT_TIMEOUT){bool clock=event.kind==UAB_UDP_CLOCK;if(clock){clock_observation.timeouts++;if(event.software_timeout)clock_observation.expired++;uab_clock_observation_disable(&clock_observation);}else{if(event.software_timeout)udp_software_timeouts++;udp_timeouts[sn]++;}if(sn==1)uab_udp_destination_reset(&audio_destination);close(sn);if(sn==1&&!clock)stream_stop();return false;}
 return true;
}
/* Submit UDP without waiting for ARP/SENDOK. Completion is polled later. */
static bool udp_send_kind(uint8_t sn,const uint8_t*data,uint16_t n,const uint8_t ip[4],uint16_t port,uab_udp_kind_t kind){
 if(!udp_poll_completion(sn))return false;
 // CR cleared / TX memory drained does not establish SENDOK. Keep ownership until completion.
 if(!uab_udp_completion_ready(&udp_completion[sn]))return false;
 if(getSn_CR(sn)||getSn_TX_FSR(sn)<n||getSn_TX_RD(sn)!=getSn_TX_WR(sn))return false;
 setSn_DIPR(sn,(uint8_t*)ip);setSn_DPORT(sn,port);if(sn==1)uab_udp_destination_record(&audio_destination,ip,port);wiz_send_data(sn,(uint8_t*)data,n);setSn_CR(sn,Sn_CR_SEND);uab_udp_completion_submit_kind(&udp_completion[sn],time_us_32(),kind);if(kind!=UAB_UDP_CLOCK)socket_tx[sn]++;return true;
}
static bool udp_send(uint8_t sn,const uint8_t*data,uint16_t n,const uint8_t ip[4],uint16_t port){return udp_send_kind(sn,data,n,ip,port,UAB_UDP_OTHER);}
static void UAB_CLOCK_RAM(clock_spi_read)(uint32_t address,uint8_t*data,uint16_t bytes){
 unsigned count=uab_clock_spi_pack_zeroed_read(address,bytes,clock_spi_read_tx,sizeof(clock_spi_read_tx));if(!count)return;
 gpio_put(17,0);spi_write_read_blocking(spi0,clock_spi_read_tx,clock_spi_rx,count);gpio_put(17,1);
 uab_clock_spi_unpack_read(clock_spi_rx,data,bytes);
}
static void UAB_CLOCK_RAM(clock_spi_write)(uint32_t address,const uint8_t*data,uint16_t bytes){
 unsigned count=uab_clock_spi_pack_write(address,data,bytes,clock_spi_tx,sizeof(clock_spi_tx));if(!count)return;
 gpio_put(17,0);spi_write_read_blocking(spi0,clock_spi_tx,clock_spi_rx,count);gpio_put(17,1);
}
static uint16_t UAB_CLOCK_RAM(clock_read16)(uint32_t address){uint8_t b[2];clock_spi_read(address,b,2);return r16(b);}
static void UAB_CLOCK_RAM(clock_write16)(uint32_t address,uint16_t value){uint8_t b[2];w16(b,value);clock_spi_write(address,b,2);}
static bool UAB_CLOCK_RAM(clock_retire_audio_completion)(void){
 // W5500 CR/IR/SR are contiguous at 0x0001..0x0003. Keep all three checks,
 // but fetch them in one burst instead of three separate SPI transactions.
 uint8_t registers[3];clock_spi_read(Sn_CR(1),registers,3);
 if(registers[2]!=SOCK_UDP){uab_udp_completion_reset(&udp_completion[1]);uab_udp_destination_reset(&audio_destination);return false;}
 uab_udp_event_t event=uab_udp_completion_poll(&udp_completion[1],(registers[1]&Sn_IR_SENDOK)!=0,(registers[1]&Sn_IR_TIMEOUT)!=0,time_us_32());
 if(registers[1]&Sn_IR_SENDOK){uint8_t clear=Sn_IR_SENDOK;clock_spi_write(Sn_IR(1),&clear,1);}
 if(event.type==UAB_UDP_EVENT_COMPLETED){if(event.kind==UAB_UDP_CLOCK)clock_observation.completed++;else udp_completed[1]++;}
 if(event.type==UAB_UDP_EVENT_TIMEOUT){bool clock=event.kind==UAB_UDP_CLOCK;if(clock){clock_observation.timeouts++;if(event.software_timeout)clock_observation.expired++;uab_clock_observation_disable(&clock_observation);}else{if(event.software_timeout)udp_software_timeouts++;udp_timeouts[1]++;}uab_udp_destination_reset(&audio_destination);close(1);if(!clock)stream_stop();return false;}
 return registers[0]==0&&uab_udp_completion_ready(&udp_completion[1]);
}
static bool UAB_CLOCK_RAM(clock_submit_reply)(uint32_t service_began){
 // Clock only: retain ordinary PCM/FEC/probe sending without alteration. Read
 // each 16-bit register as a burst and bound the FSR consistency comparison.
 // The outer clock service just retired completion and validated socket state.
 // Core 1 is the sole network owner; no yielding/re-entry occurs in between.
 if(!uab_udp_completion_ready(&udp_completion[1]))return false;
 uint16_t room=clock_read16(Sn_TX_FSR(1));if(room<UAB_CLOCK_PACKET_BYTES||room!=clock_read16(Sn_TX_FSR(1)))return false;
 uint8_t pointers[4];clock_spi_read(Sn_TX_RD(1),pointers,4);uint16_t rd=r16(pointers),wr=r16(pointers+2);if(rd!=wr)return false;
 if(!uab_clock_observation_record_work_phase(&clock_observation,time_us_32()-service_began,UAB_CLOCK_BUDGET_TX_PREPARE))return false;
 uint8_t reply[UAB_CLOCK_PACKET_BYTES];if(!uab_clock_observation_reply(&clock_observation,&clock_anchor,time_us_64(),reply))return false;
 if(!uab_udp_destination_matches(&audio_destination,stream.ip,stream.port)){uint8_t destination[6];memcpy(destination,stream.ip,4);w16(destination+4,stream.port);clock_spi_write(Sn_DIPR(1),destination,6);uab_udp_destination_record(&audio_destination,stream.ip,stream.port);}
 clock_spi_write(((uint32_t)wr<<8)+(WIZCHIP_TXBUF_BLOCK(1)<<3),reply,UAB_CLOCK_PACKET_BYTES);
 // Payload has only been copied into unused TX memory. Until TX_WR/SEND are
 // committed it is safe to abandon an over-budget attempt; audio may overwrite.
 if(!uab_clock_observation_record_work_phase(&clock_observation,time_us_32()-service_began,UAB_CLOCK_BUDGET_TX_PRECOMMIT))return false;
 clock_write16(Sn_TX_WR(1),(uint16_t)(wr+UAB_CLOCK_PACKET_BYTES));uint8_t command=Sn_CR_SEND;clock_spi_write(Sn_CR(1),&command,1);
 uab_udp_completion_submit_kind(&udp_completion[1],time_us_32(),UAB_UDP_CLOCK);return true;
}
static int udp_receive(uint8_t sn,uint8_t*buf,size_t cap,uint8_t ip[4],uint16_t*port){
 if(getSn_RX_RSR(sn)==0)return 0;int32_t n=recvfrom(sn,buf,(uint16_t)cap,ip,port);
 uint8_t pack=0;getsockopt(sn,SO_PACKINFO,&pack);if(pack&PACK_REMAINED){if(sn==1)uab_udp_destination_reset(&audio_destination);close(sn);uab_udp_completion_reset(&udp_completion[sn]);invalid_packets++;return 0;}if(n>0)socket_rx[sn]++;return n>0?n:0;
}
static uint16_t UAB_CLOCK_RAM(clock_rx_size)(void*context){(void)context;return clock_read16(Sn_RX_RSR(1));}
static uint16_t UAB_CLOCK_RAM(clock_rx_pointer)(void*context){(void)context;return clock_read16(Sn_RX_RD(1));}
static bool UAB_CLOCK_RAM(clock_rx_command_ready)(void*context){(void)context;uint8_t command;clock_spi_read(Sn_CR(1),&command,1);return command==0;}
static void UAB_CLOCK_RAM(clock_rx_read)(uint16_t pointer,uint8_t*data,uint16_t bytes,void*context){(void)context;clock_spi_read(((uint32_t)pointer<<8)+(WIZCHIP_RXBUF_BLOCK(1)<<3),data,bytes);}
static void UAB_CLOCK_RAM(clock_rx_commit)(uint16_t pointer,void*context){(void)context;clock_write16(Sn_RX_RD(1),pointer);uint8_t command=Sn_CR_RECV;clock_spi_write(Sn_CR(1),&command,1);}
static uint32_t UAB_CLOCK_RAM(clock_rx_now)(void*context){(void)context;return time_us_32();}
static uab_clock_udp_rx_ops_t clock_rx_ops={clock_rx_size,clock_rx_pointer,clock_rx_command_ready,clock_rx_read,clock_rx_commit,clock_rx_now,NULL};
static int UAB_CLOCK_RAM(audio_udp_receive)(uint8_t*buffer,size_t capacity,uint8_t peer[4],uint16_t*port,uint32_t service_began){
 // Retire the one nonblocking RECV even if a safety latch just disabled clock.
 // No ordinary Legacy receive pays for an extra CR read.
 if(clock_udp_rx.recv_pending&&!clock_observation.enabled){if(!clock_rx_command_ready(NULL))return 0;clock_udp_rx.recv_pending=false;}
 if(!clock_observation.enabled)return udp_receive(1,buffer,capacity,peer,port);
 uab_clock_udp_rx_timing_t timing;int n=uab_clock_udp_rx(&clock_udp_rx,&clock_rx_ops,buffer,peer,port,&timing);
 uint64_t b2=n>0?time_us_64():0;
 if(timing.header_read&&timing.header_us>clock_observation.rx_header_max_us)clock_observation.rx_header_max_us=timing.header_us;
 if(timing.read_us>clock_observation.rx_read_max_us)clock_observation.rx_read_max_us=timing.read_us;
 if(n==UAB_CLOCK_RX_FALLBACK)return udp_receive(1,buffer,capacity,peer,port);
 if(n>0)socket_rx[1]++;
 if(n>=4&&buffer[0]=='U'&&buffer[1]=='A'&&buffer[2]=='B'&&buffer[3]=='T'){
  uint32_t validate_began=time_us_32();
  if(!clock_receive_budget)clock_observation.skipped++;
  else{clock_receive_budget--;bool current_peer=stream.running&&peer[0]==stream.ip[0]&&peer[1]==stream.ip[1]&&peer[2]==stream.ip[2]&&peer[3]==stream.ip[3]&&*port==stream.port;uab_clock_observation_receive_from(&clock_observation,buffer,(size_t)n,b2,current_peer,owner>=0);}
  uint32_t validation=time_us_32()-validate_began;if(validation>clock_observation.rx_validate_max_us)clock_observation.rx_validate_max_us=validation;
  uab_clock_observation_record_work_phase(&clock_observation,time_us_32()-service_began,UAB_CLOCK_BUDGET_RX);
  // All optional clock handling is now complete in RAM. The ordinary caller
  // takes its common no-audio-datagram branch, without a cold Flash demux.
  return 0;
 }
 return n;
}
static void raw_poll(void){
 if(getSn_SR(0)!=SOCK_MACRAW){uab_udp_completion_reset(&udp_completion[0]);socket(0,Sn_MR_MACRAW,0,SF_IO_NONBLOCK|SF_ETHER_OWN|SF_MULTI_BLOCK|SF_IPv6_BLOCK);}
 uint8_t ignored[4]={0};uint16_t port;for(int i=0;i<1;i++){int n=udp_receive(0,udp_buffer,sizeof(udp_buffer),ignored,&port);if(n<=0)break;
  if(n>=42&&r16(udp_buffer+12)==0x0806&&r16(udp_buffer+14)==1&&r16(udp_buffer+16)==0x0800&&udp_buffer[18]==6&&udp_buffer[19]==4&&memcmp(udp_buffer+22,mac,6)&&(active.mode||lease_probing||ready)){
   const uint8_t*target=lease_probing?lease_candidate.ip:active.mode?active.ip:net.ip;uint8_t zero[4]={0};bool conflict=!memcmp(udp_buffer+28,target,4)||(probing&&!memcmp(udp_buffer+28,zero,4)&&!memcmp(udp_buffer+38,target,4));
   if(conflict){address_conflict=true;bool dhcp=!active.mode;probing=false;clear_address();if(dhcp){DHCP_stop();dhcp_retry_at=now_ms()+10000;}network_state="ADDRESS_CONFLICT";puts("NET ADDRESS_CONFLICT");}
  }
 }
 if(probing&&linked&&now_ms()>=static_probe_due){
  uint8_t arp[60]={0};const uint8_t*target=lease_probing?lease_candidate.ip:active.ip;memset(arp,255,6);memcpy(arp+6,mac,6);w16(arp+12,0x0806);w16(arp+14,1);w16(arp+16,0x0800);arp[18]=6;arp[19]=4;w16(arp+20,1);memcpy(arp+22,mac,6);memcpy(arp+38,target,4);
  if(static_probes>=3){if(lease_probing){net=lease_candidate;net.dhcp=NETINFO_DHCP;network_state="DHCP_BOUND";}else{memcpy(net.ip,active.ip,4);memcpy(net.sn,active.mask,4);memcpy(net.gw,active.gateway,4);memcpy(net.dns,active.dns,4);net.dhcp=NETINFO_STATIC;network_state="STATIC_ACTIVE";}wizchip_setnetinfo(&net);ready=true;probing=false;lease_probing=false;memcpy(arp+28,net.ip,4);printf("NET %s %u.%u.%u.%u\n",network_state,net.ip[0],net.ip[1],net.ip[2],net.ip[3]);}
  if(udp_send(0,arp,sizeof(arp),ignored,0)){static_probes++;static_probe_due=now_ms()+1000+(get_rand_32()%1000);}
 }
}
static size_t tlv(uint8_t*p,uint16_t type,const void*v,uint16_t n){w16(p,type);w16(p+2,n);memcpy(p+4,v,n);return n+4;}
static void discovery_poll(void){
 if(getSn_SR(4)==SOCK_CLOSED){uab_udp_completion_reset(&udp_completion[4]);socket(4,Sn_MR_UDP,DISCOVERY_PORT,SF_IO_NONBLOCK);}
 uint8_t ip[4];uint16_t port;int n=udp_receive(4,udp_buffer,1024,ip,&port);if(!n)return;
 discovery_receive(udp_buffer,n,ip,port);
}

/* Spare socket 7, one packet per 100 ms, same-subnet unicast replies only.
 * Keep name-service traffic bounded and use the existing nonblocking TX owner. */
static void network_name_poll(void){
 static uint64_t due;uint64_t now=now_ms();if(now<due)return;due=now+100;
 if(getSn_SR(7)==SOCK_CLOSED){uab_udp_completion_reset(&udp_completion[7]);socket(7,Sn_MR_UDP,UAB_NBNS_PORT,SF_IO_NONBLOCK);}
 uint8_t peer[4];uint16_t port;int n=udp_receive(7,udp_buffer,256,peer,&port);if(!n||!port)return;
 bool different=false;for(unsigned i=0;i<4;i++){if((peer[i]&net.sn[i])!=(net.ip[i]&net.sn[i]))return;if(peer[i]&~net.sn[i])different=true;}
 if(!different||peer[0]==0||peer[0]==127||peer[0]>=224||!memcmp(peer,net.ip,4))return;
 bool broadcast=true;for(unsigned i=0;i<4;i++)if((uint8_t)(peer[i]|net.sn[i])!=255)broadcast=false;if(broadcast)return;
 uint8_t reply[121];size_t size=uab_nbns_reply(udp_buffer,(size_t)n,net.ip,mac,reply,sizeof(reply));
 if(size)udp_send(7,reply,(uint16_t)size,peer,port);
}

static cJSON* obj(void){return cJSON_CreateObject();}
static void js(cJSON*o,const char*k,const char*v){cJSON_AddStringToObject(o,k,v);}
static void jnum(cJSON*o,const char*k,double v){cJSON_AddNumberToObject(o,k,v);}
static void jb(cJSON*o,const char*k,bool v){cJSON_AddBoolToObject(o,k,v);}
static void ju64(cJSON*o,const char*k,uint64_t v){char s[24];snprintf(s,sizeof(s),"%" PRIu64,v);js(o,k,s);}
static const char* str(cJSON*o,const char*k){cJSON*v=cJSON_GetObjectItemCaseSensitive(o,k);return cJSON_IsString(v)?v->valuestring:NULL;}
static int number(cJSON*o,const char*k,int fallback){cJSON*v=cJSON_GetObjectItemCaseSensitive(o,k);return cJSON_IsNumber(v)&&isfinite(v->valuedouble)&&v->valuedouble==v->valueint?v->valueint:fallback;}
static bool udp_port_busy(uint16_t port,void*context){uint8_t own=*(uint8_t*)context;for(uint8_t sn=1;sn<8;sn++)if(sn!=own&&getSn_SR(sn)!=SOCK_CLOSED&&getSn_PORT(sn)==port)return true;return false;}
static uint16_t udp_port_request(cJSON*args,int legacy,uint8_t sn){cJSON*p=cJSON_GetObjectItemCaseSensitive(args,"source_port");int wanted=!p||cJSON_IsNull(p)?legacy:number(args,"source_port",-1);return uab_udp_port_select(wanted,get_rand_32(),udp_port_busy,&sn);}
static bool udp_port_bind(uint8_t sn,uint16_t port){close(sn);uab_udp_completion_reset(&udp_completion[sn]);if(sn==1){uab_udp_destination_reset(&audio_destination);clock_udp_rx.recv_pending=false;}return socket(sn,Sn_MR_UDP,port,SF_IO_NONBLOCK)==sn&&getSn_SR(sn)==SOCK_UDP&&getSn_PORT(sn)==port;}
static uint64_t uint64_arg(cJSON*o,const char*k,uint64_t fallback){const char*s=str(o,k);if(s){uint64_t x=0;if(!*s)return fallback;for(;*s;s++){if(*s<'0'||*s>'9')return fallback;uint64_t digit=*s-'0';if(x>(UINT64_MAX-digit)/10)return fallback;x=x*10+digit;}return x;}cJSON*v=cJSON_GetObjectItemCaseSensitive(o,k);if(cJSON_IsNumber(v)&&isfinite(v->valuedouble)&&v->valuedouble>=0&&v->valuedouble<=9007199254740991.0&&floor(v->valuedouble)==v->valuedouble)return (uint64_t)v->valuedouble;return fallback;}
static void jip(cJSON*o,const char*k,const uint8_t ip[4]){char s[20];snprintf(s,sizeof(s),"%u.%u.%u.%u",ip[0],ip[1],ip[2],ip[3]);js(o,k,s);}
static bool valid_json(cJSON*v,int depth){if(depth>16)return false;int count=0;for(cJSON*c=v->child;c;c=c->next){if(++count>256)return false;if(c->string)for(cJSON*p=v->child;p!=c;p=p->next)if(p->string&&!strcmp(p->string,c->string))return false;if(!valid_json(c,depth+1))return false;}return !cJSON_IsNumber(v)||isfinite(v->valuedouble);}
static bool utf8_valid(const uint8_t*s,size_t n){for(size_t i=0;i<n;){uint32_t c=s[i++];if(c<0x80){if(!c)return false;continue;}unsigned need;uint32_t minimum;if(c>=0xc2&&c<=0xdf){need=1;c&=31;minimum=0x80;}else if(c>=0xe0&&c<=0xef){need=2;c&=15;minimum=0x800;}else if(c>=0xf0&&c<=0xf4){need=3;c&=7;minimum=0x10000;}else return false;if(i+need>n)return false;while(need--){if((s[i]&0xc0)!=0x80)return false;c=(c<<6)|(s[i++]&63);}if(c<minimum||c>0x10ffff||(c>=0xd800&&c<=0xdfff))return false;}return true;}
static const char* format_name(uint8_t format){return format==2?"S24LE_PACKED":"S16LE";}
static cJSON* format_json_for(uint32_t rate,uint8_t format){cJSON*f=obj();jnum(f,"sample_rate",rate);jnum(f,"channels",2);jnum(f,"bits",format==2?24:16);js(f,"sample_format",format_name(format));return f;}
static uint32_t config_formats(const uab_config_t*c){return c->formats_mask?c->formats_mask:usb_formats_legacy(c->hi_res_enabled);}
static cJSON* declared_formats(uint32_t mask){cJSON*a=cJSON_CreateArray();for(unsigned i=0;i<USB_FORMAT_COUNT;i++)if(mask&(1u<<i))cJSON_AddItemToArray(a,format_json_for(usb_formats[i].rate,usb_format_code(usb_formats[i].bits)));return a;}
static cJSON* host_control_json(void){usb_host_control_status_t h;usb_host_control_get_status(&h);cJSON*d=obj();jb(d,"ready",h.ready);jnum(d,"pending",h.pending);jnum(d,"queued",h.queued);jnum(d,"completed",h.completed);jnum(d,"dropped",h.dropped);jnum(d,"last_usage",h.last_usage);js(d,"transport","hid_consumer");jb(d,"system_volume_known",false);return d;}
static cJSON* internal_volume_json(const usb_audio_status_t*u){cJSON*d=obj();jnum(d,"percent",u->internal_volume_percent);jb(d,"mute",u->internal_mute);ju64(d,"revision",u->internal_volume_revision);jnum(d,"default_percent",80);js(d,"scope","internal_playback_runtime");return d;}
static cJSON* clock_observation_json(void){cJSON*d=obj();jb(d,"enabled",clock_observation.enabled);jb(d,"pending",clock_observation.pending);jb(d,"budget_latched",clock_observation.budget_latched);jnum(d,"budget_latched_us",clock_observation.budget_latched_us);js(d,"budget_latched_phase",clock_observation.budget_latched_phase==UAB_CLOCK_BUDGET_RX?"rx":clock_observation.budget_latched_phase==UAB_CLOCK_BUDGET_TX_PREPARE?"tx_prepare":clock_observation.budget_latched_phase==UAB_CLOCK_BUDGET_TX_PRECOMMIT?"tx_precommit":clock_observation.budget_latched_phase==UAB_CLOCK_BUDGET_TX_COMPLETE?"tx_complete":"unknown");jnum(d,"service_budget_us",UAB_CLOCK_SERVICE_BUDGET_US);jnum(d,"service_budget_breaches",clock_observation.service_budget_breaches);jnum(d,"rx_header_max_us",clock_observation.rx_header_max_us);jnum(d,"rx_read_max_us",clock_observation.rx_read_max_us);jnum(d,"rx_validate_max_us",clock_observation.rx_validate_max_us);jnum(d,"tx_prepare_max_us",clock_observation.tx_prepare_max_us);jnum(d,"tx_submit_max_us",clock_observation.tx_submit_max_us);jnum(d,"preflight_attempts",clock_observation.preflight_attempts);jnum(d,"preflight_deferred",clock_observation.preflight_deferred);jnum(d,"preflight_phase_waits",clock_observation.preflight_phase_waits);jnum(d,"preflight_min_age_us",UAB_CLOCK_PREFLIGHT_MIN_AGE_US);jnum(d,"audio_tx_grace_us",UAB_UDP_CLOCK_AUDIO_GRACE_US);jnum(d,"preflight_audio_tx_waits",clock_observation.preflight_audio_tx_waits);jb(d,"last_preflight_pending",clock_observation.last_preflight_pending);jnum(d,"last_preflight_pending_kind",clock_observation.last_preflight_pending_kind);jnum(d,"last_preflight_submitted_age_us",clock_observation.last_preflight_submitted_age_us);jb(d,"last_busy_pending",clock_observation.last_busy_pending);jnum(d,"last_busy_pending_kind",clock_observation.last_busy_pending_kind);jnum(d,"last_busy_submitted_age_us",clock_observation.last_busy_submitted_age_us);jnum(d,"last_busy_stage",clock_observation.last_busy_stage);jnum(d,"version",1);jnum(d,"stream_id",clock_observation.identity.stream_id);jnum(d,"epoch",clock_observation.identity.epoch);jnum(d,"time_requests_accepted",clock_observation.requests);jnum(d,"rx_audio_priority_deferrals",clock_observation.rx_audio_priority_deferrals);jnum(d,"time_rx_invalid",clock_observation.invalid);jnum(d,"time_identity_rejected",clock_observation.identity_rejected);jnum(d,"time_replay_rejected",clock_observation.replay_rejected);jnum(d,"time_rx_limited",clock_observation.limited);jnum(d,"time_tx_busy",clock_observation.busy);jnum(d,"time_tx_skipped",clock_observation.skipped);jnum(d,"time_tx_expired",clock_observation.expired);jnum(d,"time_packets_submitted",clock_observation.submitted);jnum(d,"time_send_completions",clock_observation.completed);jnum(d,"time_send_timeouts",clock_observation.timeouts);jnum(d,"revocations",clock_observation.revoked);jnum(d,"processing_max_us",clock_observation.processing_max_us);js(d,"anchor_sample_point","usb_fifo_extract_complete_software");return d;}
static cJSON* usb_control_json(const usb_audio_status_t*u){cJSON*d=obj();ju64(d,"revision",u->control_revision);jb(d,"master_mute",u->mute[0]);jnum(d,"master_volume_q8",u->volume_q8[0]);jnum(d,"master_volume_db",u->volume_q8[0]/256.0);js(d,"source","usb_host");js(d,"write_policy","usb_host_only");jnum(d,"minimum_db",-60);jnum(d,"maximum_db",0);jnum(d,"step_db",1.0/256.0);return d;}
static void response(session_t*s,uint32_t id,bool ok,const char*code,const char*message,cJSON*data,uint64_t seq){
 cJSON*r=obj();jb(r,"ok",ok);js(r,"code",code);js(r,"message",message);cJSON_AddItemToObject(r,"data",data?data:obj());ju64(r,"config_revision",config_revision);
 memset(s->tx,0,32);if(!uab_json_print(r,(char*)s->tx+32,8193,json_audio_service,NULL)){close_session(s);return;}
 size_t n=strlen((char*)s->tx+32);memcpy(s->tx,"UABC",4);s->tx[4]=1;s->tx[5]=2;w32(s->tx+8,n);w32(s->tx+12,id);w64(s->tx+16,seq);w64(s->tx+24,boot_id);s->tx_len=32+n;s->tx_at=0;s->tx_deadline=now_ms()+3000;
 if(s->cache_slot>=0){unsigned slot=s->cache_slot;if(n<=256){s->cache[slot].size=n;memcpy(s->cache[slot].json,s->tx+32,n);}else if(ok&&data&&cJSON_GetObjectItemCaseSensitive(data,"session_token")){
   /* Compact STREAM_PREPARE result: bounded cache, no repeated allocation/token. */
   s->cache[slot].size=UINT16_MAX;uint8_t*p=s->cache[slot].json;w32(p,(uint32_t)uint64_arg(data,"stream_id",0));w32(p+4,(uint32_t)uint64_arg(data,"epoch",0));w64(p+8,uint64_arg(data,"session_token",0));w16(p+16,(uint16_t)number(data,"source_port",AUDIO_PORT));p[18]=cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(data,"waiting_usb"));cJSON*fmt=cJSON_GetObjectItemCaseSensitive(data,"format");w32(p+19,number(fmt,"sample_rate",48000));p[23]=usb_format_code(number(fmt,"bits",16));p[24]=cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(data,"follow_usb"));p[25]=str(data,"fec_mode")&&!strcmp(str(data,"fec_mode"),"xor4");p[26]=str(data,"codec")&&!strcmp(str(data,"codec"),"ima_adpcm");p[27]=number(fmt,"channels",2)==1;p[28]=cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(data,"host_active"));
  }s->cache_slot=-1;}
}
static bool take_owner(session_t*s){if(owner<0){owner=s->socket;owner_until=0;}return owner==s->socket;}
static cJSON* firmware_json(void){
 cJSON*d=obj();js(d,"device_id",device_hex);ju64(d,"boot_id",boot_id);js(d,"model",MODEL);
 js(d,"firmware_version",VERSION);jnum(d,"major",UAB_VERSION_MAJOR);jnum(d,"minor",UAB_VERSION_MINOR);jnum(d,"patch",UAB_VERSION_PATCH);jnum(d,"protocol",1);return d;
}
static cJSON* capture_format_json(const usb_mic_status_t*m){cJSON*f=obj();jnum(f,"sample_rate",m->rate);jnum(f,"channels",1);jnum(f,"bits",m->bits);js(f,"sample_format",m->bits==24?"S24LE_PACKED":"S16LE");return f;}
static cJSON* capture_diagnostics_json(const usb_mic_status_t*m){
 cJSON*d=obj();
 ju64(d,"last_underrun_us64",m->last_underrun_us64);
 jnum(d,"last_underrun_fifo_bytes",m->last_underrun_fifo_bytes);
 jnum(d,"last_underrun_target_bytes",m->last_underrun_target_bytes);
 jnum(d,"last_underrun_task_gap_us",m->last_underrun_task_gap_us);
 jnum(d,"last_underrun_enqueue_age_us",m->last_underrun_enqueue_age_us);
 jb(d,"last_underrun_enqueue_timing_valid",m->last_underrun_enqueue_timing_valid);
 jnum(d,"maximum_task_gap_us",m->maximum_task_gap_us);
 jnum(d,"maximum_enqueue_gap_us",m->maximum_enqueue_gap_us);
 jnum(d,"fallback_concealed_frames",m->fallback_concealed_frames);
 jnum(d,"last_underrun_queued_packets",m->last_underrun_queued_packets);
 jnum(d,"last_underrun_pending_bytes",m->last_underrun_pending_bytes);
 jnum(d,"last_underrun_staged_bytes",m->last_underrun_staged_bytes);
 jnum(d,"last_underrun_tx_packets",m->last_underrun_tx_packets);
 return d;
}
static cJSON* capture_json(void){usb_mic_status_t m;usb_microphone_get_status(&m);cJSON*d=obj();jb(d,"active",m.active);jb(d,"primed",m.primed);jnum(d,"alternate_setting",m.alternate_setting);jnum(d,"generation",m.generation);jnum(d,"formats_mask",m.formats_mask);cJSON_AddItemToObject(d,"format",capture_format_json(&m));jnum(d,"fifo_bytes",m.fifo_bytes);jnum(d,"queue_packets",m.queued_packets);jnum(d,"received_packets",capture_received);jnum(d,"invalid_packets",capture_invalid);jnum(d,"stale_datagrams",capture_stale);jnum(d,"missing_frames",capture_gaps);jnum(d,"reordered_packets",capture_reorder.reordered);jnum(d,"late_packets",capture_reorder.late);jnum(d,"reorder_overflows",capture_reorder.overflows);jnum(d,"stale_packets",m.stale_packets);jnum(d,"peak_fifo_bytes",m.peak_fifo_bytes);jnum(d,"queue_drops",m.queue_drops);jnum(d,"underruns",m.underruns);jnum(d,"concealed_frames",m.concealed_frames);jnum(d,"tx_frames",m.tx_frames);jnum(d,"tx_packets",m.tx_packets);jnum(d,"task_max_us",m.task_max_us);jnum(d,"poll_max_us",capture_poll_max_us);cJSON_AddItemToObject(d,"diagnostics",capture_diagnostics_json(&m));cJSON*uc=obj();jnum(uc,"volume_q8",m.control.volume_q8);jb(uc,"mute",m.control.mute);jnum(uc,"revision",m.control.revision);js(uc,"source",m.control.from_network?"network":"usb_host");cJSON_AddItemToObject(d,"usb_control",uc);jnum(d,"port",capture_port);js(d,"priority",capture.balanced?"balanced":"playback_first");return d;}
static cJSON* capture_formats_json(uint32_t mask){cJSON*a=cJSON_CreateArray();for(unsigned i=0;i<USB_MIC_FORMAT_COUNT;i++)if(mask&(1u<<i)){usb_mic_status_t m={.rate=usb_mic_formats[i].rate,.bits=usb_mic_formats[i].bits};cJSON_AddItemToArray(a,capture_format_json(&m));}return a;}
static cJSON* info_json(void){
 usb_audio_status_t u;usb_audio_get_status(&u);cJSON*d=obj();js(d,"device_id",device_hex);ju64(d,"boot_id",boot_id);js(d,"model",MODEL);js(d,"firmware",VERSION);js(d,"firmware_version",VERSION);ju64(d,"uptime_ms",now_ms());cJSON_AddItemToObject(d,"capture",capture_json());cJSON*ports=obj();jnum(ports,"control_tcp",CONTROL_PORT);jnum(ports,"playback_udp",audio_port);jnum(ports,"capture_udp",capture_port);cJSON_AddItemToObject(d,"ports",ports);char mac_text[18];snprintf(mac_text,sizeof(mac_text),"%02X:%02X:%02X:%02X:%02X:%02X",mac[0],mac[1],mac[2],mac[3],mac[4],mac[5]);js(d,"mac",mac_text);js(d,"usb_version","2.0");jnum(d,"uac_version",1);js(d,"usb_link_speed",u.mounted?"Full-Speed / 12 Mbit/s":"尚未握手");
 cJSON*n=obj();js(n,"mode",active.mode?"static":"dhcp");js(n,"address_source",ready?(active.mode?"static":"dhcp"):"none");js(n,"state",network_state);jip(n,"ip",net.ip);jip(n,"mask",net.sn);jip(n,"gateway",net.gw);jnum(n,"network_generation",generation);ju64(n,"lease_remaining",ready&&lease_seconds!=0xffffffffu?(lease_seconds>(now_ms()-lease_started)/1000?lease_seconds-(now_ms()-lease_started)/1000:0):lease_seconds);cJSON_AddItemToObject(d,"network",n);
 cJSON*v=obj();js(v,"state",usb_audio_get_state_name(u.state));ju64(v,"emulation_revision",u.revision);jnum(v,"generation",u.generation);jb(v,"mounted",u.mounted);jb(v,"streaming",u.streaming);jb(v,"suspended",u.suspended);cJSON_AddItemToObject(v,"format",format_json_for(u.sample_rate,u.sample_format));usb_control_notify_status_t ns;usb_control_notify_get_status(&ns);cJSON*notify=obj();jb(notify,"enabled",ns.enabled);jnum(notify,"pending",ns.pending);jnum(notify,"marked",ns.marked);jnum(notify,"scheduled",ns.scheduled);jnum(notify,"completed",ns.completed);jnum(notify,"acknowledged",ns.acknowledged);jnum(notify,"suppressed",ns.suppressed);cJSON_AddItemToObject(d,"control_notify",notify);jnum(v,"alternate_setting",u.alternate_setting);jb(v,"format_selected",u.alternate_setting!=0);jb(v,"hi_res_enabled",u.hi_res_enabled);jnum(v,"formats_mask",u.formats_mask);jb(v,"active_hid_consumer_compat",usb_descriptors_active_hid_compat());js(v,"active_device_profile",usb_profile_name(usb_descriptors_active_profile()));jb(v,"capture_interface_present",usb_profile_has_capture(usb_descriptors_active_profile()));js(v,"protocol","UAC1 / USB Full-Speed");char usb_serial[PICO_UNIQUE_BOARD_ID_SIZE_BYTES*2+1];pico_get_unique_board_id_string(usb_serial,sizeof(usb_serial));js(v,"serial",usb_serial);cJSON_AddItemToObject(d,"usb_control",usb_control_json(&u));cJSON_AddItemToObject(d,"internal_volume",internal_volume_json(&u));cJSON_AddItemToObject(d,"host_control",host_control_json());cJSON_AddItemToObject(d,"usb",v);js(d,"usb_emulation_state",usb_audio_get_state_name(u.state));ju64(d,"emulation_revision",u.revision);cJSON* em=obj();js(em,"state",usb_audio_get_state_name(u.state));ju64(em,"revision",u.revision);jb(em,"auto_start_pending",uab_boot_auto_start_pending());jb(em,"can_modify_usb_config",u.state==USB_AUDIO_STOPPED&&!transaction.pending);js(em,"block_reason",transaction.pending?"USB_CONFIG_BUSY":u.state==USB_AUDIO_STOPPED?"":"USB_EMULATION_NOT_STOPPED");cJSON_AddItemToObject(d,"usb_emulation",em);return d;
}
static cJSON* host_control_keys_json(void){cJSON*a=cJSON_CreateArray();for(unsigned i=0;usb_host_control_key_name(i);i++)cJSON_AddItemToArray(a,cJSON_CreateString(usb_host_control_key_name(i)));return a;}
static cJSON* capabilities_json(void){cJSON*d=obj();jb(d,"firmware_version_query",true);jb(d,"indicator_configuration_supported",true);cJSON*led_modes=cJSON_CreateArray();for(unsigned i=0;i<UAB_INDICATOR_MODE_COUNT;i++)cJSON_AddItemToArray(led_modes,cJSON_CreateString(uab_indicator_mode_name(i)));cJSON_AddItemToObject(d,"indicator_modes",led_modes);jb(d,"clock_observation_v1",true);js(d,"network_backend","w5500_hardware");js(d,"discovery_scope","udp_broadcast_domain");js(d,"usb_speed","full_speed");jnum(d,"uac_version",1);jnum(d,"protocol",1);jnum(d,"max_udp_bytes",1200);jnum(d,"max_endpoint_bytes",772);jb(d,"format_selection_supported",true);jb(d,"output_format_filter_supported",true);jb(d,"internal_volume_control_supported",true);jb(d,"host_volume_keys_supported",true);cJSON_AddItemToObject(d,"host_control_keys",host_control_keys_json());js(d,"host_control_result","accepted_not_player_state");jb(d,"player_state_supported",false);cJSON_AddItemToObject(d,"supported_formats",declared_formats(USB_FORMAT_ALL));jb(d,"hi_res_supported",true);cJSON*codecs=cJSON_CreateArray();cJSON_AddItemToArray(codecs,cJSON_CreateString("pcm"));cJSON_AddItemToArray(codecs,cJSON_CreateString("ima_adpcm"));cJSON_AddItemToObject(d,"audio_codecs",codecs);jb(d,"fec_supported",true);js(d,"fec_mode","xor4");jb(d,"capture_supported",usb_profile_has_capture(committed.device_profile));jb(d,"capture_volume_supported",true);jb(d,"hid_consumer_compat_supported",true);cJSON*profiles=cJSON_CreateArray();for(unsigned i=0;i<USB_PROFILE_COUNT;i++)cJSON_AddItemToArray(profiles,cJSON_CreateString(usb_profile_name(i)));cJSON_AddItemToObject(d,"usb_device_profiles",profiles);jb(d,"runtime_status_supported",true);jnum(d,"capture_port",capture_port);cJSON_AddItemToObject(d,"capture_formats",capture_formats_json(USB_MIC_FORMAT_ALL));jb(d,"secure_maintenance",false);jb(d,"udp_broadcast_recovery",true);js(d,"network_config_write_policy","stopped_only");jb(d,"runtime_uac_switch",false);jb(d,"network_configuration",true);jb(d,"usb_configuration",true);jb(d,"usb_emulation_control",true);js(d,"usb_config_write_policy","stopped_only");js(d,"usb_auto_start_policy","power_on");jb(d,"stats_subscription",true);jb(d,"port_configuration",true);jb(d,"udp_port_negotiation_v1",true);jb(d,"factory_reset",false);cJSON*a=cJSON_CreateArray();cJSON_AddItemToArray(a,cJSON_CreateString("dhcp"));cJSON_AddItemToArray(a,cJSON_CreateString("static"));cJSON_AddItemToObject(d,"address_modes",a);cJSON_AddItemToObject(d,"formats",declared_formats(config_formats(&committed)));a=cJSON_CreateArray();cJSON_AddItemToArray(a,cJSON_CreateString("playback"));if(usb_profile_has_capture(committed.device_profile))cJSON_AddItemToArray(a,cJSON_CreateString("capture"));cJSON_AddItemToObject(d,"directions",a);return d;}
static cJSON* config_json(const uab_config_t*c){cJSON*d=obj(),*u=obj(),*n=obj();js(u,"indicator_mode",uab_indicator_mode_name(c->indicator_mode));jb(u,"hid_consumer_compat",c->hid_consumer_compat);js(u,"device_profile",usb_profile_name(c->device_profile));js(u,"product_name",c->product);js(u,"manufacturer_name",c->manufacturer);js(u,"playback_name",c->playback);jb(u,"hi_res_enabled",c->hi_res_enabled);jnum(u,"formats_mask",config_formats(c));jnum(u,"capture_formats_mask",c->capture_formats_mask?c->capture_formats_mask:USB_MIC_DEFAULT_MASK);cJSON_AddItemToObject(u,"capture_declared_formats",capture_formats_json(c->capture_formats_mask?c->capture_formats_mask:USB_MIC_DEFAULT_MASK));cJSON_AddItemToObject(u,"format",format_json_for(48000,1));cJSON_AddItemToObject(u,"declared_formats",declared_formats(config_formats(c)));cJSON_AddItemToObject(d,"usb",u);js(n,"mode",c->mode?"static":"dhcp");jip(n,"ip",c->ip);jip(n,"mask",c->mask);jip(n,"gateway",c->gateway);jip(n,"dns",c->dns);unsigned prefix=0;for(int i=0;i<4;i++)for(int b=0;b<8;b++)if(c->mask[i]&(1u<<b))prefix++;jnum(n,"prefix_length",prefix);cJSON_AddItemToObject(d,"network",n);ju64(d,"config_revision",c->revision);ju64(d,"revision",c->revision);return d;}
static bool parse_ip(const char*s,uint8_t*out){unsigned a,b,c,d;char trailing;if(!s||sscanf(s,"%u.%u.%u.%u%c",&a,&b,&c,&d,&trailing)!=4||a>255||b>255||c>255||d>255)return false;out[0]=a;out[1]=b;out[2]=c;out[3]=d;return true;}
static bool usable_ip(uint32_t x){unsigned first=x>>24;return first&&first!=127&&first<224&&(x>>16)!=0xa9fe;}
static const char* validate_candidate(cJSON*request,cJSON*args,uab_config_t*out,bool*usb_change,bool*net_change){
 if(uint64_arg(request,"expected_revision",UINT64_MAX)!=config_revision&&uint64_arg(args,"expected_revision",UINT64_MAX)!=config_revision)return "REVISION_CONFLICT";
 *out=committed;*usb_change=*net_change=false;cJSON*c=cJSON_GetObjectItemCaseSensitive(args,"candidate");if(!cJSON_IsObject(c)||!c->child)return "INVALID_FIELD";
 for(cJSON*section=c->child;section;section=section->next){if(!cJSON_IsObject(section))return "INVALID_FIELD";
  if(!strcmp(section->string,"usb")){*usb_change=true;usb_audio_status_t u;usb_audio_get_status(&u);if(u.state!=USB_AUDIO_STOPPED)return "USB_EMULATION_NOT_STOPPED";if(uint64_arg(args,"expected_emulation_revision",UINT64_MAX)!=u.revision)return "REVISION_CONFLICT";
   for(cJSON*f=section->child;f;f=f->next){if(!strcmp(f->string,"indicator_mode")){if(!cJSON_IsString(f)||!uab_indicator_mode_parse(f->valuestring,&out->indicator_mode))return "INVALID_FIELD";continue;}if(!strcmp(f->string,"hid_consumer_compat")){if(!cJSON_IsBool(f))return "INVALID_FIELD";out->hid_consumer_compat=cJSON_IsTrue(f);continue;}if(!strcmp(f->string,"device_profile")){if(!cJSON_IsString(f)||!usb_profile_parse(f->valuestring,&out->device_profile))return "INVALID_FIELD";continue;}if(!strcmp(f->string,"capture_formats_mask")){int m=number(section,"capture_formats_mask",0);if(!usb_mic_mask_valid((uint32_t)m))return "INVALID_FIELD";out->capture_formats_mask=(uint32_t)m;continue;}if(!strcmp(f->string,"formats_mask")){int mask=number(section,"formats_mask",0);if(mask<1||!usb_formats_valid((uint32_t)mask))return "INVALID_FIELD";out->formats_mask=(uint32_t)mask;out->hi_res_enabled=(mask&~1u)!=0;continue;}if(!strcmp(f->string,"hi_res_enabled")){if(!cJSON_IsBool(f))return "INVALID_FIELD";out->hi_res_enabled=cJSON_IsTrue(f);if(!cJSON_GetObjectItemCaseSensitive(section,"formats_mask"))out->formats_mask=usb_formats_legacy(out->hi_res_enabled);continue;}char*dst=!strcmp(f->string,"product_name")?out->product:!strcmp(f->string,"manufacturer_name")?out->manufacturer:!strcmp(f->string,"playback_name")?out->playback:NULL;if(!dst||!cJSON_IsString(f)||!usb_audio_validate_name(f->valuestring))return "INVALID_FIELD";strcpy(dst,f->valuestring);}
  }else if(!strcmp(section->string,"network")){*net_change=true;for(cJSON*f=section->child;f;f=f->next){if(!strcmp(f->string,"mode")){if(!cJSON_IsString(f))return "INVALID_FIELD";if(!strcmp(f->valuestring,"dhcp"))out->mode=0;else if(!strcmp(f->valuestring,"static"))out->mode=1;else return "INVALID_FIELD";}else if(!strcmp(f->string,"prefix_length")){int prefix=number(section,"prefix_length",0);if(prefix<1||prefix>30)return "INVALID_FIELD";w32(out->mask,0xffffffffu<<(32-prefix));}else{uint8_t*dst=!strcmp(f->string,"ip")?out->ip:!strcmp(f->string,"mask")?out->mask:!strcmp(f->string,"gateway")?out->gateway:!strcmp(f->string,"dns")?out->dns:NULL;if(!dst||!cJSON_IsString(f)||!parse_ip(f->valuestring,dst))return "INVALID_FIELD";}}}
  else return "INVALID_FIELD";
 }
 if(out->mode){uint32_t ip=r32(out->ip),mask=r32(out->mask),inv=~mask,gw=r32(out->gateway),dns=r32(out->dns);if(!usable_ip(ip)||!mask||inv<3||(inv&(inv+1))||!(ip&inv)||(ip&inv)==inv||(gw&&(!usable_ip(gw)||(gw&mask)!=(ip&mask)||!(gw&inv)||(gw&inv)==inv))||(dns&&!usable_ip(dns)))return "INVALID_FIELD";}
 if(*usb_change&&*net_change)return "INVALID_FIELD";
 if(*usb_change){static usb_descriptor_set_t staging;usb_descriptor_options_t options={config_formats(out),out->capture_formats_mask?out->capture_formats_mask:USB_MIC_DEFAULT_MASK,out->device_profile,out->hid_consumer_compat};if(!usb_descriptors_build(&options,&staging))return "INVALID_FIELD";}
 if(*net_change){usb_audio_status_t u;usb_audio_get_status(&u);if(u.state!=USB_AUDIO_STOPPED)return "USB_EMULATION_NOT_STOPPED";}
 return NULL;
}
static void rollback_transaction(void){if(!transaction.pending)return;if(transaction.applied&&transaction.network_changed){active=committed;start_address_mode();}transaction.pending=false;transaction.apply_at=0;transaction.final_stage="ROLLED_BACK";puts("CONFIG ROLLED_BACK");}
static void transaction_poll(void){if(!transaction.pending)return;if(now_ms()>transaction.deadline||(transaction.applied&&address_conflict)){rollback_transaction();return;}if(transaction.apply_at&&now_ms()>=transaction.apply_at){transaction.apply_at=0;active=transaction.candidate;start_address_mode();}}

/* Driver-free UDP broadcast discovery and network transactions. MACRAW is ARP-only. */
typedef struct {bool used;uint8_t peer[4],nonce[16];uint64_t expires,last_request;} challenge_t;
typedef struct {bool used;uint8_t peer[4],data[512];uint16_t port,len;uint64_t due;} discovery_reply_t;
static challenge_t challenges[4];static discovery_reply_t replies[4];
static struct {uint8_t peer[4],digest[32],response[512];uint64_t request_id,expires;uint16_t len;} discovery_cache[16];
static uint32_t discovery_limited;
static bool same_peer(const uint8_t*a,const uint8_t*b){return !memcmp(a,b,4);}
static uint8_t state_code(void){if(!strcmp(network_state,"LINK_DOWN"))return 0;if(!strcmp(network_state,"DHCP_ACQUIRING"))return 1;if(!strcmp(network_state,"DHCP_WAITING"))return 2;if(!strcmp(network_state,"DHCP_BOUND"))return 3;if(!strcmp(network_state,"STATIC_PROBING"))return 4;if(!strcmp(network_state,"STATIC_ACTIVE"))return 5;if(!strcmp(network_state,"ADDRESS_CONFLICT"))return 6;return 7;}
static void discovery_replies(void){
 static const uint8_t broadcast[4]={255,255,255,255};
 for(unsigned i=0;i<4;i++){discovery_reply_t*r=&replies[i];if(!r->used||now_ms()<r->due)continue;
  if(now_ms()>r->due+1000){r->used=false;discovery_limited++;continue;}
  if(linked&&udp_send(4,r->data,r->len,broadcast,r->port))r->used=false;break;
 }
}
static void discovery_receive(const uint8_t*m,size_t n,const uint8_t*peer,uint16_t port){
 uint8_t zero[16]={0};if(n<56||n>1024||memcmp(m,"UABD",4)||m[4]!=1||r16(m+6)!=56||r16(m+8)!=n||r16(m+10)||r16(m+54)){invalid_packets++;return;}
 unsigned type=m[5];bool write=type>=3&&type<=8;if((!write&&type!=1)||r16(m+52)!=0||(write?memcmp(m+20,device_id,16):(memcmp(m+20,device_id,16)&&memcmp(m+20,zero,16)))){invalid_packets++;return;}
 const uint8_t*fields[8]={0};unsigned lengths[8]={0};uint16_t previous=0;for(size_t p=56;p<n;){if(p+4>n){invalid_packets++;return;}uint16_t t=r16(m+p),len=r16(m+p+2);p+=4;if(t<=previous||p+len>n){invalid_packets++;return;}previous=t;if(t>=0x8010&&t<=0x8017){fields[t-0x8010]=m+p;lengths[t-0x8010]=len;}else if(t==0x0010){if(len!=2||r16(m+p)<1024){invalid_packets++;return;}port=r16(m+p);}else if(t&0x8000){invalid_packets++;return;}p+=len;}
 static const unsigned sizes[8]={8,16,1,4,1,4,2,8};for(unsigned i=0;i<8;i++)if(fields[i]&&lengths[i]!=sizes[i]){invalid_packets++;return;}
 uint8_t digest[32];
 discovery_reply_t*out=NULL;for(unsigned i=0;i<4;i++)if(!replies[i].used){out=&replies[i];break;}if(!out){discovery_limited++;return;}
 uint64_t request_id=r64(m+12);uab_sha256(m,n,digest);int free_cache=-1;bool reused_id=false;
 if(write){for(unsigned i=0;i<16;i++){if(discovery_cache[i].expires<=now_ms()){if(free_cache<0)free_cache=i;continue;}if(same_peer(discovery_cache[i].peer,peer)&&discovery_cache[i].request_id==request_id){if(!uab_equal(digest,discovery_cache[i].digest,32)){reused_id=true;break;}memset(out,0,sizeof(*out));out->used=true;memcpy(out->peer,peer,4);out->port=port;out->len=discovery_cache[i].len;memcpy(out->data,discovery_cache[i].response,out->len);out->due=now_ms();return;}}}
 challenge_t*challenge=NULL,*empty=NULL;for(unsigned i=0;i<4;i++){if(challenges[i].used&&same_peer(challenges[i].peer,peer)){challenge=&challenges[i];break;}if(challenges[i].expires<=now_ms())empty=&challenges[i];}
 if(!challenge){if(!empty||write){discovery_limited++;return;}challenge=empty;memset(challenge,0,sizeof(*challenge));memcpy(challenge->peer,peer,4);challenge->used=true;}
 if(!write&&challenge->last_request&&now_ms()-challenge->last_request<100){discovery_limited++;return;}
 if(write&&(!fields[0]||!fields[7]||r64(fields[7])!=boot_id||(!reused_id&&(challenge->expires<=now_ms()||!uab_equal(challenge->nonce,m+36,16))))){invalid_packets++;return;}
 uint16_t code=0;const char*reason=write?"OK":"INFO";
 if(write){
  if(reused_id){code=18;reason="REQUEST_ID_REUSED";}
  else if(r64(fields[0])!=config_revision){code=8;reason="REVISION_CONFLICT";}
  else if(free_cache<0){code=7;reason="RESOURCE_LIMIT";}
  else if(owner>=0){code=9;reason="BUSY_OWNER";}
  else if(type==3){
   if(transaction.pending){code=26;reason="USB_CONFIG_BUSY";}
   else if(!fields[2]||(fields[2][0]!=1&&fields[2][0]!=2)){code=5;reason="INVALID_FIELD";}
   else{uab_config_t candidate=committed;candidate.mode=fields[2][0]-1;unsigned timeout=candidate.mode?30:120;if(fields[6])timeout=r16(fields[6]);bool valid=timeout>=30&&timeout<=120;
    if(candidate.mode){if(!fields[3]||!fields[4]||fields[4][0]<1||fields[4][0]>30)valid=false;else{memcpy(candidate.ip,fields[3],4);w32(candidate.mask,0xffffffffu<<(32-fields[4][0]));if(fields[5])memcpy(candidate.gateway,fields[5],4);uint32_t ip=r32(candidate.ip),mask=r32(candidate.mask),inv=~mask,gw=r32(candidate.gateway);valid=valid&&usable_ip(ip)&&(ip&inv)&&(ip&inv)!=inv&&(!gw||(usable_ip(gw)&&(gw&mask)==(ip&mask)&&(gw&inv)&&(gw&inv)!=inv));}}
    if(!valid){code=5;reason="INVALID_FIELD";}else{memset(&transaction,0,sizeof(transaction));transaction.pending=true;transaction.network_changed=true;transaction.candidate=candidate;transaction.deadline=now_ms()+timeout*1000;uint8_t id[16];random_bytes(id,16);for(int i=0;i<16;i++)sprintf(transaction.id+i*2,"%02x",id[i]);reason="PREPARED";}
   }
  }else if(type>=4&&type<=6){
   char id[33]={0};if(fields[1])for(int i=0;i<16;i++)sprintf(id+i*2,"%02x",fields[1][i]);
   if(!transaction.pending||!transaction.network_changed||transaction.usb_changed||!fields[1]||strcmp(id,transaction.id)){code=5;reason="TRANSACTION_NOT_FOUND";}
   else if(type==6){rollback_transaction();reason="ROLLED_BACK";}
   else if(type==4){transaction.applied=true;transaction.apply_at=now_ms()+500;transaction.deadline=now_ms()+(transaction.candidate.mode?30000:120000);stream_stop();usb_audio_set_enabled(false);reason="RECONNECT_REQUIRED_USB_STOPPED";}
   else if(!transaction.applied||!ready||transaction.apply_at){code=20;reason="NETWORK_NOT_READY";}
   else{usb_audio_status_t u;usb_audio_get_status(&u);if(u.state!=USB_AUDIO_STOPPED){code=24;reason="USB_EMULATION_NOT_STOPPED";}else{transaction.candidate.revision=config_revision+1;if(!uab_config_save(&transaction.candidate)){code=16;reason="STORAGE_ERROR";rollback_transaction();}else{committed=transaction.candidate;active=committed;config_revision=committed.revision;transaction.pending=false;transaction.final_stage="COMMITTED";reason="COMMITTED";}}}
  }else{code=17;reason="NOT_IMPLEMENTED";}
 }
 challenge->last_request=now_ms();random_bytes(challenge->nonce,16);challenge->expires=now_ms()+30000;
 memset(out,0,sizeof(*out));out->used=true;memcpy(out->peer,peer,4);out->port=port;out->due=now_ms()+(write?0:get_rand_32()%251);uint8_t*p=out->data;memcpy(p,"UABD",4);p[4]=1;p[5]=write?7:2;w16(p+6,56);w16(p+10,1);w64(p+12,request_id);memcpy(p+20,device_id,16);memcpy(p+36,challenge->nonce,16);size_t at=56;
 at+=tlv(p+at,1,MODEL,strlen(MODEL));at+=tlv(p+at,2,VERSION,strlen(VERSION));at+=tlv(p+at,3,net.ip,4);uint8_t prefix=0;for(int i=0;i<4;i++)for(int b=0;b<8;b++)if(net.sn[i]&(1<<b))prefix++;at+=tlv(p+at,4,&prefix,1);at+=tlv(p+at,5,net.gw,4);uint8_t source=ready?(active.mode?3:1):0;at+=tlv(p+at,6,&source,1);uint8_t val[16];w16(val,CONTROL_PORT);at+=tlv(p+at,7,val,2);w64(val,boot_id);at+=tlv(p+at,8,val,8);w64(val,config_revision);at+=tlv(p+at,9,val,8);at+=tlv(p+at,10,mac,6);w16(val,code);at+=tlv(p+at,11,val,2);at+=tlv(p+at,12,reason,strlen(reason));val[0]=state_code();at+=tlv(p+at,13,val,1);
 val[0]=active.mode?2:1;at+=tlv(p+at,14,val,1);
 val[0]=transaction.pending?(transaction.applied?2:1):transaction.final_stage?(!strcmp(transaction.final_stage,"COMMITTED")?3:4):0;at+=tlv(p+at,15,val,1);
 if(transaction.id[0]){for(int i=0;i<16;i++){char hex[3]={transaction.id[i*2],transaction.id[i*2+1],0};val[i]=(uint8_t)strtoul(hex,NULL,16);}at+=tlv(p+at,0x8011,val,16);}
 w16(p+8,at);out->len=at;
 if(write&&!reused_id&&free_cache>=0){memcpy(discovery_cache[free_cache].peer,peer,4);memcpy(discovery_cache[free_cache].digest,digest,32);discovery_cache[free_cache].request_id=request_id;discovery_cache[free_cache].expires=now_ms()+60000;discovery_cache[free_cache].len=at;memcpy(discovery_cache[free_cache].response,p,at);}
}
static void execute(session_t*s){
 uint32_t id=r32(s->rx+12),n=r32(s->rx+8);uint64_t seq=r64(s->rx+16);bool tagged=r16(s->rx+6)==1;
 for(size_t i=32;i<32+n;i++)if(s->rx[i]=='\\'&&i+1<32+n){i++;if(s->rx[i]=='u'&&i+4<32+n&&!memcmp(s->rx+i+1,"0000",4)){close_session(s);return;}}
 if(!utf8_valid(s->rx+32,n)){close_session(s);return;}s->rx[32+n]=0;json_used=0;
 const char*end=NULL;cJSON*request=cJSON_ParseWithLengthOpts((char*)s->rx+32,n+1,&end,1);if(!cJSON_IsObject(request)||!valid_json(request,0)){close_session(s);return;}
 const char*op=str(request,"op");cJSON*args=cJSON_GetObjectItemCaseSensitive(request,"args");if(!op||!cJSON_IsObject(args)){close_session(s);return;}for(cJSON*f=request->child;f;f=f->next)if(strcmp(f->string,"op")&&strcmp(f->string,"args")&&strcmp(f->string,"expected_revision")){close_session(s);return;}
 if(!s->connected){
  const char*mode=str(args,"connection_mode");if(strcmp(op,"HELLO")||tagged||seq||number(args,"protocol_min",1)>1||number(args,"protocol_max",1)<1||r64(s->rx+24)||!mode||strcmp(mode,"open")){close_session(s);return;}
  s->connected=true;s->last_id=id;s->rx_seq=s->tx_seq=0;cJSON*d=obj();js(d,"device_id",device_hex);ju64(d,"boot_id",boot_id);jnum(d,"protocol",1);js(d,"connection_mode","open");js(d,"firmware_version",VERSION);jb(d,"owner",take_owner(s));
  response(s,id,true,"OK","Connected",d,0);return;
 }
 bool read_only=!strcmp(op,"GET_INFO")||!strcmp(op,"GET_FIRMWARE_VERSION")||!strcmp(op,"GET_CAPABILITIES")||!strcmp(op,"GET_CONFIG")||!strcmp(op,"GET_STATS")||!strcmp(op,"GET_STATUS")||!strcmp(op,"GET_OPERATION");
 uint8_t digest[32];uab_sha256(s->rx+32,n,digest);if(ready)audio_poll();int cache_slot=-1;
 for(int i=0;i<32;i++){if(s->cache[i].expires<=now_ms()){if(cache_slot<0)cache_slot=i;continue;}if(s->cache[i].id==id){if(!uab_equal(digest,s->cache[i].digest,32)){response(s,id,false,"REQUEST_ID_REUSED","Request id has different contents",NULL,++s->tx_seq);return;}
   if(s->cache[i].size==UINT16_MAX){const uint8_t*p=s->cache[i].json;cJSON*d=obj();jnum(d,"stream_id",r32(p));jnum(d,"epoch",r32(p+4));ju64(d,"session_token",r64(p+8));jnum(d,"source_port",r16(p+16));jnum(d,"packet_frames",(r32(p+19)+999)/1000);if(p[27]){usb_mic_status_t m={.rate=r32(p+19),.bits=p[23]==2?24:16};cJSON_AddItemToObject(d,"format",capture_format_json(&m));jb(d,"host_active",p[28]);response(s,id,true,"OK","OK",d,++s->tx_seq);return;}cJSON_AddItemToObject(d,"format",format_json_for(r32(p+19),p[23]));jb(d,"waiting_usb",p[18]);jb(d,"follow_usb",p[24]);js(d,"fec_mode",p[25]?"xor4":"none");js(d,"codec",p[26]?"ima_adpcm":"pcm");response(s,id,true,"OK","OK",d,++s->tx_seq);return;}
   if(!s->cache[i].size){response(s,id,false,"RESOURCE_LIMIT","Cached result unavailable; query operation",NULL,++s->tx_seq);return;}memset(s->tx,0,32);memcpy(s->tx,"UABC",4);s->tx[4]=1;s->tx[5]=2;w32(s->tx+8,s->cache[i].size);w32(s->tx+12,id);w64(s->tx+16,++s->tx_seq);w64(s->tx+24,boot_id);memcpy(s->tx+32,s->cache[i].json,s->cache[i].size);s->tx_len=32+s->cache[i].size;s->tx_at=0;s->tx_deadline=now_ms()+3000;return;}}
 if(id<=s->last_id){response(s,id,false,"REQUEST_ID_REUSED","Use a new monotonically increasing request id",NULL,++s->tx_seq);return;}s->last_id=id;
 if(!read_only&&!take_owner(s)){response(s,id,false,"BUSY_OWNER","Another client owns control",NULL,++s->tx_seq);return;}
  // Volume is a latest-value runtime control, not a persistent transaction.
  // Continuous dragging must not exhaust the 32-entry / 60-second result cache.
  // Ownership, increasing request IDs and sequence validation still apply.
  if(!read_only&&strcmp(op,"SET_CLOCK_OBSERVATION")&&strcmp(op,"STREAM_KEEPALIVE")&&strcmp(op,"CAPTURE_KEEPALIVE")&&strcmp(op,"SET_INTERNAL_VOLUME")&&strcmp(op,"USB_SET_CAPTURE_VOLUME")&&strcmp(op,"USB_HOST_KEY")){if(cache_slot<0){response(s,id,false,"RESOURCE_LIMIT","Retry with a new id after cache expires",NULL,++s->tx_seq);return;}s->cache_slot=cache_slot;s->cache[cache_slot].id=id;s->cache[cache_slot].expires=now_ms()+60000;s->cache[cache_slot].size=0;memcpy(s->cache[cache_slot].digest,digest,32);}
 cJSON*d=NULL;const char*code="OK",*message="OK";bool ok=true;usb_audio_status_t u;usb_audio_get_status(&u);
 if(!strcmp(op,"GET_INFO"))d=info_json();
 else if(!strcmp(op,"GET_STATUS")){d=obj();usb_audio_status_t q;usb_audio_get_status(&q);cJSON*u=obj();js(u,"state",usb_audio_get_state_name(q.state));jb(u,"streaming",q.streaming);jb(u,"mounted",q.mounted);jb(u,"suspended",q.suspended);jb(u,"active_hid_consumer_compat",usb_descriptors_active_hid_compat());js(u,"active_device_profile",usb_profile_name(usb_descriptors_active_profile()));jb(u,"capture_interface_present",usb_profile_has_capture(usb_descriptors_active_profile()));js(u,"link_speed",q.mounted?"Full-Speed / 12 Mbit/s":"尚未握手");jb(u,"format_selected",q.alternate_setting!=0);jnum(u,"alternate_setting",q.alternate_setting);jnum(u,"formats_mask",q.formats_mask);cJSON_AddItemToObject(u,"format",format_json_for(q.sample_rate,q.sample_format));cJSON_AddItemToObject(d,"usb",u);cJSON*em=obj();js(em,"state",usb_audio_get_state_name(q.state));ju64(em,"revision",q.revision);cJSON_AddItemToObject(d,"usb_emulation",em);cJSON_AddItemToObject(d,"usb_control",usb_control_json(&q));cJSON_AddItemToObject(d,"internal_volume",internal_volume_json(&q));cJSON_AddItemToObject(d,"host_control",host_control_json());cJSON_AddItemToObject(d,"capture",capture_json());}
 else if(!strcmp(op,"GET_FIRMWARE_VERSION"))d=firmware_json();
 else if(!strcmp(op,"GET_CAPABILITIES"))d=capabilities_json();
 else if(!strcmp(op,"GET_CONFIG")){d=config_json(&committed);if(transaction.pending)cJSON_AddItemToObject(d,"pending",config_json(&transaction.candidate));}
 else if(!strcmp(op,"VALIDATE_CONFIG")||!strcmp(op,"PREPARE_CONFIG")){
  bool usb_change,net_change;uab_config_t candidate;const char*error=validate_candidate(request,args,&candidate,&usb_change,&net_change);
  if(transaction.pending){ok=false;code="USB_CONFIG_BUSY";message="A configuration transaction is pending";}else if(error){ok=false;code=error;message="Candidate rejected";}else if(!strcmp(op,"VALIDATE_CONFIG")){d=obj();jb(d,"valid",true);js(d,"apply_mode",net_change?"network_reconnect":"next_usb_start");}
  else{memset(&transaction,0,sizeof(transaction));transaction.pending=true;transaction.usb_changed=usb_change;transaction.network_changed=net_change;transaction.candidate=candidate;transaction.usb_revision=u.revision;transaction.deadline=now_ms()+(net_change&&!candidate.mode?120000:30000);uint8_t random[16];random_bytes(random,16);for(int i=0;i<16;i++)sprintf(transaction.id+i*2,"%02x",random[i]);d=obj();js(d,"transaction_id",transaction.id);jnum(d,"confirm_timeout_seconds",net_change&&!candidate.mode?120:30);}
 }
 else if(!strcmp(op,"APPLY_CONFIG")||!strcmp(op,"CONFIRM_CONFIG")||!strcmp(op,"ROLLBACK_CONFIG")){
  const char*id_arg=str(args,"transaction_id");if(!transaction.pending||!id_arg||strcmp(id_arg,transaction.id)){ok=false;code="TRANSACTION_NOT_FOUND";message="No matching transaction";}
  else if(!strcmp(op,"ROLLBACK_CONFIG")){rollback_transaction();}
  else if(transaction.usb_changed&&(u.state!=USB_AUDIO_STOPPED||uint64_arg(args,"expected_emulation_revision",UINT64_MAX)!=u.revision||u.revision!=transaction.usb_revision)){ok=false;code="REVISION_CONFLICT";message="USB state changed";}
  else if(!strcmp(op,"APPLY_CONFIG")){transaction.applied=true;transaction.deadline=now_ms()+(transaction.network_changed&&!transaction.candidate.mode?120000:30000);if(transaction.network_changed){stream_stop();transaction.apply_at=now_ms()+500;}d=obj();js(d,"transaction_id",transaction.id);js(d,"stage",transaction.network_changed?"RECONNECT_REQUIRED":"APPLIED");}
  else if(!transaction.applied||(transaction.network_changed&&(!ready||transaction.apply_at))){ok=false;code="NETWORK_NOT_READY";message="Wait for candidate address before confirmation";}
  else if(u.state!=USB_AUDIO_STOPPED){ok=false;code="USB_EMULATION_NOT_STOPPED";message="Stop USB emulation before flash commit";}
  else{transaction.candidate.revision=config_revision+1;if(!uab_config_save(&transaction.candidate)){ok=false;code="STORAGE_ERROR";message="Configuration was not committed";rollback_transaction();}else{committed=transaction.candidate;active=committed;config_revision=committed.revision;usb_audio_set_names(committed.product,committed.manufacturer,committed.playback);usb_descriptor_options_t options={config_formats(&committed),committed.capture_formats_mask?committed.capture_formats_mask:USB_MIC_DEFAULT_MASK,committed.device_profile,committed.hid_consumer_compat};usb_audio_apply_descriptor_options(&options);uab_indicator_set_mode(committed.indicator_mode);transaction.pending=false;transaction.final_stage="COMMITTED";d=obj();js(d,"stage","COMMITTED");ju64(d,"config_revision",config_revision);}}
 }
 else if(!strcmp(op,"GET_STATS")){if(ready)audio_poll();d=obj();cJSON_AddItemToObject(d,"capture",capture_json());cJSON_AddItemToObject(d,"clock_observation",clock_observation_json());jnum(d,"audio_packets_sent",audio_sent);jnum(d,"audio_packets_dropped",audio_dropped);jnum(d,"audio_busy_retries",audio_busy_retries);jnum(d,"audio_pending_expired",audio_pending_expired);jnum(d,"invalid_packets",invalid_packets);jnum(d,"dhcp_attempts",dhcp_attempts);jnum(d,"network_generation",generation);jnum(d,"poll_max_us",poll_max_us);jnum(d,"stream_poll_max_us",stream_poll_max_us);jnum(d,"stream_control_max_us",stream_control_max_us);jnum(d,"stream_events_max_us",stream_events_max_us);jnum(d,"stream_raw_max_us",stream_raw_max_us);jnum(d,"usb_rx_bytes",u.rx_bytes);jnum(d,"usb_queue_drops",u.queue_drops);jnum(d,"usb_expired_frames",u.expired_frames);jnum(d,"usb_obsolete_frames",u.obsolete_frames);jnum(d,"audio_send_completions",udp_completed[1]);jnum(d,"audio_send_timeouts",udp_timeouts[1]);jnum(d,"spi_actual_hz",spi_actual_hz);js(d,"audio_codec",stream.codec?"ima_adpcm":"pcm");jnum(d,"audio_encode_max_us",audio_encode_max_us);jnum(d,"audio_pcm_bytes",audio_pcm_bytes);jnum(d,"audio_wire_bytes",audio_wire_bytes);jnum(d,"fec_packets_sent",fec_packets_sent);jnum(d,"fec_packets_expired",fec_packets_expired);jnum(d,"udp_software_timeouts",udp_software_timeouts);jnum(d,"usb_irq_section_max_us",u.irq_section_max_us);jnum(d,"usb_unity_gain_blocks",u.unity_gain_blocks);jnum(d,"usb_muted_blocks",u.muted_blocks);jnum(d,"usb_scaled_blocks",u.scaled_blocks);jnum(d,"usb_fifo_phase_waits",u.fifo_phase_waits);jnum(d,"usb_fifo_phase_wait_max_us",u.fifo_phase_wait_max_us);jnum(d,"usb_queue_peak_blocks",u.queue_peak_blocks);jnum(d,"usb_block_age_max_us",u.block_age_max_us);jnum(d,"usb_feedback_q16",u.feedback_q16);jnum(d,"usb_feedback_min_q16",u.feedback_min_q16==UINT32_MAX?0:u.feedback_min_q16);jnum(d,"usb_feedback_max_q16",u.feedback_max_q16);jnum(d,"usb_feedback_average_bytes",u.feedback_average_bytes);jnum(d,"usb_feedback_threshold_bytes",u.feedback_threshold_bytes);cJSON*hist=cJSON_CreateArray();for(unsigned bin=0;bin<16;bin++)cJSON_AddItemToArray(hist,cJSON_CreateNumber(u.fifo_generation_histogram[bin]));cJSON_AddItemToObject(d,"usb_fifo_generation_histogram",hist);jnum(d,"usb_underruns",u.underruns);jnum(d,"usb_generation",u.generation);jnum(d,"usb_consumed_frames",u.consumed_frames);jnum(d,"usb_resyncs",u.resyncs);jnum(d,"usb_peak_fifo",u.peak_fifo);jnum(d,"usb_max_service_us",u.max_service_us);jnum(d,"usb_max_stream_service_us",u.max_stream_service_us);jnum(d,"usb_fifo_bytes",u.fifo_bytes);jnum(d,"usb_control_revision",u.control_revision);cJSON*fd=obj();jnum(fd,"rx_max_gap_us",u.rx_max_gap_us);jnum(fd,"rx_last_packet_bytes",u.rx_last_packet_bytes);jnum(fd,"min_consume_fifo_bytes",u.min_consume_fifo_bytes==UINT32_MAX?0:u.min_consume_fifo_bytes);jnum(fd,"last_underrun_available_bytes",u.last_underrun_available_bytes);jnum(fd,"last_underrun_read_bytes",u.last_underrun_read_bytes);jnum(fd,"last_underrun_wanted_bytes",u.last_underrun_wanted_bytes);jnum(fd,"last_underrun_rx_gap_us",u.last_underrun_rx_gap_us);jnum(fd,"last_underrun_service_gap_us",u.last_underrun_service_gap_us);jnum(fd,"last_underrun_at_us",u.last_underrun_at_us);cJSON_AddItemToObject(d,"usb_fifo_diagnostics",fd);uab_watchdog_status_t w;uab_watchdog_get_status(&w);jnum(d,"core1_max_gap_us",w.core1_max_gap_us);jnum(d,"watchdog_feed_count",w.feed_count);jb(d,"watchdog_timeout_reboot",w.timeout_reboot);jb(d,"stream_running",stream.running);cJSON_AddItemToObject(d,"usb_control",usb_control_json(&u));cJSON_AddItemToObject(d,"internal_volume",internal_volume_json(&u));cJSON_AddItemToObject(d,"host_control",host_control_json());cJSON_AddItemToObject(d,"usb_format",format_json_for(u.sample_rate,u.sample_format));ju64(d,"sampled_at_us64",time_us_64());}
 else if(!strcmp(op,"USB_SET_FORMATS")){
  int mask=number(args,"formats_mask",0);uint32_t configured=config_formats(&committed);
  if(transaction.pending){ok=false;code="USB_CONFIG_BUSY";}
  else if(u.state!=USB_AUDIO_STOPPED){ok=false;code="USB_EMULATION_NOT_STOPPED";}
  else if(uint64_arg(args,"expected_emulation_revision",UINT64_MAX)!=u.revision){ok=false;code="REVISION_CONFLICT";}
  else if(mask<1||!usb_formats_valid((uint32_t)mask)||((uint32_t)mask&~configured)){ok=false;code="UNSUPPORTED_FORMAT";}
  else if(!usb_audio_set_formats((uint32_t)mask)){ok=false;code="USB_EMULATION_TRANSITION_BUSY";}
  else{d=obj();jnum(d,"formats_mask",mask);js(d,"scope","runtime_until_restart_or_next_config_commit");}
 }
 else if(!strcmp(op,"USB_HOST_KEY")){
  const char*key=str(args,"key");uint16_t usage=usb_host_control_key_usage(key);
  usb_host_control_status_t h;usb_host_control_get_status(&h);
  if(!usage){ok=false;code="INVALID_ARGUMENT";message="key must be listed in capabilities.host_control_keys";}
  else if(!h.ready||u.state!=USB_AUDIO_RUNNING||!u.mounted||u.suspended){ok=false;code="HOST_CONTROL_NOT_READY";message="USB host HID interface is not ready";}
  else if(!usb_host_control_enqueue(usage)){ok=false;code="HOST_CONTROL_BUSY";message="Host key queue is full; do not retry an ambiguous request";}
  else{d=obj();jb(d,"accepted",true);js(d,"key",key);js(d,"meaning","one_host_key_press");}
 }
 else if(!strcmp(op,"USB_SET_CAPTURE_VOLUME")){
  int volume=number(args,"volume_q8",INT32_MAX);cJSON*mute=cJSON_GetObjectItemCaseSensitive(args,"mute");
  if(!usb_profile_has_capture(usb_descriptors_active_profile())){ok=false;code="UNSUPPORTED_DIRECTION";}
  else if(!cJSON_IsBool(mute)||volume< -60*256||volume>0){ok=false;code="INVALID_FIELD";}
  else{usb_microphone_request_control((int16_t)volume,cJSON_IsTrue(mute));d=obj();jb(d,"accepted",true);js(d,"scope","usb_microphone_feature_unit_host_may_override");}
 }
 else if(!strcmp(op,"SET_INTERNAL_VOLUME")){
  int volume=number(args,"volume_percent",INT32_MAX);cJSON*mute=cJSON_GetObjectItemCaseSensitive(args,"mute");
  if(!cJSON_IsBool(mute)||volume<0||volume>100){ok=false;code="INVALID_FIELD";}
  else{usb_audio_set_internal_volume((unsigned)volume,cJSON_IsTrue(mute));d=obj();jb(d,"accepted",true);js(d,"scope","internal_playback_runtime");}
 }
 else if(!strcmp(op,"USB_EMULATION_START")||!strcmp(op,"USB_EMULATION_STOP")){
  if(!strcmp(op,"USB_EMULATION_START")&&transaction.pending){ok=false;code="USB_CONFIG_BUSY";message="Finish or cancel configuration first";}
  else if(uint64_arg(args,"expected_emulation_revision",UINT64_MAX)!=u.revision){ok=false;code="REVISION_CONFLICT";message="Refresh USB state";}
  else if(u.state==USB_AUDIO_STARTING||u.state==USB_AUDIO_STOPPING){ok=false;code="USB_EMULATION_TRANSITION_BUSY";message="Wait for transition completion";}else if(u.state==USB_AUDIO_FAULT&&!strcmp(op,"USB_EMULATION_START")){ok=false;code="USB_EMULATION_FAULT";message="USB fault requires recovery";}else{bool start=!strcmp(op,"USB_EMULATION_START");if(!start){stream_stop();capture_stop();}usb_audio_set_enabled(start);d=obj();js(d,"operation_id",start?"usb-start":"usb-stop");js(d,"state",start?(u.state==USB_AUDIO_RUNNING?"RUNNING":"STARTING"):(u.state==USB_AUDIO_STOPPED?"STOPPED":"STOPPING"));}
 }
 else if(!strcmp(op,"GET_OPERATION")){const char*tid=str(args,"transaction_id");if(tid&&strcmp(tid,transaction.id)){ok=false;code="TRANSACTION_NOT_FOUND";message="Unknown transaction";}else{d=info_json();js(d,"stage",tid?(transaction.pending?(transaction.applied?"APPLIED":"PREPARED"):(transaction.final_stage?transaction.final_stage:"UNKNOWN")):(u.state==USB_AUDIO_RUNNING||u.state==USB_AUDIO_STOPPED?"COMPLETED":"PENDING"));if(tid){js(d,"transaction_id",transaction.id);ju64(d,"config_revision",config_revision);cJSON_AddItemToObject(d,"committed",config_json(&committed));}}}
 else if(!strcmp(op,"USB_SET_CAPTURE_FORMATS")){
 int mask=number(args,"formats_mask",0);if(u.state!=USB_AUDIO_STOPPED){ok=false;code="USB_EMULATION_NOT_STOPPED";}else if(uint64_arg(args,"expected_emulation_revision",UINT64_MAX)!=u.revision){ok=false;code="REVISION_CONFLICT";}else if(!usb_mic_mask_valid((uint32_t)mask)||((uint32_t)mask&~(committed.capture_formats_mask?committed.capture_formats_mask:USB_MIC_DEFAULT_MASK))){ok=false;code="UNSUPPORTED_FORMAT";}else if(!usb_audio_set_capture_formats((uint32_t)mask)){ok=false;code="USB_EMULATION_TRANSITION_BUSY";}else{d=obj();jnum(d,"formats_mask",mask);}}
 else if(!strcmp(op,"CAPTURE_PREPARE")){
 usb_mic_status_t m;usb_microphone_get_status(&m);cJSON*f=cJSON_GetObjectItemCaseSensitive(args,"format");int port=number(args,"receive_port",0);uint16_t chosen_port=udp_port_request(args,CAPTURE_PORT,CAPTURE_SOCKET);
 if(!usb_profile_has_capture(usb_descriptors_active_profile())){ok=false;code="UNSUPPORTED_DIRECTION";}else if(u.state!=USB_AUDIO_RUNNING){ok=false;code="USB_EMULATION_STOPPED";}else if(number(f,"sample_rate",0)!=(int)m.rate||number(f,"bits",0)!=m.bits||number(f,"channels",0)!=1||port<1024||port>65535){ok=false;code="UNSUPPORTED_FORMAT";}
 else if(!chosen_port){ok=false;code="UDP_PORT_UNAVAILABLE";message="Invalid, reserved or occupied board UDP port";}
 else{capture_stop();capture_port=chosen_port;if(!udp_port_bind(CAPTURE_SOCKET,capture_port)){ok=false;code="UDP_BIND_FAILED";}else{capture.prepared=true;capture.id=get_rand_32()|1;capture.token=get_rand_64()|1;capture.generation=m.generation;capture.keepalive=now_ms();capture.port=(uint16_t)port;memcpy(capture.ip,s->peer,4);capture.balanced=str(args,"priority")&&!strcmp(str(args,"priority"),"balanced");d=obj();jnum(d,"stream_id",capture.id);jnum(d,"epoch",capture.generation);ju64(d,"session_token",capture.token);jnum(d,"source_port",capture_port);jnum(d,"packet_frames",(m.rate+999)/1000);cJSON_AddItemToObject(d,"format",capture_format_json(&m));jb(d,"host_active",m.active);}}}
 else if(!strcmp(op,"CAPTURE_START")){if(!capture.prepared||!capture.probed||uint64_arg(args,"stream_id",0)!=capture.id){ok=false;code="STREAM_NOT_FOUND";}else{capture.running=true;usb_microphone_set_network_enabled(true);capture.keepalive=now_ms();d=capture_json();}}
 else if(!strcmp(op,"CAPTURE_KEEPALIVE")){if(!capture.prepared||uint64_arg(args,"stream_id",0)!=capture.id){ok=false;code="STREAM_NOT_FOUND";}else{capture.keepalive=now_ms();d=capture_json();}}
 else if(!strcmp(op,"CAPTURE_STOP")){if(capture.prepared&&uint64_arg(args,"stream_id",0)!=capture.id){ok=false;code="STREAM_NOT_FOUND";}else capture_stop();}
 else if(!strcmp(op,"STREAM_PREPARE")){
  cJSON*f=cJSON_GetObjectItemCaseSensitive(args,"format");const char*dir=str(args,"direction"),*fmt=str(f,"sample_format");int port=number(args,"receive_port",0);uint16_t chosen_port=udp_port_request(args,AUDIO_PORT,1);
  if(u.state!=USB_AUDIO_RUNNING){ok=false;code=u.state==USB_AUDIO_STOPPED?"USB_EMULATION_STOPPED":"USB_EMULATION_TRANSITION_BUSY";message="USB emulation is not running";}
  else if(str(args,"codec")&&strcmp(str(args,"codec"),"pcm")&&strcmp(str(args,"codec"),"ima_adpcm")){ok=false;code="UNSUPPORTED_CODEC";message="Codec must be pcm or ima_adpcm";}
  else if(str(args,"fec_mode")&&strcmp(str(args,"fec_mode"),"none")&&strcmp(str(args,"fec_mode"),"xor4")){ok=false;code="UNSUPPORTED_FEC";message="FEC mode must be none or xor4";}
  else if(!dir||strcmp(dir,"playback")||port<1024||port>65535||number(args,"packet_frames",(u.sample_rate+999)/1000)!=(int)((u.sample_rate+999)/1000)||number(f,"sample_rate",0)!=(int)u.sample_rate||number(f,"channels",0)!=2||!fmt||strcmp(fmt,format_name(u.sample_format))){ok=false;code="UNSUPPORTED_FORMAT";message="Request must match the current USB Host format and its 1 ms frame count";}
  else if(!chosen_port){ok=false;code="UDP_PORT_UNAVAILABLE";message="Invalid, reserved or occupied board UDP port";}
  else {stream_stop();audio_port=chosen_port;if(!udp_port_bind(1,audio_port)){ok=false;code="UDP_BIND_FAILED";}else{stream.codec=str(args,"codec")&&!strcmp(str(args,"codec"),"ima_adpcm");stream.follow_usb=cJSON_IsTrue(cJSON_GetObjectItemCaseSensitive(args,"follow_usb"));stream.fec_enabled=str(args,"fec_mode")&&!strcmp(str(args,"fec_mode"),"xor4");stream.id=get_rand_32()|1;stream.epoch=u.generation;stream.generation=u.generation;stream.sample_rate=u.sample_rate;stream.sample_format=u.sample_format;stream.token=get_rand_64()|1;stream.sequence=0;stream.port=port;memcpy(stream.ip,s->peer,4);stream.prepared=true;stream.keepalive=now_ms();d=obj();jnum(d,"stream_id",stream.id);jnum(d,"epoch",stream.epoch);ju64(d,"session_token",stream.token);jnum(d,"source_port",audio_port);jnum(d,"packet_frames",(u.sample_rate+999)/1000);cJSON_AddItemToObject(d,"format",format_json_for(u.sample_rate,u.sample_format));jb(d,"waiting_usb",!u.streaming);jb(d,"follow_usb",stream.follow_usb);js(d,"fec_mode",stream.fec_enabled?"xor4":"none");js(d,"codec",stream.codec?"ima_adpcm":"pcm");}}
 }
 else if(!strcmp(op,"STREAM_START")){
  if(!stream.prepared||(!stream.follow_usb&&u.generation!=stream.generation)||uint64_arg(args,"stream_id",0)!=stream.id||uint64_arg(args,"epoch",UINT64_MAX)!=stream.epoch){ok=false;code="STREAM_NOT_FOUND";message="Prepare a new stream";}
  else if(!stream.probed){ok=false;code="PROBE_REQUIRED";message="Send UDP PROBE first";}
  else if(u.state!=USB_AUDIO_RUNNING){ok=false;code="USB_EMULATION_STOPPED";message="Start USB emulation first";}
  else{stream.running=true;audio_encode_max_us=0;stream_poll_max_us=stream_control_max_us=stream_events_max_us=stream_raw_max_us=0;stream.keepalive=now_ms();d=obj();jb(d,"waiting_usb",!u.streaming);jb(d,"follow_usb",stream.follow_usb);js(d,"fec_mode",stream.fec_enabled?"xor4":"none");js(d,"codec",stream.codec?"ima_adpcm":"pcm");}
 }
 else if(!strcmp(op,"SET_CLOCK_OBSERVATION")){
  cJSON*enabled=cJSON_GetObjectItemCaseSensitive(args,"enabled");uab_clock_identity_t id=clock_identity();
  if(!cJSON_IsBool(enabled)||number(args,"version",0)!=1||!str(args,"boot_id")||!str(args,"session_token")){ok=false;code="INVALID_FIELD";message="enabled/version and decimal-string boot_id/session_token required";}
  else if(!stream.prepared||!stream.probed||!stream.running||owner!=s->socket||memcmp(s->peer,stream.ip,4)||u.state!=USB_AUDIO_RUNNING||uint64_arg(args,"boot_id",0)!=id.boot_id||uint64_arg(args,"stream_id",0)!=id.stream_id||uint64_arg(args,"epoch",0)!=id.epoch||uint64_arg(args,"session_token",0)!=id.token){ok=false;code="STREAM_NOT_FOUND";message="Clock binding must match the current started audio stream";}
  else if(cJSON_IsTrue(enabled)&&!uab_clock_observation_enable(&clock_observation,&id)){ok=false;code="CLOCK_SERVICE_BUDGET_EXCEEDED";message="Clock work exceeded 100 us; this stream identity is latched off";}
  else{bool enable=cJSON_IsTrue(enabled);if(!enable)uab_clock_observation_disable(&clock_observation);d=obj();jb(d,"enabled",enable);jnum(d,"version",1);ju64(d,"boot_id",id.boot_id);jnum(d,"stream_id",id.stream_id);jnum(d,"epoch",id.epoch);ju64(d,"session_token",id.token);jnum(d,"request_bytes",UAB_CLOCK_PACKET_BYTES);jnum(d,"source_port",audio_port);jnum(d,"max_requests_per_second",4);jnum(d,"max_pending_us",UAB_CLOCK_PENDING_US);}
 }
 else if(!strcmp(op,"STREAM_STOP")){stream_stop();}
 else if(!strcmp(op,"SUBSCRIBE_STATS")){int interval=number(args,"period_ms",number(args,"interval_ms",1000));if(interval!=0&&(interval<500||interval>60000)){ok=false;code="INVALID_FIELD";message="Statistics period must be 0 or 500..60000 ms";}else{s->stats_interval=interval;s->stats_due=now_ms()+interval;d=obj();jnum(d,"period_ms",interval);}}
 else if(!strcmp(op,"STREAM_KEEPALIVE")){if(!stream.prepared||uint64_arg(args,"stream_id",0)!=stream.id){ok=false;code="STREAM_NOT_FOUND";}else stream.keepalive=now_ms();}
 else if(!strcmp(op,"RESET_STATS")){audio_sent=audio_dropped=invalid_packets=poll_max_us=audio_busy_retries=audio_pending_expired=0;uab_clock_observation_reset_stats(&clock_observation);memset(udp_completed,0,sizeof(udp_completed));memset(udp_timeouts,0,sizeof(udp_timeouts));d=obj();js(d,"reset_scope","network_runtime_only");js(d,"usb_counters","boot_lifetime");}else if(!strcmp(op,"REBOOT")){stream_stop();reboot_at=now_ms()+750;d=obj();js(d,"operation_id","reboot");js(d,"stage","ACCEPTED");}else if(!strcmp(op,"IDENTIFY")){int seconds=number(args,"seconds",5);if(seconds<1||seconds>30){ok=false;code="INVALID_FIELD";}else{uab_indicator_identify((unsigned)seconds);}}
 else{ok=false;code="NOT_IMPLEMENTED";message="This development firmware does not implement this operation";}
 if(ready)audio_poll();response(s,id,ok,code,message,d,++s->tx_seq);if(ready)audio_poll();
}
static void events_poll(void){
 static uint32_t usb_revision,usb_generation,network_generation;static bool running;static const char*final_stage;
 usb_audio_status_t u;usb_audio_get_status(&u);unsigned changed=0;if(u.revision!=usb_revision){usb_revision=u.revision;changed|=1;}if(u.generation!=usb_generation){usb_generation=u.generation;changed|=2;}if(generation!=network_generation){network_generation=generation;changed|=4;}if(stream.running!=running){running=stream.running;changed|=8;}if(final_stage!=transaction.final_stage){final_stage=transaction.final_stage;if(final_stage)changed|=!strcmp(final_stage,"COMMITTED")?16:32;}
 for(int i=0;i<2;i++){session_t*s=&sessions[i];if(!s->connected||s->closing)continue;s->events|=changed;if(s->stats_interval&&now_ms()>=s->stats_due){s->events|=64;s->stats_due=now_ms()+s->stats_interval;}if(!s->events||s->tx_at<s->tx_len||s->have||getSn_RX_RSR(s->socket))continue;
  unsigned bit=s->events&(~s->events+1);s->events&=~bit;const char*name=bit==1?"USB_EMULATION_CHANGED":bit==2?"FORMAT_CHANGED":bit==4?"NETWORK_CHANGED":bit==8?"STREAM_STATE_CHANGED":bit==16?"CONFIG_COMMITTED":bit==32?"CONFIG_ROLLED_BACK":"STATS";json_used=0;cJSON*e=obj(),*d=info_json();js(e,"event",name);jnum(d,"audio_packets_submitted",audio_sent);jnum(d,"audio_packets_dropped",audio_dropped);jnum(d,"audio_busy_retries",audio_busy_retries);jnum(d,"audio_pending_expired",audio_pending_expired);jnum(d,"usb_rx_bytes",u.rx_bytes);jnum(d,"usb_expired_frames",u.expired_frames);jb(d,"stream_running",stream.running);jnum(d,"stream_id",stream.id);cJSON_AddItemToObject(e,"data",d);
  memset(s->tx,0,32);if(!uab_json_print(e,(char*)s->tx+32,8193,json_audio_service,NULL)){close_session(s);continue;}size_t n=strlen((char*)s->tx+32);memcpy(s->tx,"UABC",4);s->tx[4]=1;s->tx[5]=3;w32(s->tx+8,n);w64(s->tx+16,++s->tx_seq);w64(s->tx+24,boot_id);s->tx_len=32+n;s->tx_at=0;s->tx_deadline=now_ms()+3000;
 }
}
static void control_poll(session_t*s){
 if(s->closing){if(getSn_SR(s->socket)==SOCK_CLOSED||now_ms()>=s->close_at){close(s->socket);reset_session(s);}return;}
 uint8_t status=getSn_SR(s->socket);if(status==SOCK_CLOSED){reset_session(s);socket(s->socket,Sn_MR_TCP,CONTROL_PORT,SF_IO_NONBLOCK);listen(s->socket);return;}
 if(status==SOCK_CLOSE_WAIT){close_session(s);return;}if(status!=SOCK_ESTABLISHED)return;
 if(getSn_IR(s->socket)&Sn_IR_CON){setSn_IR(s->socket,Sn_IR_CON);getSn_DIPR(s->socket,s->peer);s->last=now_ms();s->connected_at=s->last;}
 if((!s->connected&&now_ms()-s->connected_at>5000)||(s->have&&now_ms()-s->frame_at>3000)||(s->tx_at<s->tx_len&&now_ms()>s->tx_deadline)){close_session(s);return;}
 if(now_ms()-s->last>(s->connected?30000:5000)){close_session(s);return;}
 if(s->tx_at<s->tx_len){size_t n=s->tx_len-s->tx_at;if(n>512)n=512;int sent=send(s->socket,s->tx+s->tx_at,n);if(sent>0){s->tx_at+=sent;s->last=now_ms();}else if(sent<0)close_session(s);return;}
 if(!getSn_RX_RSR(s->socket))return;size_t amount=s->want-s->have;if(amount>512)amount=512;int n=recv(s->socket,s->rx+s->have,amount);if(n<=0){if(n<0)close_session(s);return;}if(!s->have)s->frame_at=now_ms();s->have+=n;s->last=now_ms();if(s->have<s->want)return;
 if(s->want==32){uint16_t flags=r16(s->rx+6);uint32_t bytes=r32(s->rx+8);if(memcmp(s->rx,"UABC",4)||s->rx[4]!=1||s->rx[5]!=1||flags!=0||bytes<2||bytes>8192||r32(s->rx+12)==0||(s->connected&&r64(s->rx+24)!=boot_id)){close_session(s);return;}s->want=32+bytes;return;}
 if(s->connected){if(r64(s->rx+16)!=s->rx_seq+1){close_session(s);return;}s->rx_seq++;}
 execute(s);s->have=0;s->want=32;
}
static bool capture_emit(const uint8_t*pcm,uint16_t frames,uint16_t gap,void*ctx){const usb_mic_status_t*m=ctx;return usb_microphone_enqueue(pcm,frames,m->rate,m->bits,m->generation,gap);}
static void capture_poll_core(void){
 uint32_t at=time_us_32();if(!ready)return;if(!capture.balanced&&((audio_tx.length&&(uint32_t)(at-audio_tx.captured_us)>=2500u)||(audio_fec.ready&&(uint32_t)(at-audio_fec.ready_us)>=3000u)))return;
 if(getSn_SR(CAPTURE_SOCKET)==SOCK_CLOSED){uab_udp_completion_reset(&udp_completion[CAPTURE_SOCKET]);socket(CAPTURE_SOCKET,Sn_MR_UDP,capture_port,SF_IO_NONBLOCK);}
 if(capture.prepared&&now_ms()-capture.keepalive>5000)capture_stop();
 usb_mic_status_t m;usb_microphone_get_status(&m);if(capture.running&&m.active&&m.generation==capture.generation){uab_capture_reorder_drain(&capture_reorder,time_us_32(),m.rate/50,2,capture_emit,&m);capture_gaps=capture_reorder.missing_frames;}
 uint8_t ip[4];uint16_t port;int n=udp_receive(CAPTURE_SOCKET,udp_buffer,sizeof(udp_buffer),ip,&port);if(n<=0)return;
 if(!capture.prepared){capture_stale++;return;}if(port!=capture.port||memcmp(ip,capture.ip,4)||n<48||memcmp(udp_buffer,"UABM",4)||udp_buffer[4]!=1||udp_buffer[5]!=48||r16(udp_buffer+38)){capture_invalid++;return;}if(r32(udp_buffer+8)!=capture.id||r32(udp_buffer+12)!=capture.generation||r64(udp_buffer+40)!=capture.token){capture_stale++;return;}
 if(r16(udp_buffer+6)==8&&n==48){capture.probed=udp_send(CAPTURE_SOCKET,udp_buffer,48,ip,port);return;}
 unsigned bytes=m.bits/8,frames=r16(udp_buffer+32);uint32_t seq=r32(udp_buffer+16);uint64_t index=r64(udp_buffer+20);
 if(!capture.running||r16(udp_buffer+6)||udp_buffer[34]!=1||udp_buffer[35]!=(m.bits==24?2:1)||r32(udp_buffer+28)!=m.rate||!frames||frames>(m.rate+999)/1000||index>UINT64_MAX-frames||r16(udp_buffer+36)!=frames*bytes||n!=48+(int)(frames*bytes)){capture_invalid++;return;}
 capture_received++;if(!m.active||m.generation!=capture.generation)return;
 uab_capture_reorder_push(&capture_reorder,index,(uint16_t)frames,udp_buffer+48,(uint16_t)(frames*bytes),time_us_32());
 uab_capture_reorder_drain(&capture_reorder,time_us_32(),m.rate/50,2,capture_emit,&m);capture_gaps=capture_reorder.missing_frames;
 uint32_t elapsed=time_us_32()-at;if(elapsed>capture_poll_max_us)capture_poll_max_us=elapsed;
}
static void capture_poll(void){uab_clock_capture_service(capture_poll_core,&clock_observation.enabled,&clock_observation.pending,clock_observation_poll);}

static bool audio_submit(const uint8_t*data,uint16_t bytes,void*context){(void)context;return udp_send_kind(1,data,bytes,stream.ip,stream.port,UAB_UDP_AUDIO);}
static bool audio_flush_pending(void){
 uint16_t original_length=audio_tx.length;
 uab_audio_tx_result_t result=uab_audio_tx_poll(&audio_tx,time_us_32(),stream.generation,stream.id,stream.token,audio_submit,NULL);
 if(result==UAB_TX_SENT){audio_sent++;if(stream.fec_enabled)uab_audio_fec_push(&audio_fec,audio_tx.packet,original_length,time_us_32());}
 else if(result==UAB_TX_EXPIRED){audio_dropped++;audio_pending_expired++;}
 else if(result==UAB_TX_WAITING){audio_busy_retries++;return false;}
 return true;
}
static void audio_poll(void){
 usb_audio_status_t status;usb_audio_get_status(&status);
 // A subscription belongs to the listener, not to one Windows playback burst.
 // Keep its destination through alt 0. Every data packet carries the source
 // generation, so even losing the first packet cannot join stale timelines.
 if(stream.prepared&&status.state!=USB_AUDIO_RUNNING)stream_stop();
 if(stream.prepared&&status.generation!=stream.generation){
  if(!stream.follow_usb)stream_stop();
  else if((int32_t)(status.generation-stream.generation)>0){uab_audio_tx_clear(&audio_tx);uab_audio_fec_clear(&audio_fec);stream.generation=status.generation;uab_clock_observation_disable(&clock_observation);clock_anchor.valid=false;}
 }
 if(udp_completion[1].pending&&udp_completion[1].kind==UAB_UDP_CLOCK)udp_poll_completion(1);
 if(getSn_SR(1)==SOCK_CLOSED){uab_udp_completion_reset(&udp_completion[1]);uab_udp_destination_reset(&audio_destination);socket(1,Sn_MR_UDP,audio_port,SF_IO_NONBLOCK);clock_udp_rx.recv_pending=false;}
 // Only the optional clock mode defers RX. Legacy/probe keeps its exact path;
 // the short circuit avoids even the queue snapshot while observation is off.
 bool defer_clock_rx=clock_observation.enabled&&!uab_clock_observation_rx_admitted(audio_tx.length!=0,audio_fec.ready,usb_audio_queued_blocks());
 if(defer_clock_rx)clock_observation.rx_audio_priority_deferrals++;
 uint8_t ip[4];uint16_t port;uint32_t udp_read_began=clock_observation.enabled&&!defer_clock_rx?time_us_32():0;int n=defer_clock_rx?0:audio_udp_receive(udp_buffer,sizeof(udp_buffer),ip,&port,udp_read_began);
 if(n){if(n>=4&&!memcmp(udp_buffer,"UABT",4)){
   uint64_t b2=time_us_64();uint32_t began=clock_observation.enabled?udp_read_began:time_us_32(),validate_began=time_us_32();
   if(!clock_receive_budget)clock_observation.skipped++;
   else{clock_receive_budget--;uab_clock_observation_receive_from(&clock_observation,udp_buffer,(size_t)n,b2,stream.running&&!memcmp(ip,stream.ip,4)&&port==stream.port,owner>=0);}
   uint32_t validation=time_us_32()-validate_began;if(validation>clock_observation.rx_validate_max_us)clock_observation.rx_validate_max_us=validation;
   uab_clock_observation_record_work_phase(&clock_observation,time_us_32()-began,UAB_CLOCK_BUDGET_RX);
  }else if(n==48&&!memcmp(udp_buffer,"UABA",4)&&udp_buffer[4]==1&&udp_buffer[5]==48&&r16(udp_buffer+6)==8&&stream.prepared&&!memcmp(ip,stream.ip,4)&&port==stream.port&&r32(udp_buffer+8)==stream.id&&r32(udp_buffer+12)==stream.epoch&&r64(udp_buffer+40)==stream.token&&r32(udp_buffer+16)==0&&r64(udp_buffer+20)==0&&r64(udp_buffer+28)==0&&r32(udp_buffer+36)==0){stream.probed=udp_send_kind(1,udp_buffer,48,ip,port,UAB_UDP_PROBE);}else invalid_packets++;}
 if(stream.prepared&&now_ms()-stream.keepalive>5000)stream_stop();
 if(owner<0&&owner_until&&now_ms()>owner_until){stream_stop();owner_until=0;}
 if(!audio_flush_pending())return;
 if(audio_fec.ready){
  if((uint32_t)(time_us_32()-audio_fec.ready_us)>UAB_AUDIO_BLOCK_DEADLINE_US){audio_fec.ready=false;fec_packets_expired++;}
  else if(udp_send_kind(1,audio_fec.packet,audio_fec.length,stream.ip,stream.port,UAB_UDP_FEC)){audio_fec.ready=false;fec_packets_sent++;}
  else return;
 }
 usb_audio_block_t block;for(int i=0;i<4&&usb_audio_read_block(&block);i++){
  if(!stream.running)continue;
  if(block.generation!=stream.generation){
   if(!stream.follow_usb){stream_stop();continue;}
   // Core 0 can publish a block before the next status snapshot is visible.
   if((int32_t)(block.generation-stream.generation)<0)continue;
   uab_audio_tx_clear(&audio_tx);uab_audio_fec_clear(&audio_fec);stream.generation=block.generation;uab_clock_observation_disable(&clock_observation);clock_anchor.valid=false;
  }
  // The producer publishes all block fields before write_index release. This
  // local copy is complete before read_index release permits slot reuse; Core 1
  // is the only reader/writer of the cached anchor, including its 64-bit fields.
  if(clock_observation.enabled){clock_anchor=(uab_clock_anchor_t){block.sample_index,block.extract_us64,block.generation,block.sample_rate,block.frames,true};}
  uint32_t seq=stream.sequence++;
   if((uint32_t)(time_us_32()-block.captured_us)>UAB_AUDIO_BLOCK_DEADLINE_US){audio_dropped++;continue;}
  uint16_t payload=block.frames*2*(block.sample_format==2?3:2);if(payload>USB_AUDIO_MAX_PCM_BYTES||(!stream.follow_usb&&(block.sample_rate!=stream.sample_rate||block.sample_format!=stream.sample_format))){stream_stop();continue;}
  uint32_t encode_at=time_us_32();
  if(!uab_audio_tx_prepare_codec(&audio_tx,&block,stream.id,stream.follow_usb?block.generation:stream.epoch,seq,stream.token,stream.codec)){audio_dropped++;continue;}
  uint32_t encode_us=time_us_32()-encode_at;if(encode_us>audio_encode_max_us)audio_encode_max_us=encode_us;audio_pcm_bytes+=payload;audio_wire_bytes+=audio_tx.length;
   if(!audio_flush_pending()||audio_fec.ready)break;
 }
 // Enabled only, after ordinary audio work has been serviced. Control/JSON
 // yielding already calls this owner several times per main loop, providing
 // natural phase opportunities without a new thread, timer or spin loop.
 if(clock_observation.enabled&&clock_observation.pending)clock_observation_poll();
}
static void UAB_CLOCK_RAM(clock_observation_poll)(void){
 if(!clock_observation.enabled||!clock_observation.pending)return;
 uint32_t began=time_us_32();uint64_t now=time_us_64();
 if(uab_clock_observation_expire(&clock_observation,now))goto finished;
 uab_clock_identity_t id=clock_identity();
 if(!stream.running||!stream.prepared||!stream.probed||owner<0||!uab_clock_observation_matches(&clock_observation,&id)){uab_clock_observation_disable(&clock_observation);goto finished;}
 // Service audio before this entry and again immediately afterward. Never
 // acquire the socket while a source block/FEC is queued, nor in the latter
 // half of an observed nominal 1 ms extraction interval. This is a conservative
 // software budget; actual non-interference remains an instrumented board test.
 usb_audio_status_t status;usb_audio_get_status(&status);
 if(!uab_clock_observation_audio_budget(&clock_observation,&clock_anchor,now,audio_tx.length!=0,audio_fec.ready,usb_audio_queued_blocks(),status.streaming))goto finished;
 uint32_t preflight_now=time_us_32();
 if(!uab_udp_clock_audio_grace_ready(&udp_completion[1],preflight_now)){clock_observation.preflight_audio_tx_waits++;goto finished;}
 if(!uab_clock_observation_preflight_slot(&clock_observation,&clock_anchor,now,status.streaming,clock_service_loop))goto finished;
 clock_observation.last_preflight_pending=udp_completion[1].pending;
 clock_observation.last_preflight_pending_kind=udp_completion[1].kind;
 clock_observation.last_preflight_submitted_age_us=udp_completion[1].pending?preflight_now-udp_completion[1].submitted_us:0;
 // The final submit already validates socket/CR/TX room. Avoid duplicating
 // those SPI reads here; only retire an existing audio completion if needed.
 if(!clock_retire_audio_completion()){clock_observation.busy++;clock_observation.last_busy_stage=1;clock_observation.last_busy_pending=clock_observation.last_preflight_pending;clock_observation.last_busy_pending_kind=clock_observation.last_preflight_pending_kind;clock_observation.last_busy_submitted_age_us=clock_observation.last_preflight_submitted_age_us;goto finished;}
 if(!uab_clock_observation_record_work_phase(&clock_observation,time_us_32()-began,UAB_CLOCK_BUDGET_TX_PREPARE))goto finished;
 // The clock-only submit rebuilds B3 after its final bounded preflight. It does
 // not call the ordinary sender and cannot reuse a previous busy attempt's B3.
 uint32_t prepared=time_us_32(),prepare_us=prepared-began;if(prepare_us>clock_observation.tx_prepare_max_us)clock_observation.tx_prepare_max_us=prepare_us;
 bool submitted=clock_submit_reply(began);uint32_t submit_us=time_us_32()-prepared;if(submit_us>clock_observation.tx_submit_max_us)clock_observation.tx_submit_max_us=submit_us;
 if(submitted){clock_observation.pending=false;clock_observation.submitted++;}
 else{clock_observation.busy++;clock_observation.last_busy_stage=2;clock_observation.last_busy_pending=clock_observation.last_preflight_pending;clock_observation.last_busy_pending_kind=clock_observation.last_preflight_pending_kind;clock_observation.last_busy_submitted_age_us=clock_observation.last_preflight_submitted_age_us;}
finished: ;
 uab_clock_observation_record_work_phase(&clock_observation,time_us_32()-began,UAB_CLOCK_BUDGET_TX_COMPLETE);
}
void uab_network_prepare(const uab_config_t *config){committed=*config;active=*config;config_revision=config->revision;}
void uab_network_main(void){
 cJSON_Hooks hooks={json_alloc,json_free};cJSON_InitHooks(&hooks);sessions[0].socket=2;sessions[1].socket=6;
 pico_unique_board_id_t id;pico_get_unique_board_id(&id);uint8_t hash[32];uab_sha256(id.id,sizeof(id.id),hash);memcpy(device_id,hash,16);for(int i=0;i<16;i++)sprintf(device_hex+2*i,"%02x",device_id[i]);memcpy(mac,device_id,6);mac[0]=(mac[0]&0xfc)|2;boot_id=get_rand_64()|1;
 gpio_init(17);gpio_set_dir(17,GPIO_OUT);gpio_put(17,1);gpio_init(20);gpio_set_dir(20,GPIO_OUT);gpio_put(20,0);sleep_ms(2);gpio_put(20,1);sleep_ms(160);
 uint32_t hz=spi_init(spi0,25000000);spi_actual_hz=hz;gpio_set_function(16,GPIO_FUNC_SPI);gpio_set_function(18,GPIO_FUNC_SPI);gpio_set_function(19,GPIO_FUNC_SPI);gpio_init(21);gpio_set_dir(21,GPIO_IN);gpio_pull_up(21);
 reg_wizchip_cs_cbfunc(cs_on,cs_off);reg_wizchip_spi_cbfunc(spi_read,spi_write);reg_wizchip_spiburst_cbfunc(spi_reads,spi_writes);uint8_t tx[8]={2,4,2,1,1,2,2,2},rx[8]={2,2,2,1,1,4,2,2};
 if(getVERSIONR()!=4||wizchip_init(tx,rx)!=0){printf("NET FAULT W5500 version=%u; USB remains independent\n",getVERSIONR());network_state="FAULT";while(1){uab_watchdog_core1_heartbeat();usb_audio_block_t discard;while(usb_audio_read_block(&discard)){}sleep_ms(1);}}
 memcpy(net.mac,mac,6);net.dhcp=NETINFO_DHCP;wizchip_setnetinfo(&net);setRTR(2000);setRCR(3);
 static struct repeating_timer timer;add_repeating_timer_ms(-1000,dhcp_tick,NULL,&timer);
 printf("UAB firmware %s device=%s SPI=%lu W5500=%u\n",VERSION,device_hex,(unsigned long)hz,getVERSIONR());
 uint64_t report=0;while(1){clock_service_loop++;clock_receive_budget=1;uab_watchdog_core1_heartbeat();uint32_t begin=time_us_32();if(ready)audio_poll();address_poll();uint32_t phase=time_us_32();raw_poll();if(stream.running&&time_us_32()-phase>stream_raw_max_us)stream_raw_max_us=time_us_32()-phase;if(linked)discovery_poll();discovery_replies();transaction_poll();if(reboot_at&&now_ms()>=reboot_at){watchdog_reboot(0,0,0);while(1)tight_loop_contents();}if(ready){audio_poll();capture_poll();audio_poll();capture_poll();audio_poll();capture_poll();network_name_poll();phase=time_us_32();control_poll(&sessions[0]);if(stream.running&&time_us_32()-phase>stream_control_max_us)stream_control_max_us=time_us_32()-phase;audio_poll();capture_poll();phase=time_us_32();control_poll(&sessions[1]);if(stream.running&&time_us_32()-phase>stream_control_max_us)stream_control_max_us=time_us_32()-phase;audio_poll();capture_poll();phase=time_us_32();events_poll();if(stream.running&&time_us_32()-phase>stream_events_max_us)stream_events_max_us=time_us_32()-phase;if(clock_observation.enabled){audio_poll();clock_observation_poll();audio_poll();}}else{usb_audio_block_t discard;while(usb_audio_read_block(&discard)){};}uint32_t elapsed=time_us_32()-begin;if(elapsed>poll_max_us)poll_max_us=elapsed;if(stream.running&&elapsed>stream_poll_max_us)stream_poll_max_us=elapsed;
  if(now_ms()-report>=5000){report=now_ms();usb_audio_status_t u;usb_audio_get_status(&u);uart_length=snprintf(uart_pending,sizeof(uart_pending),"UAB net=%s usb=%s mounted=%u streaming=%u rx=%lu tx=%lu drops=%lu\r\n",network_state,usb_audio_get_state_name(u.state),u.mounted,u.streaming,(unsigned long)u.rx_bytes,(unsigned long)audio_sent,(unsigned long)audio_dropped);uart_at=0;}for(int i=0;i<8&&uart_at<uart_length&&uart_is_writable(uart0);i++)uart_get_hw(uart0)->dr=uart_pending[uart_at++];sleep_us(50);
 }
}











