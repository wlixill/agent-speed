// SPDX-License-Identifier: MIT
// Small libbpf loader. Root is required only for kernel attachment. Lifetime is tied to stdin and its GUI caller.
#include <stdio.h>
#include <stdlib.h>
#include <stdint.h>
#include <stdbool.h>
#include <stddef.h>
#include <errno.h>
#include <string.h>
#include <unistd.h>
#include <limits.h>
#include <bpf/libbpf.h>
#include <bpf/bpf.h>
#include <bpf/btf.h>
#include <signal.h>
#include <poll.h>
#include <sys/stat.h>
#include <sys/resource.h>
#include <time.h>
struct key { uint32_t pid, pad; uint64_t start_ns; };
struct counter { uint64_t rx, tx; uint32_t parent, pad; uint64_t parent_start_ns; char comm[16]; };
static volatile sig_atomic_t stopping;
static void stop(int signal_number) { (void)signal_number; stopping = 1; }
static int stat_start(uint32_t pid, uint64_t *result) {
    char path[80], text[4096]; snprintf(path,sizeof(path),"/proc/%u/stat",pid);
    FILE *file=fopen(path,"r"); if (!file) return -1;
    size_t size=fread(text,1,sizeof(text)-1,file); fclose(file); text[size]=0;
    char *cursor=strrchr(text,')'); if (!cursor) return -1; cursor++;
    char *save=0, *part=strtok_r(cursor," ", &save);
    for (int field=3; part; field++, part=strtok_r(0," ",&save)) if (field==22) { *result=strtoull(part,0,10); return 0; }
    return -1;
}
static uint64_t ticks(uint64_t ns, long hz) { return ns/1000000000ULL*(uint64_t)hz + ns%1000000000ULL*(uint64_t)hz/1000000000ULL; }
static void quoted(const char *value, size_t size) {
    putchar('"'); for(size_t i=0;i<size && value[i];i++) { unsigned char ch=(unsigned char)value[i]; if(ch=='"'||ch=='\\') { putchar('\\');putchar(ch); } else if(ch>=32 && ch<127) putchar(ch); else printf("\\u%04x",ch); } putchar('"');
}
static bool trusted(const char *path) { struct stat st; return stat(path,&st)==0 && S_ISREG(st.st_mode) && st.st_uid==0 && !(st.st_mode&(S_IWGRP|S_IWOTH)); }
int main(int argc,char **argv) {
    if(argc==2 && strcmp(argv[1],"--help")==0) { puts("Agent Speed optional IPv4/IPv6 TCP/UDP socket-I/O event helper\nRun only from the installed application's explicit administrator-authenticated Enable button.\nNo content, endpoint, port, command line or other user's activity is exported.\nCapture ends when its stdin closes. Requires Linux BTF and kprobes; pinned libbpf 1.4.7 is statically linked."); return 0; }
    if(argc==2 && strcmp(argv[1],"--preflight")==0) {
        struct btf *btf=btf__parse("/sys/kernel/btf/vmlinux",0);
        long err=libbpf_get_error(btf);
        if(!btf || err) { fprintf(stderr,"Kernel BTF parsing failed (libbpf %s, error %ld).\n",libbpf_version_string(),err); return 2; }
        const char *types[]={"socket","sock","sock_common","task_struct"};
        for(unsigned i=0;i<4;i++) if(btf__find_by_name_kind(btf,types[i],BTF_KIND_STRUCT)<=0) { fprintf(stderr,"Missing kernel BTF type: %s\n",types[i]); btf__free(btf); return 2; }
        const char *functions[]={"tcp_sendmsg","udp_sendmsg","udpv6_sendmsg","sock_recvmsg"};
        for(unsigned i=0;i<4;i++) if(btf__find_by_name_kind(btf,functions[i],BTF_KIND_FUNC)<=0) { fprintf(stderr,"Missing kernel traffic function: %s\n",functions[i]); btf__free(btf); return 2; }
        btf__free(btf);
        printf("{\"libbpf\":\"%s\",\"libbpfSourceVersion\":\"1.4.7\",\"kernelBtf\":\"ok\",\"uid\":%u,\"privilegedAttachPerformed\":false,\"hookSet\":\"protocol-send-v2\"}\n",libbpf_version_string(),(unsigned)getuid());
        return 0;
    }
    if(argc!=2 || strcmp(argv[1],"--capture")!=0) { fputs("Use --help.\n",stderr); return 2; }
    const char *caller=getenv("PKEXEC_UID"); char *end=0; errno=0;
    unsigned long uid=caller?strtoul(caller,&end,10):0;
    if(geteuid()!=0 || !caller || !*caller || !end || *end || errno || uid==0 || uid>UINT32_MAX) { fputs("Launch through the installed application's PolicyKit action; a non-root caller UID is required.\n",stderr); return 2; }
    char executable[PATH_MAX], object_path[PATH_MAX]; ssize_t length=readlink("/proc/self/exe",executable,sizeof(executable)-1);
    if(length<=0 || length>=(ssize_t)sizeof(executable)-1) return 2;
    executable[length]=0;
    if(strcmp(executable,"/opt/agent-speed/traffic/agent-speed-traffic-helper")!=0) { fputs("Run the fixed installed helper path only.\n",stderr); return 2; }
    if(!trusted(executable)) { fputs("Installed helper must be root-owned and not writable by group/others.\n",stderr); return 2; }
    char *slash=strrchr(executable,'/'); if(!slash) return 2; *slash=0;
    if(snprintf(object_path,sizeof(object_path),"%s/traffic.bpf.o",executable)>=(int)sizeof(object_path) || !trusted(object_path)) { fputs("Installed BPF object is absent or not root-owned.\n",stderr); return 2; }
    struct rlimit limit={RLIM_INFINITY,RLIM_INFINITY}; (void)setrlimit(RLIMIT_MEMLOCK,&limit);
    struct bpf_object *object=bpf_object__open_file(object_path,0); if(!object || libbpf_get_error(object)) { fputs("Cannot open CO-RE BPF object.\n",stderr); return 2; }
    struct bpf_link *links[8]={0}; int result=2;
    if(bpf_object__load(object)!=0) { fputs("Kernel rejected BPF load (BTF, kernel permissions or lockdown policy). Basic TCP sampling remains available.\n",stderr); goto cleanup; }
    int config=bpf_object__find_map_fd_by_name(object,"config"), counters=bpf_object__find_map_fd_by_name(object,"counters"), lost=bpf_object__find_map_fd_by_name(object,"lost"); uint32_t zero=0, selected=(uint32_t)uid;
    if(config<0||counters<0||lost<0||bpf_map_update_elem(config,&zero,&selected,0)) goto cleanup;
    const char *names[8]={"tcp_send_enter","tcp_send_exit","udp_send_enter","udp_send_exit","udp6_send_enter","udp6_send_exit","recv_enter","recv_exit"};
    const char *symbols[4]={"tcp_sendmsg","udp_sendmsg","udpv6_sendmsg","sock_recvmsg"};
    for(int i=0;i<8;i++) {
        struct bpf_program *program=bpf_object__find_program_by_name(object,names[i]); if(!program) goto cleanup;
        links[i]=bpf_program__attach_kprobe(program,i%2!=0,symbols[i/2]);
        if(!links[i]||libbpf_get_error(links[i])) { links[i]=0; fprintf(stderr,"Kernel probe %s could not be attached. Basic TCP sampling remains available.\n",symbols[i/2]); goto cleanup; }
    }
    struct timespec started;clock_gettime(CLOCK_MONOTONIC,&started); char epoch[80]; snprintf(epoch,sizeof(epoch),"%ld-%ld-%d",started.tv_sec,started.tv_nsec,getpid());
    long hz=sysconf(_SC_CLK_TCK); if(hz<=0) goto cleanup;
    signal(SIGTERM,stop);signal(SIGINT,stop);signal(SIGPIPE,stop);
    setvbuf(stdout,0,_IOLBF,0);
    printf("{\"schema\":1,\"status\":\"ready\",\"hookSet\":\"protocol-send-v2\",\"uid\":%u,\"epoch\":\"%s\",\"lost\":0,\"rows\":[]}\n",selected,epoch);
    while(!stopping) {
        struct pollfd control={.fd=STDIN_FILENO,.events=POLLIN|POLLHUP};
        int ready=poll(&control,1,1000); if(ready<0 && errno!=EINTR) break;
        if(ready>0 && (control.revents&(POLLIN|POLLHUP|POLLERR))) { char byte; if(read(STDIN_FILENO,&byte,1)<=0 || byte=='q') break; }
        if(stopping) break;
        uint64_t lost_count=0;(void)bpf_map_lookup_elem(lost,&zero,&lost_count);
        printf("{\"schema\":1,\"status\":\"sample\",\"uid\":%u,\"epoch\":\"%s\",\"lost\":%llu,\"rows\":[",selected,epoch,(unsigned long long)lost_count);
        struct key key,next; bool have=false, first=true; unsigned count=0;
        struct key remove_later[32768]; unsigned removals=0;
        while(bpf_map_get_next_key(counters,have?&key:0,&next)==0 && count++<32768) {
            key=next;have=true;struct counter value;
            if(bpf_map_lookup_elem(counters,&key,&value)!=0) continue;
            if(!first) putchar(',');
            first=false;
            printf("{\"pid\":%u,\"start\":%llu,\"parent\":%u,\"parentStart\":%llu,\"rx\":%llu,\"tx\":%llu,\"comm\":",key.pid,(unsigned long long)ticks(key.start_ns,hz),value.parent,(unsigned long long)ticks(value.parent_start_ns,hz),(unsigned long long)value.rx,(unsigned long long)value.tx);quoted(value.comm,sizeof(value.comm));putchar('}');
            uint64_t live_start=0; if(stat_start(key.pid,&live_start)||live_start!=ticks(key.start_ns,hz)) remove_later[removals++]=key;
        }
        puts("]}"); if(ferror(stdout)) break;
        for(unsigned i=0;i<removals;i++) (void)bpf_map_delete_elem(counters,&remove_later[i]);
    }
    result=0;
cleanup:
    for(int i=7;i>=0;i--) if(links[i]) bpf_link__destroy(links[i]);
    bpf_object__close(object);return result;
}
