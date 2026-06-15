using System;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Voolime;

internal enum ActivationModifier
{
    Shift,
    Control,
    Alt
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
    private const int WH_KEYBOARD_LL = 13;
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
    private readonly IntPtr _keyboardHook;
    private readonly HashSet<int> _heldVolumeKeys = [];
    private ActivationModifier _modifier;
    private bool _disposed;

    public HotkeyService(Action<VolumeHotkeyPress> handler, ActivationModifier modifier)
    {
        _handler = handler;
        _modifier = modifier;
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
        _keyboardHook = NativeMethods.SetWindowsHookEx(WH_KEYBOARD_LL, _keyboardProc, NativeMethods.GetModuleHandle(null), 0);
    }

    public ActivationModifier Modifier => _modifier;

    public void SetModifier(ActivationModifier modifier)
    {
        if (_modifier == modifier)
        {
            return;
        }

        UnregisterHotkeys();
        _modifier = modifier;
        RegisterHotkeys();
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

            if (IsKeyDown(wParam.ToInt32()) && IsVolumeKey(vkCode) && IsModifierDown(_modifier))
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

    private static bool IsVolumeKey(int vkCode) =>
        vkCode is VK_VOLUME_UP or VK_VOLUME_DOWN or VK_VOLUME_MUTE;

    private static bool IsKeyDown(int message) =>
        message is WM_KEYDOWN or WM_SYSKEYDOWN;

    private static bool IsKeyUp(int message) =>
        message is WM_KEYUP or WM_SYSKEYUP;

    private static bool IsModifierDown(ActivationModifier modifier) =>
        (NativeMethods.GetAsyncKeyState(GetVirtualKey(modifier)) & 0x8000) != 0;

    private static int GetVirtualKey(ActivationModifier modifier) =>
        modifier switch
        {
            ActivationModifier.Control => VK_CONTROL,
            ActivationModifier.Alt => VK_MENU,
            _ => VK_SHIFT
        };

    private static int GetHotkeyModifier(ActivationModifier modifier) =>
        modifier switch
        {
            ActivationModifier.Control => MOD_CONTROL,
            ActivationModifier.Alt => MOD_ALT,
            _ => MOD_SHIFT
        };

    private void RegisterHotkeys()
    {
        var modifier = GetHotkeyModifier(_modifier);
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

        _source.Dispose();
    }
}
