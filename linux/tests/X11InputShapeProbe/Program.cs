using System.Runtime.InteropServices;
using AgentSpeed.Linux;

// Run only inside a dedicated Xephyr/Xvfb display. Synthetic pointer input must never target the user's desktop.
if (Environment.GetEnvironmentVariable("AGENT_SPEED_ISOLATED_X11_PROBE") != "1")
    throw new InvalidOperationException("Run this probe on an isolated X11 display with AGENT_SPEED_ISOLATED_X11_PROBE=1.");
var display = Native.XOpenDisplay(0);
if (display == 0) throw new InvalidOperationException("Cannot open isolated display.");
var root = Native.XDefaultRootWindow(display);
var bottom = Native.XCreateSimpleWindow(display, root, 40, 40, 360, 360, 0, 0, 0x008080);
var top = Native.XCreateSimpleWindow(display, root, 40, 40, 240, 240, 0, 0, 0x101020);
var buffer = Marshal.AllocHGlobal(24 * IntPtr.Size);
// Avalonia also installs a managed handler of a different delegate type. Reproduce that integration boundary.
Native.ExistingErrorHandler existingHandler = (_, _) => 0;
var existingPointer = Marshal.GetFunctionPointerForDelegate(existingHandler);
var originalHandler = Native.XSetErrorHandler(existingPointer);
try
{
    Native.XSelectInput(display, bottom, 1L << 2);
    Native.XSelectInput(display, top, 1L << 2);
    Native.XMapWindow(display, bottom);
    Native.XMapWindow(display, top);
    Native.XSync(display, false);
    AssertClick(top, 80, 80, "normal input reaches the orb");
    Set(true);
    AssertRectangles(0);
    AssertClick(bottom, 80, 80, "empty XShape input region routes clicks to underlying window");
    Native.XUnmapWindow(display, top);
    Native.XMapWindow(display, top);
    Native.XSync(display, false);
    Set(true);
    AssertClick(bottom, 80, 80, "click-through reapplies after unmap/map");
    Set(false);
    AssertRectangles(1);
    AssertClick(top, 80, 80, "disabling click-through restores orb input");
    Native.XResizeWindow(display, top, 320, 320);
    Native.XSync(display, false);
    AssertClick(top, 340, 340, "restored default region follows subsequent window resize");
    if (X11InputShape.TrySet(0, true, out _)) throw new Exception("Zero native handle accepted.");
    if (X11InputShape.TrySet(new nint(0x7fffffff), true, out _)) throw new Exception("Invalid native handle accepted.");
    if (Native.XSetErrorHandler(existingPointer) != existingPointer) throw new Exception("Existing X error handler was not restored.");
    AssertClick(top, 80, 80, "failed native operation leaves existing window interactive");
    Console.WriteLine("PASS X11 click-through, restore, map cycle, resize, safe invalid-window failure, and existing managed handler preservation");
}
finally
{
    Native.XDestroyWindow(display, top);
    Native.XDestroyWindow(display, bottom);
    Native.XCloseDisplay(display);
    Marshal.FreeHGlobal(buffer);
    Native.XSetErrorHandler(originalHandler);
    GC.KeepAlive(existingHandler);
}

void Set(bool through)
{
    if (!X11InputShape.TrySet(top, through, out var error)) throw new Exception(error);
}

void AssertRectangles(int expected)
{
    var rectangles = Native.XShapeGetRectangles(display, top, 2, out var count, out _);
    if (rectangles != 0) Native.XFree(rectangles);
    if (count != expected) throw new Exception($"Expected {expected} input rectangles, received {count}.");
}

void AssertClick(nint expected, int x, int y, string label)
{
    Native.XTestFakeMotionEvent(display, 0, x, y, 0);
    Native.XTestFakeButtonEvent(display, 1, true, 0);
    Native.XTestFakeButtonEvent(display, 1, false, 0);
    Native.XSync(display, false);
    nint received = 0;
    while (Native.XPending(display) > 0)
    {
        Native.XNextEvent(display, buffer);
        if (Marshal.ReadInt32(buffer) == 4) received = Marshal.ReadIntPtr(buffer, 4 * IntPtr.Size);
    }
    if (received != expected) throw new Exception($"{label}: expected XID {expected}, received {received}.");
    Console.WriteLine("PASS " + label);
}

internal static class Native
{
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] public delegate int ExistingErrorHandler(nint display, nint error);
    [DllImport("libX11.so.6")] public static extern nint XSetErrorHandler(nint handler);
    [DllImport("libX11.so.6")] public static extern nint XOpenDisplay(nint name);
    [DllImport("libX11.so.6")] public static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] public static extern nint XDefaultRootWindow(nint display);
    [DllImport("libX11.so.6")] public static extern nint XCreateSimpleWindow(nint display, nint parent, int x, int y,
        uint width, uint height, uint border, nuint borderColor, nuint background);
    [DllImport("libX11.so.6")] public static extern int XSelectInput(nint display, nint window, long mask);
    [DllImport("libX11.so.6")] public static extern int XMapWindow(nint display, nint window);
    [DllImport("libX11.so.6")] public static extern int XUnmapWindow(nint display, nint window);
    [DllImport("libX11.so.6")] public static extern int XResizeWindow(nint display, nint window, uint width, uint height);
    [DllImport("libX11.so.6")] public static extern int XDestroyWindow(nint display, nint window);
    [DllImport("libX11.so.6")] public static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("libX11.so.6")] public static extern int XPending(nint display);
    [DllImport("libX11.so.6")] public static extern int XNextEvent(nint display, nint buffer);
    [DllImport("libX11.so.6")] public static extern int XFree(nint data);
    [DllImport("libXext.so.6")] public static extern nint XShapeGetRectangles(nint display, nint window, int kind,
        out int count, out int ordering);
    [DllImport("libXtst.so.6")] public static extern int XTestFakeMotionEvent(nint display, int screen, int x, int y, nuint delay);
    [DllImport("libXtst.so.6")] public static extern int XTestFakeButtonEvent(nint display, uint button,
        [MarshalAs(UnmanagedType.Bool)] bool pressed, nuint delay);
}
