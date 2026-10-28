// SPDX-License-Identifier: MIT
// IPv4/IPv6 application socket I/O counters; no payload, address, port or command-line inspection.
#include <linux/bpf.h>
#include <asm/ptrace.h>
#define SEC(name) __attribute__((section(name), used))
#define __uint(name, value) int (*name)[value]
#define __type(name, value) value *name
#define CORE __attribute__((preserve_access_index))
struct sock_common { unsigned short skc_family; } CORE;
struct sock { struct sock_common __sk_common; } CORE;
struct socket { struct sock *sk; short type; } CORE;
struct task_struct { struct task_struct *group_leader; struct task_struct *real_parent; unsigned long long start_boottime; int tgid; char comm[16]; } CORE;
static void *(*lookup)(void *, const void *) = (void *)BPF_FUNC_map_lookup_elem;
static long (*update)(void *, const void *, const void *, __u64) = (void *)BPF_FUNC_map_update_elem;
static long (*remove_key)(void *, const void *) = (void *)BPF_FUNC_map_delete_elem;
static __u64 (*pid_tid)(void) = (void *)BPF_FUNC_get_current_pid_tgid;
static __u64 (*uid_gid)(void) = (void *)BPF_FUNC_get_current_uid_gid;
static long (*read_kernel)(void *, __u32, const void *) = (void *)BPF_FUNC_probe_read_kernel;
static void *(*current_task)(void) = (void *)BPF_FUNC_get_current_task;
#define READ(dst, src) read_kernel(&(dst), sizeof(dst), __builtin_preserve_access_index(&(src)))
struct key { __u32 pid; __u32 pad; __u64 start_ns; };
struct counter { __u64 rx, tx; __u32 parent; __u32 pad; __u64 parent_start_ns; char comm[16]; };
struct call_key { __u64 tid; __u32 hook; __u32 pad; };
struct call { struct key key; int receive; int pad; };
struct { __uint(type, BPF_MAP_TYPE_ARRAY); __uint(max_entries, 1); __type(key, __u32); __type(value, __u32); } config SEC(".maps");
struct { __uint(type, BPF_MAP_TYPE_HASH); __uint(max_entries, 32768); __type(key, struct key); __type(value, struct counter); } counters SEC(".maps");
struct { __uint(type, BPF_MAP_TYPE_HASH); __uint(max_entries, 16384); __type(key, struct call_key); __type(value, struct call); } inflight SEC(".maps");
struct { __uint(type, BPF_MAP_TYPE_ARRAY); __uint(max_entries, 1); __type(key, __u32); __type(value, __u64); } lost SEC(".maps");
static __always_inline void loss(void) { __u32 zero = 0; __u64 *value = lookup(&lost, &zero); if (value) __sync_fetch_and_add(value, 1); }
static __always_inline int begin(struct pt_regs *ctx, int receive, int family_filter, __u32 hook) {
    __u32 zero = 0, uid = (__u32)uid_gid(), *selected = lookup(&config, &zero);
    if (!selected || uid != *selected) return 0;
    // recvmsg(MSG_PEEK) observes the same data without consuming it; never count it twice.
    if (receive && (ctx->rdx & 2)) return 0;
    struct sock *sk = (void *)ctx->rdi;
    unsigned short family = 0;
    if (receive) {
        struct socket *socket = (void *)ctx->rdi; short type = 0;
        if (READ(sk, socket->sk) || !sk || READ(type, socket->type)) { loss(); return 0; }
        if (type != 1 && type != 2) return 0;
    }
    if (!sk || READ(family, sk->__sk_common.skc_family)) { loss(); return 0; }
    if (family != 2 && family != 10) return 0;
    // udpv6_sendmsg delegates mapped IPv4 to udp_sendmsg. Only its outer AF_INET6 hook counts it.
    if (family_filter && family != family_filter) return 0;
    struct task_struct *task = current_task(), *leader = 0, *parent = 0, *parent_leader = 0;
    __u64 start = 0, parent_start = 0, id = pid_tid();
    if (READ(leader, task->group_leader) || !leader || READ(start, leader->start_boottime)) { loss(); return 0; }
    struct key key = { .pid = id >> 32, .start_ns = start };
    if (!lookup(&counters, &key)) {
        struct counter fresh = {};
        // Parent PID comes from the task's real-parent tgid (CO-RE resolves layout below).
        if (!READ(parent, leader->real_parent) && parent && !READ(parent_leader, parent->group_leader) && parent_leader) {
            READ(parent_start, parent_leader->start_boottime); fresh.parent_start_ns = parent_start; READ(fresh.parent, parent_leader->tgid);
        }
        READ(fresh.comm, leader->comm);
        if (update(&counters, &key, &fresh, BPF_NOEXIST) && !lookup(&counters, &key)) { loss(); return 0; }
    }
    struct call call = { .key = key, .receive = receive };
    struct call_key call_key = { .tid = id, .hook = hook };
    if (update(&inflight, &call_key, &call, BPF_ANY)) loss();
    return 0;
}
static __always_inline int finish(struct pt_regs *ctx, __u32 hook) {
    struct call_key call_key = { .tid = pid_tid(), .hook = hook };
    struct call *call = lookup(&inflight, &call_key);
    if (!call) return 0;
    // All attached kernel functions return int; x86 EAX zero extension must not turn -errno into +4 GiB.
    int bytes = (int)ctx->rax;
    if (bytes > 0) {
        struct counter *counter = lookup(&counters, &call->key);
        if (counter) { if (call->receive) __sync_fetch_and_add(&counter->rx, bytes); else __sync_fetch_and_add(&counter->tx, bytes); }
        else loss();
    }
    remove_key(&inflight, &call_key); return 0;
}
SEC("kprobe/tcp_sendmsg") int tcp_send_enter(struct pt_regs *ctx) { return begin(ctx, 0, 0, 1); }
SEC("kretprobe/tcp_sendmsg") int tcp_send_exit(struct pt_regs *ctx) { return finish(ctx, 1); }
SEC("kprobe/udp_sendmsg") int udp_send_enter(struct pt_regs *ctx) { return begin(ctx, 0, 2, 2); }
SEC("kretprobe/udp_sendmsg") int udp_send_exit(struct pt_regs *ctx) { return finish(ctx, 2); }
SEC("kprobe/udpv6_sendmsg") int udp6_send_enter(struct pt_regs *ctx) { return begin(ctx, 0, 10, 3); }
SEC("kretprobe/udpv6_sendmsg") int udp6_send_exit(struct pt_regs *ctx) { return finish(ctx, 3); }
SEC("kprobe/sock_recvmsg") int recv_enter(struct pt_regs *ctx) { return begin(ctx, 1, 0, 4); }
SEC("kretprobe/sock_recvmsg") int recv_exit(struct pt_regs *ctx) { return finish(ctx, 4); }
char LICENSE[] SEC("license") = "GPL";
