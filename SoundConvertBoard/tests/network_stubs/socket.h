#pragma once
#include <stdint.h>
#define SOCK_UDP 0x22
#define Sn_MR_UDP 2
#define SF_IO_NONBLOCK 1
#define Sn_IR_TIMEOUT 8
#define Sn_IR_SENDOK 16
#define Sn_CR_SEND 0x20
#define SO_PACKINFO 0
#define PACK_REMAINED 1
void getSHAR(uint8_t*);
uint8_t getSn_SR(uint8_t);
int8_t socket(uint8_t,uint8_t,uint16_t,uint8_t);
int8_t close(uint8_t);
uint8_t getSn_IR(uint8_t);
void setSn_IR(uint8_t,uint8_t);
uint8_t getSn_CR(uint8_t);
uint16_t getSn_TX_RD(uint8_t);
uint16_t getSn_TX_WR(uint8_t);
uint16_t getSn_TX_FSR(uint8_t);
void setSn_DIPR(uint8_t,uint8_t*);
void setSn_DPORT(uint8_t,uint16_t);
void wiz_send_data(uint8_t,uint8_t*,uint16_t);
void setSn_CR(uint8_t,uint8_t);
uint16_t getSn_RX_RSR(uint8_t);
int32_t recvfrom(uint8_t,uint8_t*,uint16_t,uint8_t*,uint16_t*);
int8_t getsockopt(uint8_t,int,void*);
