#!/usr/bin/env python3
"""Explicitly authorize the installed optional helper, then verify local TCP/UDP, MSG_PEEK and closed sockets.

Run from the real desktop/user PID namespace (not inside the restricted command sandbox).
This launches pkexec and may display a system administrator authentication dialog.
Only loopback sockets created by this script are used. No external traffic is generated.
"""
import argparse
import json
import os
from pathlib import Path
import selectors
import socket
import subprocess
import sys
import threading
import time


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--output', type=Path, required=True)
    parser.add_argument('--authentication-timeout', type=int, default=120,
                        help='Seconds to wait for the visible system authentication prompt (30-600)')
    args = parser.parse_args()
    assert 30 <= args.authentication_timeout <= 600
    helper = Path('/opt/agent-speed/traffic/agent-speed-traffic-helper')
    assert helper.is_file(), 'Install the newly packaged deb before this explicit privileged runtime check'
    args.output.mkdir(parents=True, exist_ok=True)
    ns = next(line for line in Path('/proc/self/status').read_text().splitlines() if line.startswith('NSpid:'))
    ids = ns.split()[1:]
    assert len(ids) == 1, 'Run outside the tool PID sandbox so kernel event PIDs match fixture PIDs'
    stderr = (args.output/'helper-stderr.log').open('w')
    process = subprocess.Popen(['/usr/bin/pkexec', str(helper), '--capture'], stdin=subprocess.PIPE,
                               stdout=subprocess.PIPE, stderr=stderr, text=True, bufsize=1)
    select = selectors.DefaultSelector()
    select.register(process.stdout, selectors.EVENT_READ)

    def read(timeout):
        if not select.select(timeout):
            if process.poll() is not None:
                stderr.flush()
                raise AssertionError('Helper exited: '+(args.output/'helper-stderr.log').read_text())
            raise TimeoutError('Waiting for helper output or administrator authentication')
        line = process.stdout.readline()
        assert line, 'Helper closed stdout before producing a valid sample'
        result = json.loads(line)
        assert result['schema'] == 1 and result['uid'] == os.getuid()
        return result

    try:
        print('Waiting for system authentication for Agent Speed local TCP/UDP validation; no external traffic is generated.', flush=True)
        ready = read(args.authentication_timeout)
        assert ready['status'] == 'ready'
        assert ready.get('hookSet') == 'protocol-send-v2', 'Install the protocol-send-v2 helper before this regression check'
        print('PASS administrator-authorized helper is ready for current UID only', flush=True)
        main_pid = os.getpid()
        expected_rx = 0
        expected_tx = 0
        assertions = ['helper-current-uid']

        def tcp(family, method="sendall"):
            nonlocal expected_rx, expected_tx
            address = '127.0.0.1' if family == socket.AF_INET else '::1'
            listener = socket.socket(family, socket.SOCK_STREAM)
            listener.bind((address, 0))
            listener.listen(1)
            payload = b'x'*120000
            received = []

            def receive():
                peer, _ = listener.accept()
                with peer:
                    size = 0
                    while size < len(payload):
                        data = peer.recv(32768)
                        assert data
                        size += len(data)
                    received.append(size)

            thread = threading.Thread(target=receive)
            thread.start()
            with socket.socket(family, socket.SOCK_STREAM) as client:
                client.connect(listener.getsockname())
                if method == "sendall":
                    client.sendall(payload)
                else:
                    offset = 0
                    while offset < len(payload):
                        offset += client.sendmsg([payload[offset:]]) if method == "sendmsg" else os.write(client.fileno(), payload[offset:])
            thread.join(timeout=5)
            listener.close()
            assert received == [len(payload)] and not thread.is_alive()
            expected_rx += len(payload)
            expected_tx += len(payload)

        for method in ["sendall", "sendmsg", "write"]:
            tcp(socket.AF_INET, method)
            assertions.append('tcp4-'+method+'-closed-before-poll')
        ipv6 = True
        try:
            for method in ["sendall", "sendmsg", "write"]:
                tcp(socket.AF_INET6, method)
                assertions.append('tcp6-'+method+'-closed-before-poll')
        except OSError as error:
            ipv6 = False
            print('IPv6 loopback unavailable:', error, flush=True)

        udp = socket.socket(socket.AF_INET, socket.SOCK_DGRAM)
        udp.bind(('127.0.0.1', 0))
        for index in range(100):
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
                assert sender.sendto(b'u'*321, udp.getsockname()) == 321
            if index == 0:
                assert len(udp.recvfrom(4096, socket.MSG_PEEK)[0]) == 321
            assert len(udp.recvfrom(4096)[0]) == 321
        expected_rx += 32100
        expected_tx += 32100
        assertions.extend(['udp4-100-short-sockets', 'msg-peek-not-double-counted'])
        # These syscalls reach protocol hooks but fail; signed -errno must not add gigabytes.
        udp.setblocking(False)
        try:
            udp.recvfrom(4096)
            raise AssertionError('An empty nonblocking receive unexpectedly succeeded')
        except BlockingIOError:
            pass
        finally:
            udp.setblocking(True)
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
            try:
                sender.sendto(b'not-counted', ('127.0.0.1', 0))
                raise AssertionError('Sending UDP to destination port zero unexpectedly succeeded')
            except OSError:
                pass
        assertions.append('negative-errno-is-not-positive-bytes')
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as sender:
            sender.connect(udp.getsockname())
            assert sender.sendmsg([b'm'*111, b'n'*333]) == 444
            assert len(udp.recvfrom(4096)[0]) == 444
            assert os.write(sender.fileno(), b'w'*222) == 222
            assert len(udp.recvfrom(4096)[0]) == 222
        expected_rx += 666
        expected_tx += 666
        assertions.append('udp4-sendmsg-and-write')
        if ipv6:
            with socket.socket(socket.AF_INET6, socket.SOCK_DGRAM) as receiver:
                receiver.bind(('::1', 0))
                with socket.socket(socket.AF_INET6, socket.SOCK_DGRAM) as sender:
                    assert sender.sendto(b'6'*333, receiver.getsockname()) == 333
                assert len(receiver.recvfrom(4096)[0]) == 333
            expected_rx += 333
            expected_tx += 333
            assertions.append('udp6-short-socket')
            with socket.socket(socket.AF_INET6, socket.SOCK_DGRAM) as sender:
                sender.setsockopt(socket.IPPROTO_IPV6, socket.IPV6_V6ONLY, 0)
                assert sender.sendto(b'v'*555, ('::ffff:127.0.0.1', udp.getsockname()[1])) == 555
            assert len(udp.recvfrom(4096)[0]) == 555
            expected_rx += 555
            expected_tx += 555
            assertions.append('udp6-mapped-v4-counted-once')
        child = subprocess.Popen([sys.executable, '-c',
            'import socket,sys;s=socket.socket(socket.AF_INET,socket.SOCK_DGRAM);s.sendto(b"c"*789,("127.0.0.1",int(sys.argv[1])));s.close()',
            str(udp.getsockname()[1])])
        child.wait(timeout=5)
        assert child.returncode == 0 and len(udp.recvfrom(4096)[0]) == 789
        udp.close()
        expected_rx += 789
        assertions.append('exited-child-udp-preserved')
        rows = {}
        deadline = time.monotonic()+8
        while time.monotonic() < deadline:
            sample = read(3)
            assert sample['lost'] == 0, sample
            rows.update({row['pid']: row for row in sample['rows']})
            if main_pid in rows and child.pid in rows and rows[main_pid]['rx'] >= expected_rx:
                break
        (args.output/'observed.json').write_text(json.dumps({'mainExpected': {'rx': expected_rx, 'tx': expected_tx},
            'mainActual': rows.get(main_pid), 'shortChildActual': rows.get(child.pid)}, indent=2))
        assert rows[main_pid]['rx'] == expected_rx, (rows.get(main_pid), expected_rx)
        assert rows[main_pid]['tx'] == expected_tx, (rows.get(main_pid), expected_tx)
        assert rows[child.pid]['tx'] == 789 and rows[child.pid]['rx'] == 0, rows.get(child.pid)
        assert all(set(row) == {'pid','start','parent','parentStart','rx','tx','comm'} for row in rows.values())
        process.stdin.close()
        process.wait(timeout=5)
        assert process.returncode == 0
        assertions.extend(['exact-application-byte-totals', 'no-payload-endpoint-fields', 'stdin-eof-stops-helper'])
        (args.output/'result.json').write_text(json.dumps({'checks': assertions, 'uid': os.getuid(), 'ipv6': ipv6,
            'mainExpected': {'rx': expected_rx, 'tx': expected_tx}, 'mainActual': rows[main_pid],
            'shortChildActual': rows[child.pid], 'helperExit': process.returncode}, indent=2))
        print('PASS enhanced TCP/UDP runtime:', len(assertions), 'checks; exact loopback bytes; helper exited cleanly', flush=True)
    finally:
        if process.poll() is None:
            try:
                process.stdin.close()
            except (BrokenPipeError, ValueError):
                pass
            try:
                process.wait(timeout=5)
            except subprocess.TimeoutExpired:
                print('Helper did not stop after stdin EOF; root process may require administrator cleanup.', file=sys.stderr)
        stderr.close()
        select.close()


if __name__ == '__main__':
    try:
        main()
    except Exception:
        import traceback
        if '--output' in sys.argv:
            target = Path(sys.argv[sys.argv.index('--output') + 1])
            target.mkdir(parents=True, exist_ok=True)
            (target/'failure.txt').write_text(traceback.format_exc())
        raise
