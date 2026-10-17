using System;
using System.Runtime.InteropServices;

namespace KbmOverlay
{
    internal static class Native
    {
        public const int WM_INPUT = 0x00FF;

        public const uint RID_INPUT = 0x10000003;
        public const int RIM_TYPEMOUSE = 0;
        public const int RIM_TYPEKEYBOARD = 1;
        public const uint RIDEV_INPUTSINK = 0x00000100;

        public const ushort HID_USAGE_PAGE_GENERIC = 0x01;
        public const ushort HID_USAGE_GENERIC_MOUSE = 0x02;
        public const ushort HID_USAGE_GENERIC_KEYBOARD = 0x06;

        public const ushort RI_KEY_BREAK = 0x01;
        public const ushort RI_KEY_E0 = 0x02;
        public const ushort RI_KEY_E1 = 0x04;

        public const ushort MOUSE_MOVE_ABSOLUTE = 0x01;
        public const ushort MOUSE_VIRTUAL_DESKTOP = 0x02;
        public const ushort RI_MOUSE_WHEEL = 0x0400;
        public const ushort RI_MOUSE_HWHEEL = 0x0800;

        public const uint MAPVK_VK_TO_VSC_EX = 4;

        public const int SM_CXSCREEN = 0;
        public const int SM_CYSCREEN = 1;
        public const int SM_CXVIRTUALSCREEN = 78;
        public const int SM_CYVIRTUALSCREEN = 79;

        public const int VK_LBUTTON = 0x01;
        public const int VK_RBUTTON = 0x02;
        public const int VK_MBUTTON = 0x04;
        public const int VK_XBUTTON1 = 0x05;
        public const int VK_XBUTTON2 = 0x06;
        public const int VK_SHIFT = 0x10;
        public const int VK_CONTROL = 0x11;
        public const int VK_MENU = 0x12;
        public const int VK_PAUSE = 0x13;
        public const int VK_HANGUL = 0x15;
        public const int VK_HANJA = 0x19;
        public const int VK_NUMLOCK = 0x90;
        public const int VK_LSHIFT = 0xA0;
        public const int VK_RSHIFT = 0xA1;
        public const int VK_LCONTROL = 0xA2;
        public const int VK_RCONTROL = 0xA3;
        public const int VK_LMENU = 0xA4;
        public const int VK_RMENU = 0xA5;

        [StructLayout(LayoutKind.Sequential)]
        public struct RAWINPUTDEVICE
        {
            public ushort UsagePage;
            public ushort Usage;
            public uint Flags;
            public IntPtr Target;
        }

        [DllImport("user32.dll", SetLastError = true)]
        public static extern bool RegisterRawInputDevices([In] RAWINPUTDEVICE[] devices, uint count, uint size);

        [DllImport("user32.dll")]
        public static extern uint GetRawInputData(IntPtr rawInput, uint command, IntPtr data, ref uint size, uint headerSize);

        [DllImport("user32.dll")]
        public static extern short GetAsyncKeyState(int vKey);

        [DllImport("user32.dll")]
        public static extern uint MapVirtualKey(uint code, uint mapType);

        [DllImport("user32.dll")]
        public static extern int GetSystemMetrics(int index);

        [StructLayout(LayoutKind.Sequential)]
        private struct FLASHWINFO
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("kernel32.dll")]
        private static extern IntPtr GetConsoleWindow();

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FLASHWINFO info);

        // Blinks the console's taskbar button until the window is focused (no sound, which could reach the stream).
        public static void FlashConsole()
        {
            IntPtr window = GetConsoleWindow();
            if (window == IntPtr.Zero) return;
            var info = new FLASHWINFO
            {
                Size = (uint)Marshal.SizeOf(typeof(FLASHWINFO)),
                Window = window,
                Flags = 0x3 | 0xC, // FLASHW_ALL | FLASHW_TIMERNOFG
                Count = 0,
                Timeout = 0
            };
            FlashWindowEx(ref info);
        }
    }
}
