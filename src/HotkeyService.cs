using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Voolime;

[Flags]
internal enum ActivationModifiers
{
    None = 0,
    Shift = 1,
    Control = 2,
    Alt = 4
}

internal sealed record VolumeHotkeyPress(VolumeHotkeyKind Kind, bool IsHeldRepeat);

internal sealed class HotkeyService : IDisposable
{
    private const int HotkeyVolumeDown = 0x5601;
    private const int HotkeyVolumeUp = 0x5602;
    private const int HotkeyVolumeMute = 0x5603;
    private const int MOD_ALT = 0x0001;
    private const int MOD_CONTROL = 0x0002;
    private const int MOD_SHIFT = 0x0004;
    private const int WM_HOTKEY = 0x0312;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;
    private const int WM_MOUSEWHEEL = 0x020A;
    private const int WM_MOUSEHWHEEL = 0x020E;
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;
    private const int VK_CONTROL = 0x11;
    private const int VK_MENU = 0x12;
    private const int VK_SHIFT = 0x10;
    private const int VK_VOLUME_MUTE = 0xAD;
    private const int VK_VOLUME_DOWN = 0xAE;
    private const int VK_VOLUME_UP = 0xAF;

    private readonly Action<VolumeHotkeyPress> _handler;
    private readonly Dispatcher _dispatcher;
    private readonly HwndSource _source;
    private readonly NativeMethods.LowLevelKeyboardProc _keyboardProc;
    private readonly NativeMethods.LowLevelMouseProc _mouseProc;
    private readonly IntPtr _keyboardHook;
    private readonly IntPtr _mouseHook;
    private readonly HashSet<int> _heldVolumeKeys = [];
    private ActivationModifiers _keyboardModifiers;
    private ActivationModifiers _mouseModifiers;
    private bool _disposed;

    public HotkeyService(Action<VolumeHotkeyPress> handler, ActivationModifiers keyboardModifiers, ActivationModifiers mouseModifiers)
    {
        _handler = handler;
        _keyboardModifiers = keyboardModifiers;
        _mouseModifiers = mouseModifiers;
        _dispatcher = Dispatcher.CurrentDispatcher;

        var parameters = new HwndSourceParameters("VoolimeHotkeySink")
        {
            WindowStyle = 0,
            ParentWindow = NativeMethods.HWND_MESSAGE
        };

        _source = new HwndSource(parameters);
        _source.AddHook(WndProc);

        RegisterHotkeys();

        _keyboardProc = KeyboardHookCallback;
        _mouseProc = MouseHookCallback;
        _keyboardHook = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, NativeMethods.GetModuleHandle(null), 0);
        var keyboardHookError = _keyboardHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        _mouseHook = NativeMethods.SetWindowsHookEx(WH_MOUSE_LL, _mouseProc, NativeMethods.GetModuleHandle(null), 0);
        var mouseHookError = _mouseHook == IntPtr.Zero ? Marshal.GetLastWin32Error() : 0;
        AppLogger.Info($"Keyboard hook installed: {_keyboardHook != IntPtr.Zero}, error={keyboardHookError}.");
        AppLogger.Info($"Mouse hook installed: {_mouseHook != IntPtr.Zero}, error={mouseHookError}.");
    }

    public ActivationModifiers KeyboardModifiers => _keyboardModifiers;

    public ActivationModifiers MouseModifiers => _mouseModifiers;

    public void SetKeyboardModifiers(ActivationModifiers modifiers)
    {
        if (_keyboardModifiers == modifiers)
        {
            return;
        }

        UnregisterHotkeys();
        _keyboardModifiers = modifiers;
        RegisterHotkeys();
    }

    public void SetMouseModifiers(ActivationModifiers modifiers) =>
        _mouseModifiers = modifiers;

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

        Dispatch(kind, isHeldRepeat: false);
        return IntPtr.Zero;
    }

    private IntPtr KeyboardHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0)
        {
            var info = Marshal.PtrToStructure<NativeMethods.KBDLLHOOKSTRUCT>(lParam);
            var vkCode = (int)info.vkCode;

            if (IsKeyUp(wParam.ToInt32()) && IsVolumeKey(vkCode))
            {
                _heldVolumeKeys.Remove(vkCode);
                return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
            }

            if (IsKeyDown(wParam.ToInt32()) && IsVolumeKey(vkCode) && AreModifiersDown(_keyboardModifiers))
            {
                var kind = vkCode switch
                {
                    VK_VOLUME_UP => VolumeHotkeyKind.Up,
                    VK_VOLUME_MUTE => VolumeHotkeyKind.ToggleMute,
                    _ => VolumeHotkeyKind.Down
                };

                var isHeldRepeat = !_heldVolumeKeys.Add(vkCode);
                Dispatch(kind, isHeldRepeat);
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_keyboardHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
        var message = wParam.ToInt32();
        if (nCode >= 0 && IsWheelMessage(message) && AreModifiersDown(_mouseModifiers))
        {
            var info = Marshal.PtrToStructure<NativeMethods.MSLLHOOKSTRUCT>(lParam);
            var wheelDelta = unchecked((short)((info.mouseData >> 16) & 0xFFFF));
            if (wheelDelta != 0)
            {
                var kind = GetWheelKind(message, wheelDelta);
                AppLogger.Info($"Mouse wheel volume action consumed: message=0x{message:X}, delta={wheelDelta}, kind={kind}, modifiers={_mouseModifiers}.");
                Dispatch(kind, isHeldRepeat: false);
                return new IntPtr(1);
            }
        }

        return NativeMethods.CallNextHookEx(_mouseHook, nCode, wParam, lParam);
    }

    private static bool IsVolumeKey(int vkCode) =>
        vkCode is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE;

    private static bool IsKeyDown(int message) =>
        message is WM_KEYDOWN or WM_SYSKEYDOWN;

    private static bool IsKeyUp(int message) =>
        message is WM_KEYUP or WM_SYSKEYUP;

    private static bool IsWheelMessage(int message) =>
        message is WM_MOUSEWHEEL or WM_MOUSEHWHEEL;

    private static VolumeHotkeyKind GetWheelKind(int message, short wheelDelta)
    {
        if (message == WM_MOUSEHWHEEL)
        {
            return wheelDelta > 0 ? VolumeHotkeyKind.Down : VolumeHotkeyKind.Up;
        }

        return wheelDelta > 0 ? VolumeHotkeyKind.Up : VolumeHotkeyKind.Down;
    }

    private static bool AreModifiersDown(ActivationModifiers modifiers)
    {
        if (modifiers == ActivationModifiers.None)
        {
            return false;
        }

        return (!modifiers.HasFlag(ActivationModifiers.Shift) || IsKeyPressed(VK_SHIFT)) &&
               (!modifiers.HasFlag(ActivationModifiers.Control) || IsKeyPressed(VK_CONTROL)) &&
               (!modifiers.HasFlag(ActivationModifiers.Alt) || IsKeyPressed(VK_MENU));
    }

    private static bool IsKeyPressed(int virtualKey) =>
        (NativeMethods.GetAsyncKeyState(virtualKey) & 0x8000) != 0;

    private static int GetHotkeyModifier(ActivationModifiers modifiers)
    {
        var hotkeyModifier = 0;
        if (modifiers.HasFlag(ActivationModifiers.Shift))
        {
            hotkeyModifier |= MOD_SHIFT;
        }

        if (modifiers.HasFlag(ActivationModifiers.Control))
        {
            hotkeyModifier |= MOD_CONTROL;
        }

        if (modifiers.HasFlag(ActivationModifiers.Alt))
        {
            hotkeyModifier |= MOD_ALT;
        }

        return hotkeyModifier;
    }

    private void RegisterHotkeys()
    {
        if (_keyboardModifiers == ActivationModifiers.None)
        {
            return;
        }

        var modifier = GetHotkeyModifier(_keyboardModifiers);
        NativeMethods.RegisterHotKey(_source.Handle, HotkeyVolumeDown, modifier, VK_VOLUME_DOWN);
        NativeMethods.RegisterHotKey(_source.Handle, HotkeyVolumeUp, modifier, VK_VOLUME_UP);
        NativeMethods.RegisterHotKey(_source.Handle, HotkeyVolumeMute, modifier, VK_VOLUME_MUTE);
    }

    private void UnregisterHotkeys()
    {
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyVolumeDown);
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyVolumeUp);
        NativeMethods.UnregisterHotKey(_source.Handle, HotkeyVolumeMute);
    }

    private void Dispatch(VolumeHotkeyKind kind, bool isHeldRepeat) =>
        _dispatcher.BeginInvoke(() => _handler(new VolumeHotkeyPress(kind, isHeldRepeat)));

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnregisterHotkeys();

        if (_keyboardHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_keyboardHook);
        }

        if (_mouseHook != IntPtr.Zero)
        {
            NativeMethods.UnhookWindowsHookEx(_mouseHook);
        }

        _source.Dispose();
    }
}
