using System.Collections.Generic;

namespace KbmOverlay
{
    internal sealed class KeyId
    {
        public readonly string Name;
        // Keys that only send one event per press (Korean Han/Eng and Hanja on some keyboards).
        public readonly bool Tap;

        public KeyId(string name, bool tap)
        {
            Name = name;
            Tap = tap;
        }
    }

    // Maps raw scan codes to key names. Names and the scan code table follow
    // Zergatul.Obs.InputOverlay (MIT, see THIRD-PARTY-NOTICES.txt) so its pages stay easy to port.
    internal static class KeyNames
    {
        private static readonly Dictionary<int, KeyId> ByScanCode = new Dictionary<int, KeyId>();

        private static readonly KeyId Pause = new KeyId("Pause", false);
        private static readonly KeyId NumLock = new KeyId("NumLock", false);
        private static readonly KeyId Hangul = new KeyId("Hangul", true);
        private static readonly KeyId Hanja = new KeyId("Hanja", true);

        static KeyNames()
        {
            Add(0x01, "Esc");
            Add(0x3B, "F1"); Add(0x3C, "F2"); Add(0x3D, "F3"); Add(0x3E, "F4");
            Add(0x3F, "F5"); Add(0x40, "F6"); Add(0x41, "F7"); Add(0x42, "F8");
            Add(0x43, "F9"); Add(0x44, "F10"); Add(0x57, "F11"); Add(0x58, "F12");
            Add(0x64, "F13"); Add(0x65, "F14"); Add(0x66, "F15"); Add(0x67, "F16");
            Add(0x68, "F17"); Add(0x69, "F18"); Add(0x6A, "F19"); Add(0x6B, "F20");
            Add(0x6C, "F21"); Add(0x6D, "F22"); Add(0x6E, "F23"); Add(0x76, "F24");

            Add(0x29, "Tilde");
            Add(0x02, "Key1"); Add(0x03, "Key2"); Add(0x04, "Key3"); Add(0x05, "Key4"); Add(0x06, "Key5");
            Add(0x07, "Key6"); Add(0x08, "Key7"); Add(0x09, "Key8"); Add(0x0A, "Key9"); Add(0x0B, "Key0");
            Add(0x0C, "Minus"); Add(0x0D, "Plus"); Add(0x0E, "Backspace");

            Add(0x0F, "Tab");
            Add(0x10, "KeyQ"); Add(0x11, "KeyW"); Add(0x12, "KeyE"); Add(0x13, "KeyR"); Add(0x14, "KeyT");
            Add(0x15, "KeyY"); Add(0x16, "KeyU"); Add(0x17, "KeyI"); Add(0x18, "KeyO"); Add(0x19, "KeyP");
            Add(0x1A, "LeftBracket"); Add(0x1B, "RightBracket"); Add(0x2B, "Backslash");

            Add(0x3A, "CapsLock");
            Add(0x1E, "KeyA"); Add(0x1F, "KeyS"); Add(0x20, "KeyD"); Add(0x21, "KeyF"); Add(0x22, "KeyG");
            Add(0x23, "KeyH"); Add(0x24, "KeyJ"); Add(0x25, "KeyK"); Add(0x26, "KeyL");
            Add(0x27, "Semicolon"); Add(0x28, "Quote"); Add(0x1C, "Enter");

            Add(0x2A, "LeftShift");
            Add(0x2C, "KeyZ"); Add(0x2D, "KeyX"); Add(0x2E, "KeyC"); Add(0x2F, "KeyV"); Add(0x30, "KeyB");
            Add(0x31, "KeyN"); Add(0x32, "KeyM");
            Add(0x33, "Comma"); Add(0x34, "Period"); Add(0x35, "Slash"); Add(0x36, "RightShift");
            Add(0x56, "IntlBackslash");

            Add(0x1D, "LeftCtrl"); Add(0xE05B, "LeftWindows"); Add(0x38, "LeftAlt");
            Add(0x39, "Space");
            Add(0xE038, "RightAlt"); Add(0xE05C, "RightWindows"); Add(0xE05D, "App"); Add(0xE01D, "RightCtrl");

            Add(0xE037, "PrintScreen"); Add(0x54, "PrintScreen"); // 0x54 is Alt+PrintScreen (SysRq)
            Add(0x46, "ScrollLock");

            Add(0xE052, "Insert"); Add(0xE047, "Home"); Add(0xE049, "PageUp");
            Add(0xE053, "Delete"); Add(0xE04F, "End"); Add(0xE051, "PageDown");
            Add(0xE04B, "Left"); Add(0xE04D, "Right"); Add(0xE048, "Up"); Add(0xE050, "Down");

            Add(0xE035, "NumSlash"); Add(0x37, "NumAsterisk"); Add(0x4A, "NumMinus"); Add(0x4E, "NumPlus");
            Add(0x52, "Num0"); Add(0x4F, "Num1"); Add(0x50, "Num2"); Add(0x51, "Num3"); Add(0x4B, "Num4");
            Add(0x4C, "Num5"); Add(0x4D, "Num6"); Add(0x47, "Num7"); Add(0x48, "Num8"); Add(0x49, "Num9");
            Add(0x53, "NumPeriod"); Add(0xE01C, "NumEnter");

            ByScanCode[0x45] = NumLock;
            ByScanCode[0xE045] = NumLock;
            ByScanCode[0xE11D] = Pause;
            ByScanCode[0x72] = Hangul; ByScanCode[0xF2] = Hangul;
            ByScanCode[0x71] = Hanja; ByScanCode[0xF1] = Hanja;
        }

        private static void Add(int code, string name)
        {
            ByScanCode[code] = new KeyId(name, false);
        }

        // code is the make code with 0xE000 / 0xE100 added for E0 / E1 prefixed keys.
        public static KeyId Resolve(int code, int vk)
        {
            // Pause arrives as E1 1D followed by a fake 45, and NumLock is sometimes reported with E0.
            if (vk == Native.VK_PAUSE) return Pause;
            if (vk == Native.VK_NUMLOCK) return NumLock;

            if (ByScanCode.TryGetValue(code, out KeyId key)) return key;

            if (vk == Native.VK_HANGUL) return Hangul;
            if (vk == Native.VK_HANJA) return Hanja;
            return null;
        }

        // The virtual key to ask GetAsyncKeyState about, with left and right modifiers told apart.
        public static int AsyncVk(int vk, int code)
        {
            bool e0 = (code >> 8) == 0xE0;
            switch (vk)
            {
                case Native.VK_SHIFT: return (code & 0xFF) == 0x36 ? Native.VK_RSHIFT : Native.VK_LSHIFT;
                case Native.VK_CONTROL: return e0 ? Native.VK_RCONTROL : Native.VK_LCONTROL;
                case Native.VK_MENU: return e0 ? Native.VK_RMENU : Native.VK_LMENU;
                default: return vk;
            }
        }
    }
}
