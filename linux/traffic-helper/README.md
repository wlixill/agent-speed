# Optional Linux socket traffic event helper

The ordinary application continues to use unprivileged TCP_INFO sampling. This optional component is started only by the explicit Enhanced monitoring button and PolicyKit authentication. No daemon, boot unit, setuid bit or file capabilities are installed. Closing monitoring or the application closes the helper's stdin, unloads the BPF links/maps and exits the root process.

The helper loads a small CO-RE BPF object with a statically linked, pinned libbpf 1.4.7. Ubuntu 22.04's libbpf 0.5 cannot parse ENUM64 in current HWE kernel BTF; no host library is replaced. It attaches entry/return probes to `tcp_sendmsg`, `udp_sendmsg`, `udpv6_sendmsg` and `sock_recvmsg`; for the authenticated caller UID, IPv4/IPv6 stream/datagram sockets contribute successful return bytes. It retains cumulative counters until userspace reads them, so ordinary UDP and sockets/processes that close between polls are visible. MSG_PEEK does not count bytes twice. Hook-specific in-flight keys and IPv4/IPv6 family checks prevent mapped-IPv4 UDP delegation from counting a send twice. The signed 32-bit return value is checked before incrementing, so -errno cannot be mistaken for gigabytes sent. No content, endpoint address/port, file contents, command line, credentials or other user's counters are exported. GUI process classification continues to require recognized executable/script identity and unique open-transcript evidence for a session; exited roots cannot reuse stale transcript evidence.

This is **application socket I/O observation**, not complete wire traffic. Protocol headers, retransmissions, discarded data and paths bypassing these hooks are outside the domain. sendfile/splice/io_uring paths are not exhaustively verified. A full map, probe read error or unsupported kernel reports unavailable/partial state; the existing basic sampler remains available. Old TCP-only histories remain observed/mixed intervals rather than being relabelled as complete TCP/UDP history. Traffic is not put into the external report schema, which cannot express these coverage limits.

## Build/package

```sh
bash linux/traffic-helper/build.sh artifacts/traffic-helper
```

Build dependencies: `clang-14`, `gcc`, `make`, `pkg-config`, `libelf-dev`, `zlib1g-dev`, distribution Linux UAPI headers; no BCC runtime or full kernel headers. Targets Ubuntu 22.04 x64 kernels exposing `/sys/kernel/btf/vmlinux` and permitting kprobe BPF attachment.

Install root-owned, non-writable-by-users:

- `agent-speed-traffic-helper` (0755) and `traffic.bpf.o` (0644) under `/opt/agent-speed/traffic/`.
- `org.agentspeed.traffic.policy` under `/usr/share/polkit-1/actions/` (0644).
- Runtime dependencies: `libelf1`, `zlib1g`, `policykit-1`; no distribution libbpf runtime dependency.
- Copy `vendor/NOTICE`, `vendor/LIBBPF-LICENSE`, `vendor/SHA256SUMS` and `vendor/libbpf-v1.4.7.tar.gz` into the package documentation. The static dependency is distributed under its BSD-2-Clause option with its unchanged source archive.

The build checks the vendored archive SHA-256, uses local source only, and runs the unprivileged `--preflight` check. This reads/parses kernel BTF without loading or attaching any BPF program; it can also be run directly before authorization.

The helper accepts only `--capture`, derives the UID from `PKEXEC_UID`, and refuses another location, a root caller, or untrusted helper/object ownership. It never accepts arbitrary load paths or another capture UID.

After installation, explicitly authenticated real-kernel verification can be run on the real desktop (outside a PID namespace):

```sh
python3 linux/scripts/traffic-enhanced-smoke.py --output artifacts/traffic-enhanced-runtime
```

This tests loopback TCP4/TCP6, 100 UDP sockets closed before a poll, MSG_PEEK, a child exiting before a poll, exact application-byte totals, no payload/address fields, and stdin-EOF shutdown. It can show an administrator authentication dialog. It sends no external traffic.

Reference: Linux kernel [libbpf and CO-RE lifecycle](https://docs.kernel.org/bpf/libbpf/libbpf_overview.html).
