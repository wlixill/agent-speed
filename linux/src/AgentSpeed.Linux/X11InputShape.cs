using System.Runtime.InteropServices;
using System.Reflection.Emit;

namespace AgentSpeed.Linux;

/// <summary>Changes only the native X11 input region; visual transparency and hit testing remain independent.</summary>
internal static class X11InputShape
{
    private const int ShapeInput = 2;
    private const int ShapeSet = 0;
    private static readonly object ErrorHandlerLock = new();

    public static bool TrySet(nint window, bool clickThrough, out string? error)
    {
        error = null;
        if (window == 0) { error = "悬浮窗的 X11 窗口尚未就绪。"; return false; }
        nint display = 0;
        try
        {
            display = XOpenDisplay(0);
            if (display == 0) { error = "无法连接 X11 显示服务。"; return false; }
            if (!XShapeQueryExtension(display, out _, out _) || !XShapeQueryVersion(display, out var major, out var minor)
                || major < 1 || (major == 1 && minor < 1))
            {
                error = "当前 X11 显示服务不支持输入区域穿透。";
                return false;
            }

            // X errors arrive asynchronously. Trap errors on our own connection so a disappearing native
            // window cannot invoke Xlib's process-exiting default handler; leave Avalonia's connection alone.
            lock (ErrorHandlerLock)
            {
                XSync(display, false);
                byte nativeError = 0;
                XErrorHandler? previousHandler = null;
                XErrorHandler handler = (nint connection, ref XErrorEvent value) =>
                {
                    if (connection == display) { nativeError = value.ErrorCode; return 0; }
                    return previousHandler?.Invoke(connection, ref value) ?? 0;
                };
                var previous = XSetErrorHandler(Marshal.GetFunctionPointerForDelegate(handler));
                try
                {
                    // Avalonia installs a managed delegate with its own delegate type. Marshal may return that
                    // original delegate, which cannot be cast to ours even though the native signatures match.
                    if (previous != 0) previousHandler = NativeForwarder(previous);
                    SetRegion(display, window, clickThrough);
                    XSync(display, false);
                    var rectangles = XShapeGetRectangles(display, window, ShapeInput, out var count, out _);
                    if (rectangles != 0) XFree(rectangles);
                    XSync(display, false);
                    if (nativeError != 0 || (clickThrough ? count != 0 : count == 0))
                    {
                        // Never leave a failed enable operation with an empty input region.
                        SetRegion(display, window, false);
                        XSync(display, false);
                        error = nativeError != 0 ? "X11 输入区域设置失败（错误 " + nativeError + "）。"
                            : "X11 未确认悬浮窗输入区域，已尝试恢复鼠标操作。";
                        return false;
                    }
                    return true;
                }
                finally
                {
                    XSetErrorHandler(previous);
                    GC.KeepAlive(handler);
                    GC.KeepAlive(previousHandler);
                }
            }
        }
        catch (Exception exception) when (exception is DllNotFoundException or EntryPointNotFoundException)
        {
            error = "缺少 X11 输入区域支持：" + exception.Message;
            return false;
        }
        finally { if (display != 0) XCloseDisplay(display); }
    }

    private static void SetRegion(nint display, nint window, bool clickThrough)
    {
        if (clickThrough)
            XShapeCombineRectangles(display, window, ShapeInput, 0, 0, 0, 0, ShapeSet, 0);
        else
            // A None mask restores the server's default region, which tracks subsequent window resizing.
            XShapeCombineMask(display, window, ShapeInput, 0, 0, 0, ShapeSet);
    }

    private static XErrorHandler NativeForwarder(nint address)
    {
        var parameters = new[] { typeof(nint), typeof(XErrorEvent).MakeByRefType() };
        var method = new DynamicMethod("ForwardExistingXErrorHandler", typeof(int), parameters, typeof(X11InputShape).Module, true);
        var il = method.GetILGenerator();
        il.Emit(OpCodes.Ldarg_0);
        il.Emit(OpCodes.Ldarg_1);
        il.Emit(OpCodes.Ldc_I8, address.ToInt64());
        il.Emit(OpCodes.Conv_I);
        il.EmitCalli(OpCodes.Calli, CallingConvention.Cdecl, typeof(int), parameters);
        il.Emit(OpCodes.Ret);
        return method.CreateDelegate<XErrorHandler>();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct XErrorEvent
    {
        public int Type;
        public nint Display;
        public nuint ResourceId;
        public nuint Serial;
        public byte ErrorCode;
        public byte RequestCode;
        public byte MinorCode;
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int XErrorHandler(nint display, ref XErrorEvent error);

    [DllImport("libX11.so.6")] private static extern nint XOpenDisplay(nint displayName);
    [DllImport("libX11.so.6")] private static extern int XCloseDisplay(nint display);
    [DllImport("libX11.so.6")] private static extern int XSync(nint display, [MarshalAs(UnmanagedType.Bool)] bool discard);
    [DllImport("libX11.so.6")] private static extern int XFree(nint data);
    [DllImport("libX11.so.6")] private static extern nint XSetErrorHandler(nint handler);
    [DllImport("libXext.so.6")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XShapeQueryExtension(nint display, out int eventBase, out int errorBase);
    [DllImport("libXext.so.6")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool XShapeQueryVersion(nint display, out int major, out int minor);
    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineRectangles(nint display, nint window, int kind, int x, int y,
        nint rectangles, int count, int operation, int ordering);
    [DllImport("libXext.so.6")]
    private static extern void XShapeCombineMask(nint display, nint window, int kind, int x, int y, nint source, int operation);
    [DllImport("libXext.so.6")]
    private static extern nint XShapeGetRectangles(nint display, nint window, int kind, out int count, out int ordering);
}
