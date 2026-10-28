#!/usr/bin/env bash
set -euo pipefail
root="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")" && pwd)"
output="${1:-$root/../../artifacts/traffic-helper}"
mkdir -p "$output"
output="$(realpath -- "$output")"
(cd -- "$root/vendor" && sha256sum --check SHA256SUMS)
work="$(mktemp -d "$output/libbpf-build.XXXXXX")"
tar -xzf "$root/vendor/libbpf-v1.4.7.tar.gz" -C "$work"
mkdir -p "$work/objects"
make -s -C "$work/libbpf-1.4.7/src" -j2 BUILD_STATIC_ONLY=1 OBJDIR="$work/objects" INCLUDEDIR="$work/include" \
    CFLAGS='-O2 -Werror -Wall -std=gnu89' all install_headers
compiler="${CLANG:-clang-14}"
"$compiler" -O2 -g -gdwarf-4 -target bpf -D__TARGET_ARCH_x86 -I/usr/include/x86_64-linux-gnu -Wall -Werror \
    -c "$root/traffic.bpf.c" -o "$output/traffic.bpf.o"
gcc -O2 -std=c11 -D_GNU_SOURCE -Wall -Wextra -Werror -Wl,-z,relro,-z,now \
    -I"$work/include" "$root/traffic-helper.c" "$work/objects/libbpf.a" -lelf -lz -o "$output/agent-speed-traffic-helper"
cp "$root/vendor/NOTICE" "$output/libbpf-NOTICE"
cp "$root/vendor/LIBBPF-LICENSE" "$output/libbpf-LICENSE"
if [ -r /sys/kernel/btf/vmlinux ]; then
    "$output/agent-speed-traffic-helper" --preflight
else
    echo "Kernel BTF is unavailable on the build host; native compilation completed, runtime preflight is deferred."
fi
