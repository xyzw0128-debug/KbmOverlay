using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;

namespace KbmOverlay
{
    // Receives keyboard and mouse input through Raw Input on a hidden window.
    // RIDEV_INPUTSINK delivers input while other apps (games) have focus, and
    // mouse movement arrives as relative counts, so it works when a game locks the cursor.
    internal sealed class RawInputReader : NativeWindow
    {
        private static readonly string[] ButtonNames = { "Mouse1", "Mouse2", "Mouse3", "Mouse4", "Mouse5" };
        private static readonly int[] ButtonVks =
        {
            Native.VK_LBUTTON, Native.VK_RBUTTON, Native.VK_MBUTTON, Native.VK_XBUTTON1, Native.VK_XBUTTON2
        };

        private const uint BufferSize = 1024;

        private readonly InputHub hub;
        private readonly uint headerSize = (uint)(8 + 2 * IntPtr.Size);
        private readonly IntPtr buffer = Marshal.AllocHGlobal((int)BufferSize);
        private int lastAbsX = int.MinValue;
        private int lastAbsY;
        private bool reportedError;

        public RawInputReader(InputHub hub)
        {
            this.hub = hub;
        }

        public void Start()
        {
            Exception error = null;
            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                try
                {
                    CreateHandle(new CreateParams());
                    Register();
                }
                catch (Exception e)
                {
                    error = e;
                    ready.Set();
                    return;
                }
                ready.Set();
                Application.Run();
            });
            thread.IsBackground = true;
            thread.Name = "RawInput";
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();
            if (error != null) throw error;
        }

        private void Register()
        {
            var devices = new[]
            {
                new Native.RAWINPUTDEVICE
                {
                    UsagePage = Native.HID_USAGE_PAGE_GENERIC,
                    Usage = Native.HID_USAGE_GENERIC_MOUSE,
                    Flags = Native.RIDEV_INPUTSINK,
                    Target = Handle
                },
                new Native.RAWINPUTDEVICE
                {
                    UsagePage = Native.HID_USAGE_PAGE_GENERIC,
                    Usage = Native.HID_USAGE_GENERIC_KEYBOARD,
                    Flags = Native.RIDEV_INPUTSINK,
                    Target = Handle
                }
            };
            if (!Native.RegisterRawInputDevices(devices, (uint)devices.Length, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE))))
                throw new Win32Exception(Marshal.GetLastWin32Error());
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_INPUT)
            {
                try
                {
                    Read(m.LParam);
                }
                catch (Exception e)
                {
                    if (!reportedError)
                    {
                        reportedError = true;
                        Log.Error("입력을 읽다가 오류가 났어요: " + e.Message);
                    }
                }
            }
            base.WndProc(ref m);
        }

        private void Read(IntPtr rawInput)
        {
            uint size = BufferSize;
            if (Native.GetRawInputData(rawInput, Native.RID_INPUT, buffer, ref size, headerSize) == uint.MaxValue)
                return;

            int type = Marshal.ReadInt32(buffer, 0);
            int offset = (int)headerSize;
            if (type == Native.RIM_TYPEKEYBOARD)
                ReadKeyboard(offset);
            else if (type == Native.RIM_TYPEMOUSE)
                ReadMouse(offset);
        }

        private void ReadKeyboard(int o)
        {
            int makeCode = (ushort)Marshal.ReadInt16(buffer, o);
            int flags = (ushort)Marshal.ReadInt16(buffer, o + 2);
            int vk = (ushort)Marshal.ReadInt16(buffer, o + 6);

            // 0xFF marks the extra half of an escape sequence (e.g. the fake 45 after Pause).
            if (vk == 0xFF) return;

            int code = makeCode;
            if ((flags & Native.RI_KEY_E0) != 0) code |= 0xE000;
            else if ((flags & Native.RI_KEY_E1) != 0) code |= 0xE100;

            // Input injected without a scan code (macro tools, on-screen keyboards).
            if (makeCode == 0 && vk != 0)
                code = (int)Native.MapVirtualKey((uint)vk, Native.MAPVK_VK_TO_VSC_EX);

            KeyId key = KeyNames.Resolve(code, vk);
            if (key == null) return;

            bool down = (flags & Native.RI_KEY_BREAK) == 0;
            if (key.Tap)
                hub.KeyTap(key.Name);
            else
                hub.Key(key.Name, KeyNames.AsyncVk(vk, code), down);
        }

        private void ReadMouse(int o)
        {
            int moveFlags = (ushort)Marshal.ReadInt16(buffer, o);
            int buttonFlags = (ushort)Marshal.ReadInt16(buffer, o + 4);
            short buttonData = Marshal.ReadInt16(buffer, o + 6);
            int x = Marshal.ReadInt32(buffer, o + 12);
            int y = Marshal.ReadInt32(buffer, o + 16);

            if ((moveFlags & Native.MOUSE_MOVE_ABSOLUTE) != 0)
            {
                // Tablets, remote desktop and VMs report 0..65535 positions; turn them into pixel deltas.
                bool virtualDesktop = (moveFlags & Native.MOUSE_VIRTUAL_DESKTOP) != 0;
                int width = Native.GetSystemMetrics(virtualDesktop ? Native.SM_CXVIRTUALSCREEN : Native.SM_CXSCREEN);
                int height = Native.GetSystemMetrics(virtualDesktop ? Native.SM_CYVIRTUALSCREEN : Native.SM_CYSCREEN);
                int px = (int)(x / 65535.0 * width);
                int py = (int)(y / 65535.0 * height);
                if (lastAbsX != int.MinValue && (px != lastAbsX || py != lastAbsY))
                    hub.Move(px - lastAbsX, py - lastAbsY);
                lastAbsX = px;
                lastAbsY = py;
            }
            else if (x != 0 || y != 0)
            {
                hub.Move(x, y);
            }

            if (buttonFlags == 0) return;

            for (int i = 0; i < ButtonNames.Length; i++)
            {
                if ((buttonFlags & (1 << (2 * i))) != 0) hub.Button(ButtonNames[i], ButtonVks[i], true);
                if ((buttonFlags & (2 << (2 * i))) != 0) hub.Button(ButtonNames[i], ButtonVks[i], false);
            }
            if ((buttonFlags & Native.RI_MOUSE_WHEEL) != 0) hub.Wheel(0, buttonData);
            if ((buttonFlags & Native.RI_MOUSE_HWHEEL) != 0) hub.Wheel(buttonData, 0);
        }
    }
}
