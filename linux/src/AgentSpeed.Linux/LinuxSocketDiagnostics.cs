using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace AgentSpeed.Linux;

/// <summary>TCP acknowledged/received counters; inode plus kernel cookie prevents reuse from becoming traffic.</summary>
public sealed record LinuxTcpCounter(ulong Inode, ulong Cookie, long Rx, long Tx);

/// <summary>Linux UAPI SOCK_DIAG_BY_FAMILY / INET_DIAG_INFO, requiring no helper or root daemon.</summary>
public sealed class LinuxSocketDiagnostics
{
    private const int Netlink = 16, SockDiag = 4, SolSocket = 1, ReceiveTimeout = 20;
    private static readonly uint Uid = Native.geteuid();

    public IReadOnlyList<LinuxTcpCounter> Read()
    {
        if (!OperatingSystem.IsLinux() || !BitConverter.IsLittleEndian)
            throw new PlatformNotSupportedException("TCP 进程采样需要 Linux 小端平台。");
        var result = new List<LinuxTcpCounter>();
        ReadFamily(2, 1, result);
        ReadFamily(10, 2, result);
        return result;
    }

    private static void ReadFamily(byte family, uint sequence, List<LinuxTcpCounter> output)
    {
        var fd = Native.socket(Netlink, 2 | 0x80000, SockDiag);
        if (fd < 0) throw Failure("无法创建 TCP 诊断套接字");
        try
        {
            var address = new byte[12];
            BinaryPrimitives.WriteUInt16LittleEndian(address, Netlink);
            if (Native.bind(fd, address, (uint)address.Length) != 0) throw Failure("无法绑定 TCP 诊断套接字");
            var timeout = new TimeValue { Seconds = 0, Microseconds = 400_000 };
            if (Native.setsockopt(fd, SolSocket, ReceiveTimeout, ref timeout, (uint)Marshal.SizeOf<TimeValue>()) != 0)
                throw Failure("无法设置 TCP 诊断超时");
            var request = new byte[72];
            BinaryPrimitives.WriteUInt32LittleEndian(request, (uint)request.Length);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(4), 20);
            BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(6), 0x301);
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(8), sequence);
            request[16] = family; request[17] = 6; request[18] = 2; // TCP, INET_DIAG_INFO.
            BinaryPrimitives.WriteUInt32LittleEndian(request.AsSpan(20), 0xfff);
            BinaryPrimitives.WriteUInt64LittleEndian(request.AsSpan(64), ulong.MaxValue);
            if (Native.sendto(fd, request, (nuint)request.Length, 0, address, (uint)address.Length) != request.Length)
                throw Failure("无法请求 TCP 诊断数据");
            var buffer = new byte[65536];
            for (var i = 0; i < 256; i++)
            {
                var count = Native.recv(fd, buffer, (nuint)buffer.Length, 0x20);
                if (count <= 0) throw Failure("TCP 诊断采样未完整返回");
                if (count > buffer.Length) throw new IOException("TCP 诊断数据超过单条消息限制。");
                var parsed = Parse(buffer.AsSpan(0, checked((int)count)), sequence, Uid);
                output.AddRange(parsed.Rows);
                if (parsed.Done) return;
            }
            throw new IOException("TCP 诊断数据超过采样上限。");
        }
        finally { Native.close(fd); }
    }

    /// <summary>Validate lengths before reading kernel-version-dependent TCP_INFO. Addresses and ports are never retained.</summary>
    public static (IReadOnlyList<LinuxTcpCounter> Rows, bool Done) Parse(ReadOnlySpan<byte> data, uint sequence, uint uid)
    {
        var rows = new List<LinuxTcpCounter>();
        var offset = 0;
        while (offset < data.Length)
        {
            if (data.Length - offset < 16) throw new IOException("TCP 诊断消息头不完整。");
            var message = data[offset..];
            var length = BinaryPrimitives.ReadUInt32LittleEndian(message);
            if (length < 16 || length > message.Length) throw new IOException("TCP 诊断消息长度无效。");
            var flags = BinaryPrimitives.ReadUInt16LittleEndian(message[6..]);
            if ((flags & 0x10) != 0) throw new IOException("TCP 诊断枚举被内核中断。");
            if (BinaryPrimitives.ReadUInt32LittleEndian(message[8..]) == sequence)
            {
                var type = BinaryPrimitives.ReadUInt16LittleEndian(message[4..]);
                var body = message.Slice(16, checked((int)length) - 16);
                if (type == 3) return (rows, true);
                if (type == 2)
                {
                    if (body.Length < 4) throw new IOException("TCP 诊断错误消息不完整。");
                    var code = BinaryPrimitives.ReadInt32LittleEndian(body);
                    if (code != 0) throw new IOException("TCP 诊断被内核拒绝，错误码 " + -code);
                }
                else if (type == 20)
                {
                    if (body.Length < 72) throw new IOException("TCP 诊断记录不完整。");
                    if (BinaryPrimitives.ReadUInt32LittleEndian(body[64..]) == uid)
                    {
                        var inode = BinaryPrimitives.ReadUInt32LittleEndian(body[68..]);
                        var cookie = BinaryPrimitives.ReadUInt64LittleEndian(body[44..]);
                        var attributes = body[72..];
                        while (attributes.Length >= 4)
                        {
                            var attributeLength = BinaryPrimitives.ReadUInt16LittleEndian(attributes);
                            if (attributeLength < 4 || attributeLength > attributes.Length) throw new IOException("TCP_INFO 属性长度无效。");
                            var attributeType = BinaryPrimitives.ReadUInt16LittleEndian(attributes[2..]) & 0x3fff;
                            if (attributeType == 2 && attributeLength >= 140 && inode != 0)
                            {
                                var info = attributes.Slice(4, attributeLength - 4);
                                var tx = BinaryPrimitives.ReadUInt64LittleEndian(info[120..]);
                                var rx = BinaryPrimitives.ReadUInt64LittleEndian(info[128..]);
                                if (rx <= long.MaxValue && tx <= long.MaxValue) rows.Add(new(inode, cookie, (long)rx, (long)tx));
                            }
                            var aligned = (attributeLength + 3) & ~3;
                            if (aligned > attributes.Length) break;
                            attributes = attributes[aligned..];
                        }
                    }
                }
            }
            var next = checked(((int)length + 3) & ~3);
            if (next > data.Length - offset) break;
            offset += next;
        }
        return (rows, false);
    }

    private static IOException Failure(string text) => new(text + "：" + new Win32Exception(Marshal.GetLastPInvokeError()).Message);
    [StructLayout(LayoutKind.Sequential)]
    private struct TimeValue { public long Seconds; public long Microseconds; }
    private static class Native
    {
        [DllImport("libc", SetLastError = true)] internal static extern int socket(int domain, int type, int protocol);
        [DllImport("libc", SetLastError = true)] internal static extern int bind(int fd, byte[] address, uint length);
        [DllImport("libc", SetLastError = true)] internal static extern int setsockopt(int fd, int level, int option, ref TimeValue value, uint length);
        [DllImport("libc", SetLastError = true)] internal static extern nint sendto(int fd, byte[] data, nuint length, int flags, byte[] address, uint addressLength);
        [DllImport("libc", SetLastError = true)] internal static extern nint recv(int fd, byte[] data, nuint length, int flags);
        [DllImport("libc")] internal static extern int close(int fd);
        [DllImport("libc")] internal static extern uint geteuid();
    }
}
