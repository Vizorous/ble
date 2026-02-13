using System.Diagnostics;
using System.Runtime.InteropServices;

namespace BleEdgeSender;

internal readonly record struct MouseMoveEvent(int X, int Y, bool IsInjected);
internal readonly record struct MouseButtonEvent(MouseButtonId Button, bool IsDown);
internal readonly record struct KeyEvent(int VirtualKey, bool IsDown);

internal sealed class InputCapture : IDisposable
{
    private const int WhKeyboardLl = 13;
    private const int WhMouseLl = 14;

    private const int WmMouseMove = 0x0200;
    private const int WmLButtonDown = 0x0201;
    private const int WmLButtonUp = 0x0202;
    private const int WmRButtonDown = 0x0204;
    private const int WmRButtonUp = 0x0205;
    private const int WmMButtonDown = 0x0207;
    private const int WmMButtonUp = 0x0208;
    private const int WmMouseWheel = 0x020A;
    private const uint LlmhfInjected = 0x00000001;

    private const int WmKeyDown = 0x0100;
    private const int WmKeyUp = 0x0101;
    private const int WmSysKeyDown = 0x0104;
    private const int WmSysKeyUp = 0x0105;

    private const int WmQuit = 0x0012;

    private readonly Func<MouseMoveEvent, bool> _onMouseMove;
    private readonly Func<MouseButtonEvent, bool> _onMouseButton;
    private readonly Func<short, bool> _onMouseWheel;
    private readonly Func<KeyEvent, bool> _onKey;

    private readonly AutoResetEvent _threadReady = new(false);

    private Thread? _hookThread;
    private uint _hookThreadId;
    private Exception? _startupError;

    private IntPtr _keyboardHook = IntPtr.Zero;
    private IntPtr _mouseHook = IntPtr.Zero;

    private HookProc? _keyboardProc;
    private HookProc? _mouseProc;

    public InputCapture(
        Func<MouseMoveEvent, bool> onMouseMove,
        Func<MouseButtonEvent, bool> onMouseButton,
        Func<short, bool> onMouseWheel,
        Func<KeyEvent, bool> onKey)
    {
        _onMouseMove = onMouseMove;
        _onMouseButton = onMouseButton;
        _onMouseWheel = onMouseWheel;
        _onKey = onKey;
    }

    public void Start()
    {
        if (_hookThread != null)
        {
            return;
        }

        _startupError = null;
        _hookThread = new Thread(HookThreadMain)
        {
            IsBackground = true,
            Name = "InputCaptureHooks",
        };

        _hookThread.Start();
        _threadReady.WaitOne();
        if (_startupError != null)
        {
            throw new InvalidOperationException("Unable to start input hooks.", _startupError);
        }
    }

    public void Stop()
    {
        if (_hookThread == null)
        {
            return;
        }

        PostThreadMessage(_hookThreadId, WmQuit, IntPtr.Zero, IntPtr.Zero);
        _hookThread.Join(TimeSpan.FromSeconds(2));

        _hookThread = null;
        _hookThreadId = 0;
    }

    private void HookThreadMain()
    {
        try
        {
            _hookThreadId = GetCurrentThreadId();

            _keyboardProc = KeyboardHook;
            _mouseProc = MouseHook;

            using var process = Process.GetCurrentProcess();
            using var module = process.MainModule;
            var moduleHandle = GetModuleHandle(module?.ModuleName);

            _keyboardHook = SetWindowsHookEx(WhKeyboardLl, _keyboardProc, moduleHandle, 0);
            _mouseHook = SetWindowsHookEx(WhMouseLl, _mouseProc, moduleHandle, 0);

            if (_keyboardHook == IntPtr.Zero || _mouseHook == IntPtr.Zero)
            {
                throw new InvalidOperationException("Unable to install global keyboard/mouse hooks.");
            }

            _threadReady.Set();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }
        }
        catch (Exception ex)
        {
            _startupError = ex;
            _threadReady.Set();
        }
        finally
        {
            if (_keyboardHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_keyboardHook);
                _keyboardHook = IntPtr.Zero;
            }

            if (_mouseHook != IntPtr.Zero)
            {
                UnhookWindowsHookEx(_mouseHook);
                _mouseHook = IntPtr.Zero;
            }
        }
    }

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            if (message == WmKeyDown || message == WmKeyUp || message == WmSysKeyDown || message == WmSysKeyUp)
            {
                var data = Marshal.PtrToStructure<KbdLlHookStruct>(lParam);
                var isDown = message is WmKeyDown or WmSysKeyDown;
                var suppress = _onKey(new KeyEvent((int)data.vkCode, isDown));
                if (suppress)
                {
                    return (IntPtr)1;
                }
            }
        }

        return CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var message = wParam.ToInt32();
            var data = Marshal.PtrToStructure<MsLlHookStruct>(lParam);

            switch (message)
            {
                case WmMouseMove:
                    if (_onMouseMove(new MouseMoveEvent(data.pt.x, data.pt.y, (data.flags & LlmhfInjected) != 0)))
                    {
                        return (IntPtr)1;
                    }
                    break;
                case WmLButtonDown:
                    if (_onMouseButton(new MouseButtonEvent(MouseButtonId.Left, true)))
                    {
                        return (IntPtr)1;
                    }
                    break;
                case WmLButtonUp:
                    if (_onMouseButton(new MouseButtonEvent(MouseButtonId.Left, false)))
                    {
                        return (IntPtr)1;
                    }
                    break;
                case WmRButtonDown:
                    if (_onMouseButton(new MouseButtonEvent(MouseButtonId.Right, true)))
                    {
                        return (IntPtr)1;
                    }
                    break;
                case WmRButtonUp:
                    if (_onMouseButton(new MouseButtonEvent(MouseButtonId.Right, false)))
                    {
                        return (IntPtr)1;
                    }
                    break;
                case WmMButtonDown:
                    if (_onMouseButton(new MouseButtonEvent(MouseButtonId.Middle, true)))
                    {
                        return (IntPtr)1;
                    }
                    break;
                case WmMButtonUp:
                    if (_onMouseButton(new MouseButtonEvent(MouseButtonId.Middle, false)))
                    {
                        return (IntPtr)1;
                    }
                    break;
                case WmMouseWheel:
                    var wheelDelta = (short)((data.mouseData >> 16) & 0xFFFF);
                    if (_onMouseWheel(wheelDelta))
                    {
                        return (IntPtr)1;
                    }
                    break;
            }
        }

        return CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    public void Dispose()
    {
        Stop();
        _threadReady.Dispose();
    }

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct Point
    {
        public int x;
        public int y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MsLlHookStruct
    {
        public Point pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KbdLlHookStruct
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr hwnd;
        public uint message;
        public UIntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public Point pt;
        public uint lPrivate;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hmod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref Msg lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref Msg lpmsg);

    [DllImport("user32.dll")]
    private static extern bool PostThreadMessage(uint idThread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
