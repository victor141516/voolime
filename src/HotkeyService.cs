using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Voolime;

internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyVolumeDown = 0x5601;
    private const int HotkeyVolumeUp = 0x5602;
    private const int HotkeyVolumeMute = 0x5603;
    private const int MOD_SHIFT = 0x0004;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WH_KEYBOARD_LL = 13;
    private const int VK_SHIFT = 0x10;
    private const int VK_VOLUME_MUTE = 0xAD;
    private const int VK_VOLUME_DOWN = 0xAE;
    private const int VK_VOLUME_UP = 0xAF;

    private readonly Action<VolumeHotkeyKind> _handler;
    private readonly Dispatcher _dispatcher;
    private readonly HwndSource _source;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private readonly IntPtr _keyboardHook;
    private bool _disposed;

    public HotkeyService(Action<VolumeHotkeyKind> handler)
    {
        _handler = handler;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var parameters = new HwndSourceParameters("VoolimeHotkeySink")
        {
            WindowStyle = 0,
            ParentWindow = NativeMethods.HWND_MESSAGE
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        NativeMethods.RegisterHotKey(_source.Handle, HotkeyVolumeDown, MOD_SHIFT, VK_VOLUME_DOWN);
        NativeMethods.RegisterHotKey(_source.Handle, HotkeyVolumeUp, MOD_SHIFT, VK_VOLUME_UP);
        NativeMethods.RegisterHotKey(_source.Handle, HotkeyVolumeMute, MOD_SHIFT, VK_VOLUME_MUTE);

        _keyboardProc = KeyboardHookCallback;
        _keyboardHook = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, NativeMethods.GetModuleHandle(null), 0);
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg != WM_HOTKEY)
        {
            return IntPtr.Zero;
        }

        handled = true;
        var kind = wParam.ToInt32() switch
        {
            HotkeyVolumeUp => VolumeHotkeyKind.Up,
            HotkeyVolumeMute => VolumeHotkeyKind.ToggleMute,
            _ => VolumeHotkeyKind.Down
        };

        _handler(kind);
        return IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && (wParam.ToInt32() == WM_KEYDOWN || wParam.ToInt32() == WM_SYSKEYDOWN))
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var vkCode = (int)info.vkCode;
            if (IsVolumeKey(vkCode) && IsShiftDown())
            {
                var kind = vkCode switch
                {
                    VK_VOLUME_UP => VolumeHotkeyKind.Up,
                    VK_VOLUME_MUTE => VolumeHotkeyKind.ToggleMute,
                    _ => VolumeHotkeyKind.Down
                };

                _dispatcher.BeginInvoke(() => _handler(kind));
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private static bool IsVolumeKey(int vkCode) =>
        vkCode is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE;

    private static bool IsShiftDown() =>
        (NativeMethods.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyVolumeDown);
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyVolumeUp);
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyVolumeMute);

        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        }

        _source.Dispose();
    }
}
