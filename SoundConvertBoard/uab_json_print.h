#pragma once
#include <limits.h>
#include <stddef.h>
#include <string.h>
#include "cJSON.h"

/* Core-1 control serialization. Yield between values while retaining cJSON's
 * exact string escaping and numeric representation. The callback must neither
 * mutate the tree nor re-enter JSON/control processing. */
typedef struct {char *buffer;size_t capacity,used;void(*service)(void*);void *context;} uab_json_writer_t;
static inline void uab_json_service(uab_json_writer_t*w){if(w->service)w->service(w->context);}
static inline int uab_json_character(uab_json_writer_t*w,char c){if(w->used+1>=w->capacity)return 0;w->buffer[w->used++]=c;w->buffer[w->used]=0;return 1;}
static inline int uab_json_scalar(uab_json_writer_t*w,const cJSON*value){
 uab_json_service(w);size_t remaining=w->capacity-w->used;
 if(remaining>INT_MAX||!cJSON_PrintPreallocated((cJSON*)value,w->buffer+w->used,(int)remaining,0))return 0;
 w->used+=strlen(w->buffer+w->used);uab_json_service(w);return 1;
}
static inline int uab_json_value(uab_json_writer_t*w,const cJSON*value,unsigned depth){
 if(!value||depth>32)return 0;uab_json_service(w);
 int object=cJSON_IsObject(value),array=cJSON_IsArray(value);
 if(!object&&!array)return uab_json_scalar(w,value);
 if(!uab_json_character(w,object?'{':'['))return 0;
 for(const cJSON*child=value->child;child;child=child->next){
  if(child!=value->child&&!uab_json_character(w,','))return 0;
  if(object){cJSON key={0};key.type=cJSON_String;key.valuestring=child->string;if(!uab_json_scalar(w,&key)||!uab_json_character(w,':'))return 0;}
  if(!uab_json_value(w,child,depth+1))return 0;
 }
 uab_json_service(w);return uab_json_character(w,object?'}':']');
}
static inline int uab_json_print(cJSON*value,char*buffer,size_t capacity,void(*service)(void*),void*context){
 if(!buffer||!capacity)return 0;buffer[0]=0;uab_json_writer_t writer={buffer,capacity,0,service,context};return uab_json_value(&writer,value,0);
}
