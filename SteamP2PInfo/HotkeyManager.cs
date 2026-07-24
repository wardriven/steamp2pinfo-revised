using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Input;
using SteamP2PInfo.WinAPI;
using MahApps.Metro.Controls;

namespace SteamP2PInfo
{
    // Static class handling hotkeys which activates once on a key combination
    // whose actual key may change at any time.
    public static class HotkeyManager
    {
        /// <summary>
        /// Dynamic hotkey: a getter which gets the key to detect, and a handler.
        /// </summary>
        private class DynamicHotkey
        {
            public Func<int> getter;
            public Action handler;
            public IntPtr hWindow;
            public int activeVirtualKey;
        }

        private static int hkCnt;
        private static IntPtr hHook;
        private static User32.HookProc hookProc;
        private static Dictionary<int, DynamicHotkey> hotkeys;

        public static bool Enabled => hHook != IntPtr.Zero;

        static HotkeyManager()
        {
            hkCnt = 0;
            hHook = IntPtr.Zero;
            hotkeys = new Dictionary<int, DynamicHotkey>();
            hookProc = new User32.HookProc(EvtDispatcher);
        }

        public static bool Enable()
        {
            if (hHook != IntPtr.Zero) return true;
            ResetPressedState();
            hHook = User32.SetWindowsHookEx(13, hookProc, IntPtr.Zero, 0);
            if (Enabled)
                DiagnosticLogger.Write("HOTKEY", "Global hotkey hook enabled.");
            else
                DiagnosticLogger.Write("ERROR", "Global hotkey hook could not be enabled. Win32 error: " + Marshal.GetLastWin32Error());
            return Enabled;
        }

        public static void Disable()
        {
            if (Enabled)
            {
                if (!User32.UnhookWindowsHookEx(hHook))
                    DiagnosticLogger.Write("ERROR", "Global hotkey hook could not be disabled. Win32 error: " + Marshal.GetLastWin32Error());
                else
                    DiagnosticLogger.Write("HOTKEY", "Global hotkey hook disabled.");
                hHook = IntPtr.Zero;
            }
            ResetPressedState();
        }

        public static int AddHotkey(IntPtr hWindow, Func<HotKey> getter, Action handler)
        {
            int iget()
            {
                HotKey hk = getter();
                return (hk == null) ? 0 : (int)hk.ModifierKeys << 8 | (int)hk.Key;
            };
            return AddHotkey(new DynamicHotkey { hWindow = hWindow, getter = iget, handler = handler });
        }

        public static int AddHotkey(IntPtr hWindow, Func<int> getter, Action handler)
        {
            return AddHotkey(new DynamicHotkey { hWindow = hWindow, getter = getter, handler = handler });
        }

        private static int AddHotkey(DynamicHotkey hk)
        {
            hotkeys[++hkCnt] = hk;
            DiagnosticLogger.Write("HOTKEY", "Registered dynamic hotkey ID " + hkCnt + ".");
            return hkCnt;
        }

        public static bool RemoveHotkey(int id)
        {
            bool removed = hotkeys.Remove(id);
            if (removed)
                DiagnosticLogger.Write("HOTKEY", "Removed dynamic hotkey ID " + id + ".");
            return removed;
        }

        private static IntPtr EvtDispatcher(int nCode, IntPtr wParam, IntPtr lParam)
        {
            int msg = wParam.ToInt32();
            bool isKeyDown = msg == 0x100 || msg == 0x104;
            bool isKeyUp = msg == 0x101 || msg == 0x105;
            if (nCode >= 0 && (isKeyDown || isKeyUp))
            {
                KBDLLHOOKSTRUCT kbInfo = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                IntPtr foreWindow = User32.GetForegroundWindow();

                if (isKeyUp)
                {
                    foreach (DynamicHotkey hotkey in hotkeys.Values)
                        EndPress(ref hotkey.activeVirtualKey, kbInfo.vkCode);
                    return User32.CallNextHookEx(hHook, nCode, wParam, lParam);
                }

                int kState = kbInfo.vkCode; // Build current key state (with modifiers)
                kState |= (User32.GetAsyncKeyState(0x5B) & 0x8000) >> 4; // LWIN
                kState |= (User32.GetAsyncKeyState(0x5c) & 0x8000) >> 4; // RWIN
                kState |= (User32.GetAsyncKeyState(0x10) & 0x8000) >> 5; // SHIFT
                kState |= (User32.GetAsyncKeyState(0x11) & 0x8000) >> 6; // CTRL
                kState |= (User32.GetAsyncKeyState(0x12) & 0x8000) >> 7; // ALT

                foreach (KeyValuePair<int, DynamicHotkey> entry in hotkeys)
                {
                    DynamicHotkey hk = entry.Value;
                    try
                    {
                        if (hk.hWindow == foreWindow
                            && hk.getter() == kState
                            && TryBeginPress(ref hk.activeVirtualKey, kbInfo.vkCode))
                        {
                            DiagnosticLogger.Write("HOTKEY", "Pressed " + DescribeHotkey(kState) + " (dynamic hotkey ID " + entry.Key + ").");
                            hk.handler();
                        }
                    }
                    catch (Exception ex)
                    {
                        DiagnosticLogger.WriteException("ERROR", ex, "Dynamic hotkey ID " + entry.Key + " failed.");
                    }
                }
            }
            return User32.CallNextHookEx(hHook, nCode, wParam, lParam);
        }

        internal static bool TryBeginPress(ref int activeVirtualKey, int virtualKey)
        {
            if (activeVirtualKey != 0)
                return false;

            activeVirtualKey = virtualKey;
            return true;
        }

        internal static void EndPress(ref int activeVirtualKey, int virtualKey)
        {
            if (activeVirtualKey == virtualKey)
                activeVirtualKey = 0;
        }

        private static void ResetPressedState()
        {
            foreach (DynamicHotkey hotkey in hotkeys.Values)
                hotkey.activeVirtualKey = 0;
        }

        private static string DescribeHotkey(int keyState)
        {
            var parts = new List<string>();
            if ((keyState & 0x0800) != 0) parts.Add("Win");
            if ((keyState & 0x0400) != 0) parts.Add("Shift");
            if ((keyState & 0x0200) != 0) parts.Add("Ctrl");
            if ((keyState & 0x0100) != 0) parts.Add("Alt");

            Key key = KeyInterop.KeyFromVirtualKey(keyState & 0xFF);
            parts.Add(key == Key.None ? string.Format("VK_0x{0:X2}", keyState & 0xFF) : key.ToString());
            return string.Join("+", parts);
        }
    }
}
