// Star Citizen Anti AFK - a tiny anti-idle utility for Windows 10/11.
// Made by C0P3Y | Icarus Interstellar Inc.
//
//  * Single portable .exe, uses the .NET Framework 4.x that ships with Windows (no install).
//  * Zero registry access. Settings live in StarCitizenAntiAFK.ini next to the exe
//    (falls back to %LOCALAPPDATA%\StarCitizenAntiAFK\ if the exe folder is read-only).
//  * Optional auto-accept: reads the top-centre HUD of Star Citizen with the OCR engine built into
//    Windows 10/11 (via reflection, no extra files) and presses [ for mission / party invites.
//  * Idle detection uses GetLastInputInfo (passive, no hooks). Its own injected input is
//    fingerprinted so only REAL keyboard/mouse activity returns it to "waiting".
//  * Written in C# 5 style so it can be rebuilt with the csc.exe that ships with Windows.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

[assembly: System.Reflection.AssemblyTitle("Star Citizen Anti AFK")]
[assembly: System.Reflection.AssemblyProduct("Star Citizen Anti AFK")]
[assembly: System.Reflection.AssemblyCompany("Icarus Interstellar Inc.")]
[assembly: System.Reflection.AssemblyVersion("1.0.0.0")]

namespace AntiAfk
{
    // ------------------------------------------------------------------ Win32
    static class Native
    {
        public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
        public const uint KEYEVENTF_EXTENDEDKEY = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_SCANCODE = 8;
        public const uint MOUSEEVENTF_MOVE = 1;
        public const int WM_HOTKEY = 0x0312;
        public const int WM_INPUT = 0x00FF;
        public const uint RIDEV_INPUTSINK = 0x00000100;
        public const uint RID_HEADER = 0x10000005;
        public const uint RIM_TYPEMOUSE = 0, RIM_TYPEKEYBOARD = 1;
        public const uint MOD_NOREPEAT = 0x4000;

        [StructLayout(LayoutKind.Sequential)]
        public struct LASTINPUTINFO { public uint cbSize; public uint dwTime; }

        [StructLayout(LayoutKind.Sequential)]
        public struct MOUSEINPUT { public int dx; public int dy; public uint mouseData; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Sequential)]
        public struct KEYBDINPUT { public ushort wVk; public ushort wScan; public uint dwFlags; public uint time; public IntPtr dwExtraInfo; }

        [StructLayout(LayoutKind.Explicit)]
        public struct INPUTUNION
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct INPUT { public uint type; public INPUTUNION u; }

        [DllImport("user32.dll")] static extern bool GetLastInputInfo(ref LASTINPUTINFO plii);
        [DllImport("kernel32.dll")] public static extern uint GetTickCount();
        [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT[] inputs, int cbSize);
        [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
        [DllImport("user32.dll")] public static extern uint MapVirtualKey(uint code, uint mapType);
        [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
        [DllImport("user32.dll")] public static extern void SwitchToThisWindow(IntPtr hWnd, bool altTab);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)] public struct POINT { public int X, Y; }
        [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTDEVICE { public ushort usUsagePage; public ushort usUsage; public uint dwFlags; public IntPtr hwndTarget; }
        [StructLayout(LayoutKind.Sequential)] public struct RAWINPUTHEADER { public uint dwType; public uint dwSize; public IntPtr hDevice; public IntPtr wParam; }
        [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE[] devices, uint count, uint size);
        [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr hRawInput, uint command, IntPtr data, ref uint size, uint headerSize);
        [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hWnd, out RECT rect);
        [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hWnd, ref POINT pt);
        [DllImport("dwmapi.dll")] public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        /// <summary>Tick count (same clock as GetTickCount) of the last input event in this session.</summary>
        public static uint LastInputTick()
        {
            LASTINPUTINFO i = new LASTINPUTINFO();
            i.cbSize = (uint)Marshal.SizeOf(typeof(LASTINPUTINFO));
            GetLastInputInfo(ref i);
            return i.dwTime;
        }
    }

    // ------------------------------------------------------------ Input sender
    static class Injector
    {
        static bool IsExtended(Keys k)
        {
            switch (k)
            {
                case Keys.Up: case Keys.Down: case Keys.Left: case Keys.Right:
                case Keys.Insert: case Keys.Delete: case Keys.Home: case Keys.End:
                case Keys.Prior: case Keys.Next: case Keys.NumLock: case Keys.Divide:
                    return true;
            }
            return false;
        }

        static Native.INPUT Key(Keys k, uint scan, uint flags)
        {
            Native.INPUT i = new Native.INPUT();
            i.type = Native.INPUT_KEYBOARD;
            i.u.ki.wVk = (ushort)k;
            i.u.ki.wScan = (ushort)scan;
            i.u.ki.dwFlags = flags;
            return i;
        }

        static void Send(Native.INPUT i)
        {
            Native.SendInput(1, new Native.INPUT[] { i }, Marshal.SizeOf(typeof(Native.INPUT)));
        }

        /// <summary>Press and release a key. Uses scan codes so DirectInput/raw-input games see it.</summary>
        public static void TapKey(Keys key, int holdMs)
        {
            uint scan = Native.MapVirtualKey((uint)key, 0);
            uint flags = scan != 0 ? Native.KEYEVENTF_SCANCODE : 0;
            if (IsExtended(key)) flags |= Native.KEYEVENTF_EXTENDEDKEY;
            Send(Key(key, scan, flags));
            Thread.Sleep(holdMs);
            Send(Key(key, scan, flags | Native.KEYEVENTF_KEYUP));
        }

        /// <summary>Keys that open a Star Citizen screen (F1 mobiGlas, F2 map, F11 social list) instead of doing nothing.</summary>
        public static bool OpensMenu(Keys key)
        {
            return key == Keys.F1 || key == Keys.F2 || key == Keys.F11;
        }

        /// <summary>Tap a key; for menu keys tap it again after a short pause so the screen it opened closes again.</summary>
        public static void AfkKey(Keys key, int holdMs)
        {
            TapKey(key, holdMs);
            if (!OpensMenu(key)) return;
            Thread.Sleep(1500);   // the game ignores a second press that comes much sooner
            TapKey(key, holdMs);
        }

        static void MoveRel(int dx)
        {
            Native.INPUT i = new Native.INPUT();
            i.type = Native.INPUT_MOUSE;
            i.u.mi.dx = dx;
            i.u.mi.dwFlags = Native.MOUSEEVENTF_MOVE;
            Send(i);
        }

        /// <summary>Center the cursor, nudge it +px to the right, then back -px to the left.</summary>
        public static void Wiggle(int px)
        {
            Rectangle b = Screen.PrimaryScreen.Bounds;
            int cx = b.Left + b.Width / 2, cy = b.Top + b.Height / 2;
            Native.SetCursorPos(cx, cy);
            Thread.Sleep(30);
            MoveRel(px);
            Thread.Sleep(60);
            MoveRel(-px);
            Thread.Sleep(30);
            Native.SetCursorPos(cx, cy); // cancel any pointer-acceleration drift
        }
    }

    // ------------------------------------------------------ Game window focus
    enum FocusResult { NotRunning, Focused, Failed }

    static class GameFocus
    {
        public const string ProcessName = "StarCitizen"; // StarCitizen.exe

        /// <summary>Finds the game's main window. Returns false if the game isn't running (or has no window yet).</summary>
        static bool Find(out IntPtr hwnd, out uint pid)
        {
            hwnd = IntPtr.Zero; pid = 0;
            Process[] list = null;
            try
            {
                list = Process.GetProcessesByName(ProcessName);
                foreach (Process p in list)
                {
                    try
                    {
                        p.Refresh();
                        IntPtr h = p.MainWindowHandle;
                        if (h != IntPtr.Zero && Native.IsWindow(h)) { hwnd = h; pid = (uint)p.Id; return true; }
                    }
                    catch { }
                }
            }
            catch { }
            finally
            {
                if (list != null) foreach (Process p in list) p.Dispose();
            }
            return false;
        }

        static bool GameIsForeground(uint pid)
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;
            uint fpid;
            Native.GetWindowThreadProcessId(fg, out fpid);
            return fpid == pid;
        }

        public static bool TryGetWindow(out IntPtr hwnd, out uint pid) { return Find(out hwnd, out pid); }

        public static bool IsForeground(uint pid) { return GameIsForeground(pid); }

        /// <summary>The game window's client area in screen coordinates.</summary>
        public static bool ClientScreenRect(IntPtr hwnd, out Rectangle r)
        {
            r = Rectangle.Empty;
            Native.RECT rc;
            if (!Native.GetClientRect(hwnd, out rc)) return false;
            Native.POINT pt = new Native.POINT();
            if (!Native.ClientToScreen(hwnd, ref pt)) return false;
            r = new Rectangle(pt.X, pt.Y, rc.Right - rc.Left, rc.Bottom - rc.Top);
            return true;
        }

        /// <summary>
        /// Brings StarCitizen.exe to the foreground. NotRunning means nothing was touched, so the
        /// currently active window stays active. Never throws.
        /// </summary>
        public static FocusResult Activate()
        {
            try
            {
                IntPtr h; uint pid;
                if (!Find(out h, out pid)) return FocusResult.NotRunning;
                if (GameIsForeground(pid)) return FocusResult.Focused;

                if (Native.IsIconic(h)) Native.ShowWindow(h, 9); // SW_RESTORE

                // Windows blocks background apps from stealing focus; attaching to the foreground and
                // target threads' input queues is the standard way to be allowed to switch.
                uint dummy;
                IntPtr fg = Native.GetForegroundWindow();
                uint fgThread = fg != IntPtr.Zero ? Native.GetWindowThreadProcessId(fg, out dummy) : 0;
                uint tgThread = Native.GetWindowThreadProcessId(h, out dummy);
                uint me = Native.GetCurrentThreadId();
                bool a1 = fgThread != 0 && fgThread != me && Native.AttachThreadInput(me, fgThread, true);
                bool a2 = tgThread != 0 && tgThread != me && Native.AttachThreadInput(me, tgThread, true);
                Native.BringWindowToTop(h);
                Native.SetForegroundWindow(h);
                if (a2) Native.AttachThreadInput(me, tgThread, false);
                if (a1) Native.AttachThreadInput(me, fgThread, false);

                Thread.Sleep(120);
                if (!GameIsForeground(pid))
                {
                    Native.SwitchToThisWindow(h, true); // fallback that ignores foreground lock
                    Thread.Sleep(200);
                }
                else Thread.Sleep(80);
                return GameIsForeground(pid) ? FocusResult.Focused : FocusResult.Failed;
            }
            catch { return FocusResult.Failed; }
        }
    }

    // ---------------------------------------------------------------- Settings
    sealed class Settings
    {
        public int IdleSeconds = 300;        // idle time before AFK input starts
        public int IntervalSeconds = 30;     // time between AFK inputs
        public int Mode = 0;                 // 0 = keyboard, 1 = mouse wiggle
        public int KeyVk = (int)Keys.Space;  // key sent in keyboard mode
        public int HotVk = (int)Keys.F9;     // start/stop hotkey
        public int HotMods = 2 | 4;          // Win32 MOD_ flags: 1 Alt, 2 Ctrl, 4 Shift
        public int Tray = 1;                 // hide to tray when minimized
        public int FocusGame = 1;            // switch to StarCitizen.exe before each AFK input
        public int AcceptMission = 0;        // auto-accept shared missions/contracts (OCR)
        public int AcceptParty = 0;          // auto-accept party invites (OCR)
        public int AcceptVk = (int)Keys.OemOpenBrackets; // key that accepts a prompt: [
        public int ScanMs = 800;             // how often the screen is read
        public int Debug = 0;                // 1 = save what the scanner sees next to the ini
        public string MissionWords = "contract shared";  // ';'-separated phrases that identify a mission prompt
        public string PartyWords = "party invite";       // ... and a party invite prompt

        const string FileName = "StarCitizenAntiAFK.ini";
        const string LegacyFileName = "AntiAFK.ini";

        static string[] Paths()
        {
            return new string[] {
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, FileName),
                Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "StarCitizenAntiAFK"), FileName)
            };
        }

        // Settings from the earlier "AntiAFK" build are picked up once, then saved under the new name.
        static string[] LoadPaths()
        {
            string[] p = Paths();
            return new string[] {
                p[0], p[1],
                Path.Combine(AppDomain.CurrentDomain.BaseDirectory, LegacyFileName),
                Path.Combine(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "AntiAFK"), LegacyFileName)
            };
        }

        public static Settings Load()
        {
            Settings s = new Settings();
            foreach (string p in LoadPaths())
            {
                try
                {
                    if (!File.Exists(p)) continue;
                    foreach (string line in File.ReadAllLines(p))
                    {
                        int eq = line.IndexOf('=');
                        if (eq <= 0) continue;
                        string k = line.Substring(0, eq).Trim();
                        string raw = line.Substring(eq + 1).Trim();
                        if (k == "MissionWords") { s.MissionWords = raw; continue; }
                        if (k == "PartyWords") { s.PartyWords = raw; continue; }
                        int v;
                        if (!int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out v)) continue;
                        switch (k)
                        {
                            case "IdleSeconds": s.IdleSeconds = v; break;
                            case "IntervalSeconds": s.IntervalSeconds = v; break;
                            case "Mode": s.Mode = v; break;
                            case "KeyVk": s.KeyVk = v; break;
                            case "HotVk": s.HotVk = v; break;
                            case "HotMods": s.HotMods = v; break;
                            case "Tray": s.Tray = v; break;
                            case "FocusGame": s.FocusGame = v; break;
                            case "AcceptMission": s.AcceptMission = v; break;
                            case "AcceptParty": s.AcceptParty = v; break;
                            case "AcceptVk": s.AcceptVk = v; break;
                            case "ScanMs": s.ScanMs = v; break;
                            case "Debug": s.Debug = v; break;
                        }
                    }
                    break;
                }
                catch { }
            }
            s.IdleSeconds = Math.Max(1, Math.Min(999 * 60 + 59, s.IdleSeconds));
            s.IntervalSeconds = Math.Max(1, Math.Min(3600, s.IntervalSeconds));
            s.Mode = s.Mode == 1 ? 1 : 0;
            if (s.KeyVk < 1 || s.KeyVk > 254) s.KeyVk = (int)Keys.Space;
            if (s.HotVk < 1 || s.HotVk > 254) s.HotVk = (int)Keys.F9;
            s.HotMods &= 7;
            s.Tray = s.Tray == 0 ? 0 : 1;
            s.FocusGame = s.FocusGame == 0 ? 0 : 1;
            s.AcceptMission = s.AcceptMission == 1 ? 1 : 0;
            s.AcceptParty = s.AcceptParty == 1 ? 1 : 0;
            if (s.AcceptVk < 1 || s.AcceptVk > 254) s.AcceptVk = (int)Keys.OemOpenBrackets;
            s.ScanMs = Math.Max(300, Math.Min(5000, s.ScanMs));
            s.Debug = s.Debug == 1 ? 1 : 0;
            if (string.IsNullOrEmpty(s.MissionWords)) s.MissionWords = "contract shared";
            if (string.IsNullOrEmpty(s.PartyWords)) s.PartyWords = "party invite";
            return s;
        }

        /// <summary>A writable folder next to the settings file (used only for the optional debug dump).</summary>
        public static string DataDir()
        {
            foreach (string p in Paths())
            {
                string d = Path.GetDirectoryName(p);
                try
                {
                    Directory.CreateDirectory(d);
                    string probe = Path.Combine(d, ".write-test");
                    File.WriteAllText(probe, "x");
                    File.Delete(probe);
                    return d;
                }
                catch { }
            }
            return Path.GetTempPath();
        }

        public void Save()
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("IdleSeconds=" + IdleSeconds);
            sb.AppendLine("IntervalSeconds=" + IntervalSeconds);
            sb.AppendLine("Mode=" + Mode);
            sb.AppendLine("KeyVk=" + KeyVk);
            sb.AppendLine("HotVk=" + HotVk);
            sb.AppendLine("HotMods=" + HotMods);
            sb.AppendLine("Tray=" + Tray);
            sb.AppendLine("FocusGame=" + FocusGame);
            sb.AppendLine("AcceptMission=" + AcceptMission);
            sb.AppendLine("AcceptParty=" + AcceptParty);
            sb.AppendLine("AcceptVk=" + AcceptVk);
            sb.AppendLine("ScanMs=" + ScanMs);
            sb.AppendLine("Debug=" + Debug);
            sb.AppendLine("MissionWords=" + MissionWords);
            sb.AppendLine("PartyWords=" + PartyWords);
            foreach (string p in Paths())
            {
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(p));
                    File.WriteAllText(p, sb.ToString());
                    return;
                }
                catch { }
            }
        }
    }

    // ------------------------------------------------------------ Idle tracking
    /// <summary>
    /// Tracks when the USER last touched the keyboard/mouse. Input that the app injected itself (AFK keys,
    /// wiggles, auto-accept presses) is recognised by its timestamp and ignored, so it never resets the idle
    /// countdown and never counts as the user coming back.
    /// </summary>
    sealed class IdleTracker
    {
        public uint LastReal;
        uint lastSeen;

        public void Reset(uint lastInput) { LastReal = lastSeen = lastInput; }

        /// <summary>Raw-input mode: a physical keyboard/mouse event happened at this tick.</summary>
        public void MarkReal(uint tick) { LastReal = tick; }

        /// <summary>Feed the latest system last-input tick plus the tick our own injection produced. True if it was the user.</summary>
        public bool Observe(uint lastInput, uint ownTick)
        {
            if (lastInput == lastSeen) return false;
            lastSeen = lastInput;
            int d = unchecked((int)(lastInput - ownTick));
            if (d >= 0 && d <= 250) return false;   // produced by us (allowing a little registration lag)
            LastReal = lastInput;
            return true;
        }

        public uint Idle(uint nowTick) { return unchecked(nowTick - LastReal); }
    }

    // --------------------------------------------------------- Text matching
    static class Matcher
    {
        /// <summary>Lower-case letters and digits only, so OCR spacing/punctuation noise doesn't matter.</summary>
        public static string Compact(string s)
        {
            if (s == null) return "";
            StringBuilder sb = new StringBuilder(s.Length);
            foreach (char c in s)
                if (char.IsLetterOrDigit(c)) sb.Append(char.ToLowerInvariant(c));
            return sb.ToString();
        }

        /// <summary>True if needle occurs in hay with at most maxErr single-character edits (Sellers' algorithm).</summary>
        public static bool FuzzyContains(string hay, string needle, int maxErr)
        {
            int m = needle.Length;
            if (m == 0) return false;
            int[] prev = new int[m + 1], cur = new int[m + 1];
            for (int i = 0; i <= m; i++) prev[i] = i;
            if (prev[m] <= maxErr) return true;
            for (int j = 0; j < hay.Length; j++)
            {
                cur[0] = 0; // a match may start anywhere in the text
                for (int i = 1; i <= m; i++)
                {
                    int cost = needle[i - 1] == hay[j] ? 0 : 1;
                    int v = prev[i - 1] + cost;
                    if (prev[i] + 1 < v) v = prev[i] + 1;
                    if (cur[i - 1] + 1 < v) v = cur[i - 1] + 1;
                    cur[i] = v;
                }
                if (cur[m] <= maxErr) return true;
                int[] t = prev; prev = cur; cur = t;
            }
            return false;
        }

        /// <summary>Does the OCR text contain any of the ';'-separated phrases (a couple of typos tolerated)?</summary>
        public static bool ContainsAny(string ocrText, string phrases)
        {
            string hay = Compact(ocrText);
            if (hay.Length == 0 || string.IsNullOrEmpty(phrases)) return false;
            foreach (string p in phrases.Split(new char[] { ';' }))
            {
                string n = Compact(p);
                if (n.Length < 4) continue;
                if (FuzzyContains(hay, n, n.Length / 5)) return true;
            }
            return false;
        }

        /// <summary>
        /// Pulls the contract's name out of the OCR text of a "Contract Shared: ..." prompt: everything after the
        /// first colon on the line that holds the title. Returns "" if the name couldn't be read.
        /// </summary>
        public static string ContractName(string ocrText, string phrases)
        {
            if (string.IsNullOrEmpty(ocrText)) return "";
            foreach (string raw in ocrText.Split(new char[] { (char)10 }))
            {
                string line = raw.Trim();
                if (!ContainsAny(line, phrases)) continue;
                int i = line.IndexOfAny(new char[] { ':', ';' });
                if (i < 0 || i + 1 >= line.Length) return "";
                string name = line.Substring(i + 1).Trim();
                name = Regex.Replace(name, @"\s+", " ");
                return name.Trim(' ', '.', ',', '-', '|');
            }
            return "";
        }

        /// <summary>ACCEPT / DECLINE button labels visible, i.e. some prompt is on screen.</summary>
        public static bool LooksLikePrompt(string ocrText)
        {
            string c = Compact(ocrText);
            return FuzzyContains(c, "decline", 1) || FuzzyContains(c, "accept", 1);
        }
    }

    // ------------------------------------------------------------ Image helpers
    static class ImageOps
    {
        /// <summary>
        /// The HUD text is near-white on a darker pill. Bright pixels become black and everything else white,
        /// which is what OCR engines read best. Buffers are tightly packed BGRA.
        /// </summary>
        public static byte[] BrightToBlack(byte[] bgra, int threshold, out int maxLuma)
        {
            byte[] o = new byte[bgra.Length];
            int mx = 0;
            for (int i = 0; i + 3 < bgra.Length; i += 4)
            {
                int y = (bgra[i + 2] * 299 + bgra[i + 1] * 587 + bgra[i] * 114) / 1000;
                if (y > mx) mx = y;
                byte v = (byte)(y >= threshold ? 0 : 255);
                o[i] = v; o[i + 1] = v; o[i + 2] = v; o[i + 3] = 255;
            }
            maxLuma = mx;
            return o;
        }

        /// <summary>Copy with alpha forced to opaque (screen grabs can carry alpha 0).</summary>
        public static byte[] Opaque(byte[] bgra)
        {
            byte[] o = (byte[])bgra.Clone();
            for (int i = 3; i < o.Length; i += 4) o[i] = 255;
            return o;
        }

        public static byte[] ToBgra(Bitmap bmp)
        {
            BitmapData d = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            try
            {
                byte[] raw = new byte[bmp.Width * 4 * bmp.Height];
                if (d.Stride == bmp.Width * 4) Marshal.Copy(d.Scan0, raw, 0, raw.Length);
                else
                    for (int y = 0; y < bmp.Height; y++)
                        Marshal.Copy(new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), raw, y * bmp.Width * 4, bmp.Width * 4);
                return raw;
            }
            finally { bmp.UnlockBits(d); }
        }

        public static void SavePng(byte[] bgra, int w, int h, string path)
        {
            using (Bitmap b = new Bitmap(w, h, PixelFormat.Format32bppArgb))
            {
                BitmapData d = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
                try { Marshal.Copy(bgra, 0, d.Scan0, Math.Min(bgra.Length, Math.Abs(d.Stride) * h)); }
                finally { b.UnlockBits(d); }
                b.Save(path, ImageFormat.Png);
            }
        }
    }

    // -------------------------------------------------- Windows built-in OCR
    // Windows 10/11 ship an OCR engine (Windows.Media.Ocr). It is reached through reflection so the app needs
    // no extra files, no Windows SDK and no references beyond the .NET Framework that comes with Windows.
    static class WinOcr
    {
        static readonly object gate = new object();
        static bool tried;
        static int triedAt;
        static string error = "";
        static object engine;
        static Type tResult;
        static MethodInfo miRecognize, miCreateBitmap, miAsBuffer, miAsTaskDef;
        static object pixBgra8, alphaPremul;
        static int createArgs;

        public static string Error { get { return error; } }

        public static string Short(Exception ex)
        {
            while (ex.InnerException != null && (ex is TargetInvocationException || ex is AggregateException)) ex = ex.InnerException;
            string m = ex.Message.Replace("\r", " ").Replace("\n", " ");
            return m.Length > 110 ? m.Substring(0, 110) + "..." : m;
        }

        static Type WinRt(string fullName)
        {
            string ns = fullName.Substring(0, fullName.LastIndexOf('.'));
            Type t = Type.GetType(fullName + ", " + ns + ", ContentType=WindowsRuntime", false);
            if (t == null) t = Type.GetType(fullName + ", Windows.Foundation, ContentType=WindowsRuntime", false);
            if (t == null) throw new InvalidOperationException("Windows type not found: " + fullName);
            return t;
        }

        static bool Init()
        {
            lock (gate)
            {
                if (engine != null) return true;
                // After a failure, try again every 30 s (e.g. once a language pack has been installed).
                if (tried && unchecked(Environment.TickCount - triedAt) < 30000) return false;
                tried = true; triedAt = Environment.TickCount;
                try
                {
                    Assembly rt;
                    try { rt = Assembly.Load("System.Runtime.WindowsRuntime, Version=4.0.0.0, Culture=neutral, PublicKeyToken=b77a5c561934e089"); }
                    catch { rt = Assembly.Load("System.Runtime.WindowsRuntime"); }
                    Type ext = rt.GetType("System.WindowsRuntimeSystemExtensions", true);
                    Type bufExt = rt.GetType("System.Runtime.InteropServices.WindowsRuntime.WindowsRuntimeBufferExtensions", true);
                    miAsBuffer = bufExt.GetMethod("AsBuffer", new Type[] { typeof(byte[]) });
                    foreach (MethodInfo m in ext.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "AsTask" || !m.IsGenericMethodDefinition) continue;
                        ParameterInfo[] ps = m.GetParameters();
                        if (ps.Length != 1 || m.GetGenericArguments().Length != 1) continue;
                        Type pt = ps[0].ParameterType;
                        if (pt.IsGenericType && pt.GetGenericTypeDefinition().Name == "IAsyncOperation`1") { miAsTaskDef = m; break; }
                    }
                    if (miAsBuffer == null || miAsTaskDef == null) { error = "Windows runtime helpers not found"; return false; }

                    Type tEngine = WinRt("Windows.Media.Ocr.OcrEngine");
                    object eng = tEngine.GetMethod("TryCreateFromUserProfileLanguages", BindingFlags.Public | BindingFlags.Static).Invoke(null, null);
                    if (eng == null)
                    {
                        object lang = Activator.CreateInstance(WinRt("Windows.Globalization.Language"), new object[] { "en-US" });
                        eng = tEngine.GetMethod("TryCreateFromLanguage", BindingFlags.Public | BindingFlags.Static).Invoke(null, new object[] { lang });
                    }
                    if (eng == null) { error = "no OCR language installed (add English in Windows language settings)"; return false; }

                    miRecognize = tEngine.GetMethod("RecognizeAsync");
                    tResult = miRecognize.ReturnType.GetGenericArguments()[0];
                    Type tBmp = WinRt("Windows.Graphics.Imaging.SoftwareBitmap");
                    pixBgra8 = Enum.ToObject(WinRt("Windows.Graphics.Imaging.BitmapPixelFormat"), 87);   // Bgra8
                    alphaPremul = Enum.ToObject(WinRt("Windows.Graphics.Imaging.BitmapAlphaMode"), 0);   // Premultiplied
                    foreach (MethodInfo m in tBmp.GetMethods(BindingFlags.Public | BindingFlags.Static))
                    {
                        if (m.Name != "CreateCopyFromBuffer") continue;
                        int n = m.GetParameters().Length;
                        if (n == 5 || (n == 4 && miCreateBitmap == null)) { miCreateBitmap = m; createArgs = n; }
                    }
                    if (miCreateBitmap == null) { error = "SoftwareBitmap support not found"; return false; }

                    engine = eng;
                    error = "";
                    return true;
                }
                catch (Exception ex) { error = Short(ex); engine = null; return false; }
            }
        }

        /// <summary>Reads text from a BGRA image. Returns null if OCR isn't available (see Error).</summary>
        public static string Recognize(byte[] bgra, int w, int h)
        {
            if (!Init()) return null;
            object bmp = null;
            try
            {
                object buf = miAsBuffer.Invoke(null, new object[] { bgra });
                bmp = miCreateBitmap.Invoke(null, createArgs == 5 ? new object[] { buf, pixBgra8, w, h, alphaPremul } : new object[] { buf, pixBgra8, w, h });
                object op = miRecognize.Invoke(engine, new object[] { bmp });
                Task t = (Task)miAsTaskDef.MakeGenericMethod(tResult).Invoke(null, new object[] { op });
                if (!t.Wait(4000)) { error = "OCR timed out"; return null; }
                object res = t.GetType().GetProperty("Result").GetValue(t, null);
                object text = res.GetType().GetProperty("Text").GetValue(res, null);
                return text as string ?? "";
            }
            catch (Exception ex) { error = Short(ex); return null; }
            finally
            {
                IDisposable d = bmp as IDisposable;
                if (d != null) { try { d.Dispose(); } catch { } }
            }
        }
    }

    // ------------------------------------------------------------------- Theme
    static class Theme
    {
        public static float Scale = 1f;
        public static int S(int v) { return (int)Math.Round(v * Scale); }

        public static readonly Color Bg = Color.FromArgb(17, 19, 24);
        public static readonly Color Card = Color.FromArgb(26, 29, 36);
        public static readonly Color Border = Color.FromArgb(42, 46, 56);
        public static readonly Color Field = Color.FromArgb(34, 38, 47);
        public static readonly Color Text = Color.FromArgb(230, 232, 236);
        public static readonly Color Muted = Color.FromArgb(139, 144, 160);
        public static readonly Color Accent = Color.FromArgb(76, 141, 255);
        public static readonly Color Green = Color.FromArgb(61, 214, 140);
        public static readonly Color Amber = Color.FromArgb(245, 185, 66);
        public static readonly Color Grey = Color.FromArgb(110, 115, 130);
        public static readonly Color Stop = Color.FromArgb(229, 83, 83);

        public static Font Regular = new Font("Segoe UI", 9.5f);
        public static Font Semi = new Font("Segoe UI Semibold", 9.5f);
        public static Font Big = new Font("Segoe UI Semibold", 11.5f);
        public static Font Title = new Font("Segoe UI Semibold", 16f);
        public static Font Small = new Font("Segoe UI", 8.5f);
    }

    static class Gfx
    {
        public static GraphicsPath Round(Rectangle r, int rad)
        {
            GraphicsPath p = new GraphicsPath();
            int d = Math.Min(rad * 2, Math.Min(r.Width, r.Height));
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Rectangle r, int rad, Color fill, Color border)
        {
            using (GraphicsPath p = Round(r, rad))
            {
                using (SolidBrush b = new SolidBrush(fill)) g.FillPath(b, p);
                if (border != Color.Empty)
                    using (Pen pen = new Pen(border, 1f)) g.DrawPath(pen, p);
            }
        }

        public static string KeyName(Keys k)
        {
            switch (k)
            {
                case Keys.Return: return "Enter";
                case Keys.Back: return "Backspace";
                case Keys.Prior: return "Page Up";
                case Keys.Next: return "Page Down";
                case Keys.ControlKey: return "Ctrl";
                case Keys.ShiftKey: return "Shift";
                case Keys.Menu: return "Alt";
                case Keys.Capital: return "Caps Lock";
                case Keys.Oemtilde: return "`";
                case Keys.OemMinus: return "-";
                case Keys.Oemplus: return "=";
                case Keys.OemOpenBrackets: return "[";
                case Keys.Oem6: return "]";
                case Keys.Oem5: return "\\";
                case Keys.Oem1: return ";";
                case Keys.Oem7: return "'";
                case Keys.Oemcomma: return ",";
                case Keys.OemPeriod: return ".";
                case Keys.OemQuestion: return "/";
                case Keys.Divide: return "Num /";
                case Keys.Multiply: return "Num *";
                case Keys.Subtract: return "Num -";
                case Keys.Add: return "Num +";
                case Keys.Decimal: return "Num .";
            }
            if (k >= Keys.D0 && k <= Keys.D9) return ((int)k - (int)Keys.D0).ToString();
            if (k >= Keys.NumPad0 && k <= Keys.NumPad9) return "Num " + ((int)k - (int)Keys.NumPad0);
            return k.ToString();
        }

        public static string HotkeyName(int mods, Keys k)
        {
            string s = "";
            if ((mods & 2) != 0) s += "Ctrl + ";
            if ((mods & 1) != 0) s += "Alt + ";
            if ((mods & 4) != 0) s += "Shift + ";
            return s + KeyName(k);
        }
    }

    // ---------------------------------------------------------------- Controls
    /// <summary>Base for flat owner-drawn controls (double-buffered, opaque).</summary>
    abstract class FlatControl : Control
    {
        public Color Surface = Theme.Card;
        protected FlatControl()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            SetStyle(ControlStyles.SupportsTransparentBackColor, false);
            Font = Theme.Regular;
        }
        protected void Prepare(PaintEventArgs e)
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            e.Graphics.Clear(Surface);
        }
    }

    sealed class Card : FlatControl
    {
        public Card() { Surface = Theme.Bg; SetStyle(ControlStyles.Selectable, false); TabStop = false; }
        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e);
            Gfx.FillRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(10), Theme.Card, Theme.Border);
        }
    }

    sealed class StatusCard : FlatControl
    {
        Color dot = Theme.Grey; string title = "", detail = "";
        public StatusCard() { Surface = Theme.Bg; SetStyle(ControlStyles.Selectable, false); TabStop = false; }
        public void Set(Color c, string t, string d)
        {
            if (c == dot && t == title && d == detail) return;
            dot = c; title = t; detail = d; Invalidate();
        }
        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e);
            Graphics g = e.Graphics;
            Gfx.FillRound(g, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(10), Theme.Card, Theme.Border);
            int d = Theme.S(14), cx = Theme.S(20), cy = Height / 2 - d / 2;
            using (SolidBrush glow = new SolidBrush(Color.FromArgb(50, dot))) g.FillEllipse(glow, cx - Theme.S(5), cy - Theme.S(5), d + Theme.S(10), d + Theme.S(10));
            using (SolidBrush b = new SolidBrush(dot)) g.FillEllipse(b, cx, cy, d, d);
            int tx = cx + d + Theme.S(16);
            TextRenderer.DrawText(g, title, Theme.Big, new Rectangle(tx, Theme.S(11), Width - tx - Theme.S(10), Theme.S(26)),
                Theme.Text, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
            TextRenderer.DrawText(g, detail, Theme.Regular, new Rectangle(tx, Theme.S(37), Width - tx - Theme.S(10), Theme.S(20)),
                Theme.Muted, TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    sealed class BigButton : FlatControl
    {
        bool hover, down, running;
        public BigButton() { Surface = Theme.Bg; Cursor = Cursors.Hand; }
        public bool Running { get { return running; } set { if (running != value) { running = value; Invalidate(); } } }
        protected override void OnMouseEnter(EventArgs e) { hover = true; Invalidate(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; Invalidate(); base.OnMouseLeave(e); }
        protected override void OnMouseDown(MouseEventArgs e) { down = true; Focus(); Invalidate(); base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Enter || k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) OnClick(EventArgs.Empty); base.OnKeyUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e);
            Color baseC = running ? Theme.Stop : Theme.Accent;
            int adj = down ? -25 : (hover ? 18 : 0);
            Color c = Color.FromArgb(Math.Max(0, Math.Min(255, baseC.R + adj)), Math.Max(0, Math.Min(255, baseC.G + adj)), Math.Max(0, Math.Min(255, baseC.B + adj)));
            Gfx.FillRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(10), c, Color.Empty);
            TextRenderer.DrawText(e.Graphics, running ? "Stop" : "Start", Theme.Big, new Rectangle(0, 0, Width, Height),
                Color.White, TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding);
        }
    }

    sealed class SwitchControl : FlatControl
    {
        bool on;
        public event EventHandler CheckedChanged;
        public SwitchControl() { Cursor = Cursors.Hand; }
        public bool Checked
        {
            get { return on; }
            set { if (on != value) { on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); } }
        }
        protected override void OnClick(EventArgs e) { Checked = !on; base.OnClick(e); }
        protected override bool IsInputKey(Keys k) { return k == Keys.Space || base.IsInputKey(k); }
        protected override void OnKeyUp(KeyEventArgs e) { if (e.KeyCode == Keys.Space) Checked = !on; base.OnKeyUp(e); }
        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e);
            Graphics g = e.Graphics;
            Rectangle track = new Rectangle(0, 0, Width - 1, Height - 1);
            Gfx.FillRound(g, track, Height / 2, on ? Theme.Accent : Theme.Field, on ? Color.Empty : Theme.Border);
            int pad = Theme.S(3), d = Height - 1 - pad * 2;
            int x = on ? Width - 1 - pad - d : pad;
            using (SolidBrush b = new SolidBrush(Color.White)) g.FillEllipse(b, x, pad, d, d);
            if (Focused)
                using (Pen p = new Pen(Color.FromArgb(120, Theme.Accent), 1f))
                using (GraphicsPath fp = Gfx.Round(new Rectangle(0, 0, Width - 1, Height - 1), Height / 2))
                    g.DrawPath(p, fp);
        }
    }

    sealed class NumField : FlatControl
    {
        readonly TextBox tb = new TextBox();
        readonly int min, max;
        int val;
        bool busy;
        public event EventHandler ValueChanged;

        public NumField(int min, int max, int value)
        {
            this.min = min; this.max = max; val = value;
            tb.BorderStyle = BorderStyle.None;
            tb.BackColor = Theme.Field;
            tb.ForeColor = Theme.Text;
            tb.Font = Theme.Regular;
            tb.TextAlign = HorizontalAlignment.Center;
            tb.MaxLength = max.ToString().Length;
            tb.Text = value.ToString();
            Controls.Add(tb);
            tb.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            tb.TextChanged += delegate { OnText(); };
            tb.GotFocus += delegate { Invalidate(); tb.SelectAll(); };
            tb.LostFocus += delegate { busy = true; tb.Text = val.ToString(); busy = false; Invalidate(); };
            tb.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Up) { Step(1); e.Handled = true; }
                else if (e.KeyCode == Keys.Down) { Step(-1); e.Handled = true; }
            };
            tb.MouseWheel += delegate(object s, MouseEventArgs e) { Step(e.Delta > 0 ? 1 : -1); };
            SetStyle(ControlStyles.Selectable, false);
        }

        public int Value
        {
            get { return val; }
            set
            {
                int v = Math.Max(min, Math.Min(max, value));
                busy = true; tb.Text = v.ToString(); busy = false;
                if (v != val) { val = v; if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
            }
        }

        void Step(int d) { Value = val + d; tb.SelectAll(); }

        void OnText()
        {
            if (busy) return;
            int v;
            if (!int.TryParse(tb.Text, out v)) v = min;
            int c = Math.Max(min, Math.Min(max, v));
            if (c != v) { busy = true; tb.Text = c.ToString(); tb.SelectionStart = tb.Text.Length; busy = false; }
            if (c != val) { val = c; if (ValueChanged != null) ValueChanged(this, EventArgs.Empty); }
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            tb.Width = Width - Theme.S(16);
            tb.Left = Theme.S(8);
            tb.Top = Math.Max(0, (Height - tb.Height) / 2);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e);
            Gfx.FillRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(7), Theme.Field, tb.Focused ? Theme.Accent : Theme.Border);
        }
    }

    /// <summary>Click, then press a key (optionally with Ctrl/Alt/Shift) to capture it. Esc cancels.</summary>
    sealed class KeyCapture : FlatControl
    {
        public bool AllowMods;
        public Keys Key;
        public int Mods;
        bool capturing;
        public event EventHandler Changed, CaptureBegan, CaptureEnded;

        public KeyCapture(bool allowMods) { AllowMods = allowMods; Cursor = Cursors.Hand; }

        public void Set(Keys k, int mods) { Key = k; Mods = mods; Invalidate(); }

        void End(bool changed)
        {
            capturing = false; Invalidate();
            if (changed && Changed != null) Changed(this, EventArgs.Empty);
            if (CaptureEnded != null) CaptureEnded(this, EventArgs.Empty);
        }

        protected override void OnClick(EventArgs e)
        {
            if (!Enabled) return;
            Focus();
            if (!capturing) { capturing = true; Invalidate(); if (CaptureBegan != null) CaptureBegan(this, EventArgs.Empty); }
            base.OnClick(e);
        }

        protected override void OnLostFocus(EventArgs e) { if (capturing) End(false); base.OnLostFocus(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (!capturing) return base.ProcessCmdKey(ref msg, keyData);
            Keys k = keyData & Keys.KeyCode;
            int mods = 0;
            if ((keyData & Keys.Alt) != 0) mods |= 1;
            if ((keyData & Keys.Control) != 0) mods |= 2;
            if ((keyData & Keys.Shift) != 0) mods |= 4;
            if (k == Keys.Escape) { End(false); return true; }
            if (AllowMods)
            {
                if (k == Keys.ControlKey || k == Keys.ShiftKey || k == Keys.Menu || k == Keys.LWin || k == Keys.RWin) return true; // wait for the main key
                Key = k; Mods = mods;
            }
            else { Key = k; Mods = 0; }
            End(true);
            return true;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Prepare(e);
            Color border = capturing ? Theme.Accent : Theme.Border;
            Gfx.FillRound(e.Graphics, new Rectangle(0, 0, Width - 1, Height - 1), Theme.S(7), Theme.Field, border);
            string text = capturing ? "Press a key..." : (AllowMods ? Gfx.HotkeyName(Mods, Key) : Gfx.KeyName(Key));
            Color fc = !Enabled ? Color.FromArgb(90, 95, 108) : (capturing ? Theme.Accent : Theme.Text);
            TextRenderer.DrawText(e.Graphics, text, Theme.Semi, new Rectangle(0, 0, Width, Height), fc,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPadding);
        }
    }

    // --------------------------------------------------------------- Main form
    sealed class MainForm : Form
    {
        const int HOTKEY_ID = 0xA7A1;
        enum RunState { Stopped = 0, Waiting = 1, Afk = 2 }

        readonly Settings cfg = Settings.Load();
        RunState state = RunState.Stopped;
        readonly System.Windows.Forms.Timer timer = new System.Windows.Forms.Timer();
        readonly Stopwatch clock = Stopwatch.StartNew();
        readonly Icon[] icons = new Icon[3];
        uint ownTick;          // LastInputTick value produced by our own injected input
        readonly IdleTracker idleT = new IdleTracker();
        uint realAtAfk;        // IdleTracker.LastReal when AFK mode began; a change means the user is back
        long nextFireMs;
        int sent;
        FocusResult focus = FocusResult.NotRunning;
        bool hotkeyOk = true;

        // Invite scanner (runs on its own thread; see ScanLoop)
        readonly object injectLock = new object();   // serialises every SendInput burst + its fingerprint
        Thread scanThread;
        volatile bool running, closing;
        long lastAcceptMs = -100000;
        int accepted, blankScans;
        string acceptedNote = "", lastScanText = "";
        // Physical-input tracking (Raw Input). Joysticks and software-injected input never reach this.
        bool rawOk;                 // raw input registered; otherwise fall back to GetLastInputInfo
        uint physTick, lastPhysSeen;
        IntPtr rawBuf = Marshal.AllocHGlobal(64);

        readonly List<string> recentContracts = new List<string>();   // newest first, at most MaxRecent
        const int MaxRecent = 5;
        bool promptUp;         // an accept key was already sent for the prompt that is (probably) still on screen
        int clearScans;        // consecutive scans without a recognised prompt

        StatusCard status;
        BigButton btn;
        NumField nfMin, nfSec, nfInt;
        SwitchControl swMode, swTray, swFocus, swMission, swParty;
        Label lblScan, lblRecent;
        KeyCapture kcKey, kcHot;
        Label lblKeyboard, lblMouse, lblKeyRow;
        NotifyIcon tray;
        ToolStripMenuItem miToggle;

        static int S(int v) { return Theme.S(v); }

        public MainForm()
        {
            SuspendLayout();
            Text = "Star Citizen Anti AFK";
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedSingle;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            Font = Theme.Regular;
            ClientSize = new Size(S(400), S(846));
            if (Height > Screen.PrimaryScreen.WorkingArea.Height) AutoScroll = true; // small/high-DPI screens

            icons[0] = MakeIcon(Theme.Grey);
            icons[1] = MakeIcon(Theme.Amber);
            icons[2] = MakeIcon(Theme.Green);
            Icon = icons[0];

            BuildUi();
            BuildTray();

            timer.Interval = 250;
            timer.Tick += delegate { Tick(); };

            Resize += delegate
            {
                if (WindowState == FormWindowState.Minimized && cfg.Tray == 1) Hide();
            };
            FormClosing += delegate { Shutdown(); };

            ResumeLayout(false);
            RefreshUi();

            scanThread = new Thread(ScanLoop);
            scanThread.IsBackground = true;
            scanThread.Name = "InviteScanner";
            scanThread.Start();
        }

        // ---------------------------------------------------------- UI building
        Label MakeLabel(Control parent, string text, int x, int y, int w, int h, Color color, Font font, ContentAlignment align, Color back)
        {
            Label l = new Label();
            l.Text = text; l.AutoSize = false;
            l.SetBounds(S(x), S(y), S(w), S(h));
            l.ForeColor = color; l.Font = font; l.TextAlign = align; l.BackColor = back;
            parent.Controls.Add(l);
            return l;
        }

        Label RowLabel(Control parent, string text, int x, int y, int w)
        {
            return MakeLabel(parent, text, x, y, w, 32, Theme.Text, Theme.Regular, ContentAlignment.MiddleLeft, Theme.Card);
        }

        T Place<T>(Control parent, T c, int x, int y, int w, int h) where T : FlatControl
        {
            c.SetBounds(S(x), S(y), S(w), S(h));
            parent.Controls.Add(c);
            return c;
        }

        void BuildUi()
        {
            MakeLabel(this, "Star Citizen Anti AFK", 18, 12, 364, 34, Theme.Text, Theme.Title, ContentAlignment.MiddleLeft, Theme.Bg);

            status = Place(this, new StatusCard(), 16, 54, 368, 68);
            btn = Place(this, new BigButton(), 16, 132, 368, 46);
            btn.Click += delegate { SetRunning(state == RunState.Stopped); };

            // --- Timing card
            Card timing = Place(this, new Card(), 16, 190, 368, 100);
            RowLabel(timing, "Go AFK after idle for", 16, 12, 150);
            nfMin = Place(timing, new NumField(0, 999, cfg.IdleSeconds / 60), 168, 12, 54, 32);
            nfMin.Surface = Theme.Card;
            RowLabel(timing, "min", 226, 12, 30);
            nfSec = Place(timing, new NumField(0, 59, cfg.IdleSeconds % 60), 258, 12, 46, 32);
            RowLabel(timing, "sec", 308, 12, 30);
            RowLabel(timing, "Send input every", 16, 56, 150);
            nfInt = Place(timing, new NumField(1, 3600, cfg.IntervalSeconds), 168, 56, 62, 32);
            RowLabel(timing, "sec", 234, 56, 30);

            // --- Action card
            Card action = Place(this, new Card(), 16, 300, 368, 144);
            RowLabel(action, "Input type", 16, 12, 90);
            lblKeyboard = MakeLabel(action, "Keyboard", 96, 12, 76, 32, Theme.Text, Theme.Semi, ContentAlignment.MiddleRight, Theme.Card);
            swMode = Place(action, new SwitchControl(), 180, 16, 46, 24);
            lblMouse = MakeLabel(action, "Mouse wiggle", 234, 12, 110, 32, Theme.Muted, Theme.Semi, ContentAlignment.MiddleLeft, Theme.Card);
            lblKeyboard.Click += delegate { swMode.Checked = false; };
            lblMouse.Click += delegate { swMode.Checked = true; };
            lblKeyboard.Cursor = Cursors.Hand; lblMouse.Cursor = Cursors.Hand;
            lblKeyRow = RowLabel(action, "Key to press", 16, 56, 150);
            kcKey = Place(action, new KeyCapture(false), 168, 56, 170, 32);
            kcKey.Set((Keys)cfg.KeyVk, 0);
            RowLabel(action, "Switch to Star Citizen first", 16, 100, 270);
            swFocus = Place(action, new SwitchControl(), 292, 104, 46, 24);
            swFocus.Checked = cfg.FocusGame == 1;

            // --- Auto-accept card
            Card inv = Place(this, new Card(), 16, 454, 368, 242);
            RowLabel(inv, "Auto-accept mission invites", 16, 10, 270);
            swMission = Place(inv, new SwitchControl(), 292, 14, 46, 24);
            swMission.Checked = cfg.AcceptMission == 1;
            RowLabel(inv, "Auto-accept party invites", 16, 46, 270);
            swParty = Place(inv, new SwitchControl(), 292, 50, 46, 24);
            swParty.Checked = cfg.AcceptParty == 1;
            lblScan = MakeLabel(inv, "Scanner idle - press Start", 16, 84, 336, 36, Theme.Muted, Theme.Small, ContentAlignment.TopLeft, Theme.Card);
            lblRecent = MakeLabel(inv, RecentText(), 16, 124, 336, 108, Theme.Muted, Theme.Small, ContentAlignment.TopLeft, Theme.Card);

            // --- Controls card
            Card ctl = Place(this, new Card(), 16, 706, 368, 100);
            RowLabel(ctl, "Start / stop hotkey", 16, 12, 150);
            kcHot = Place(ctl, new KeyCapture(true), 168, 12, 170, 32);
            kcHot.Set((Keys)cfg.HotVk, cfg.HotMods);
            RowLabel(ctl, "Hide to tray when minimized", 16, 56, 240);
            swTray = Place(ctl, new SwitchControl(), 292, 60, 46, 24);
            swTray.Checked = cfg.Tray == 1;

            MakeLabel(this, "Settings are saved next to the app. No registry entries.", 16, 814, 368, 24,
                Theme.Accent, Theme.Small, ContentAlignment.MiddleCenter, Theme.Bg);

            swMode.Checked = cfg.Mode == 1;
            ApplyModeVisuals();

            // Hook events only after initial values are applied.
            nfMin.ValueChanged += delegate { IdleChanged(); };
            nfSec.ValueChanged += delegate { IdleChanged(); };
            nfInt.ValueChanged += delegate { cfg.IntervalSeconds = nfInt.Value; cfg.Save(); };
            swMode.CheckedChanged += delegate { cfg.Mode = swMode.Checked ? 1 : 0; ApplyModeVisuals(); cfg.Save(); };
            swFocus.CheckedChanged += delegate { cfg.FocusGame = swFocus.Checked ? 1 : 0; cfg.Save(); };
            swTray.CheckedChanged += delegate { cfg.Tray = swTray.Checked ? 1 : 0; cfg.Save(); };
            swMission.CheckedChanged += delegate { cfg.AcceptMission = swMission.Checked ? 1 : 0; cfg.Save(); };
            swParty.CheckedChanged += delegate { cfg.AcceptParty = swParty.Checked ? 1 : 0; cfg.Save(); };
            kcKey.Changed += delegate { cfg.KeyVk = (int)kcKey.Key; cfg.Save(); };
            kcHot.CaptureBegan += delegate { Native.UnregisterHotKey(Handle, HOTKEY_ID); };
            kcHot.Changed += delegate { cfg.HotVk = (int)kcHot.Key; cfg.HotMods = kcHot.Mods; cfg.Save(); };
            kcHot.CaptureEnded += delegate { RegisterHotkey(); RefreshUi(); };
        }

        void IdleChanged()
        {
            cfg.IdleSeconds = Math.Max(1, nfMin.Value * 60 + nfSec.Value);
            cfg.Save();
        }

        void ApplyModeVisuals()
        {
            bool mouse = swMode.Checked;
            lblKeyboard.ForeColor = mouse ? Theme.Muted : Theme.Text;
            lblMouse.ForeColor = mouse ? Theme.Text : Theme.Muted;
            kcKey.Enabled = !mouse;
            lblKeyRow.ForeColor = mouse ? Theme.Muted : Theme.Text;
            lblKeyRow.Text = mouse ? "Wiggle: center, 10 px right, then back" : "Key to press";
            lblKeyRow.Width = S(mouse ? 336 : 150);
            kcKey.Visible = !mouse;
        }

        // ------------------------------------------------------------- Tray
        void BuildTray()
        {
            ContextMenuStrip menu = new ContextMenuStrip();
            ToolStripMenuItem miShow = new ToolStripMenuItem("Show window");
            miShow.Click += delegate { ShowWindow(); };
            miToggle = new ToolStripMenuItem("Start");
            miToggle.Click += delegate { SetRunning(state == RunState.Stopped); };
            ToolStripMenuItem miExit = new ToolStripMenuItem("Exit");
            miExit.Click += delegate { Close(); };
            menu.Items.Add(miShow);
            menu.Items.Add(miToggle);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(miExit);

            tray = new NotifyIcon();
            tray.Icon = icons[0];
            tray.Text = "Star Citizen Anti AFK";
            tray.ContextMenuStrip = menu;
            tray.DoubleClick += delegate { ShowWindow(); };
            tray.Visible = true;
        }

        void ShowWindow()
        {
            Show();
            if (WindowState == FormWindowState.Minimized) WindowState = FormWindowState.Normal;
            Activate();
        }

        static Icon MakeIcon(Color c)
        {
            using (Bitmap bmp = new Bitmap(32, 32))
            using (Graphics g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                using (SolidBrush b = new SolidBrush(Color.FromArgb(26, 29, 36))) g.FillEllipse(b, 1, 1, 30, 30);
                using (SolidBrush b = new SolidBrush(c)) g.FillEllipse(b, 7, 7, 18, 18);
                IntPtr h = bmp.GetHicon();
                Icon ic = (Icon)Icon.FromHandle(h).Clone();
                Native.DestroyIcon(h);
                return ic;
            }
        }

        // ---------------------------------------------------- Window plumbing
        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            int on = 1; // dark title bar (Win11 / Win10 20H1+)
            if (Native.DwmSetWindowAttribute(Handle, 20, ref on, 4) != 0)
                Native.DwmSetWindowAttribute(Handle, 19, ref on, 4);
            RegisterHotkey();
            RegisterRawInput();
            RefreshUi();
        }

        /// <summary>
        /// Ask Windows for raw keyboard and mouse input, delivered to this window even in the background.
        /// Events from real devices carry a device handle; input injected by software (this app's own keys,
        /// macro or joystick-mapping tools) carries none, and joysticks aren't keyboards or mice at all.
        /// </summary>
        void RegisterRawInput()
        {
            try
            {
                Native.RAWINPUTDEVICE[] d = new Native.RAWINPUTDEVICE[2];
                d[0].usUsagePage = 1; d[0].usUsage = 2; d[0].dwFlags = Native.RIDEV_INPUTSINK; d[0].hwndTarget = Handle;  // mouse
                d[1].usUsagePage = 1; d[1].usUsage = 6; d[1].dwFlags = Native.RIDEV_INPUTSINK; d[1].hwndTarget = Handle;  // keyboard
                rawOk = Native.RegisterRawInputDevices(d, 2, (uint)Marshal.SizeOf(typeof(Native.RAWINPUTDEVICE)));
            }
            catch { rawOk = false; }
        }

        void OnRawInput(IntPtr hRaw)
        {
            uint now = Native.GetTickCount();
            if (unchecked(now - physTick) < 15) return;   // mice report hundreds of times a second; one per 15 ms is plenty
            uint hdrSize = (uint)Marshal.SizeOf(typeof(Native.RAWINPUTHEADER)), size = hdrSize;
            if (Native.GetRawInputData(hRaw, Native.RID_HEADER, rawBuf, ref size, hdrSize) == uint.MaxValue) return;
            Native.RAWINPUTHEADER hdr = (Native.RAWINPUTHEADER)Marshal.PtrToStructure(rawBuf, typeof(Native.RAWINPUTHEADER));
            if (hdr.hDevice == IntPtr.Zero) return;       // injected by software, including this app's own presses
            if (hdr.dwType != Native.RIM_TYPEMOUSE && hdr.dwType != Native.RIM_TYPEKEYBOARD) return;
            physTick = now;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            Native.UnregisterHotKey(Handle, HOTKEY_ID);
            base.OnHandleDestroyed(e);
        }

        void RegisterHotkey()
        {
            if (!IsHandleCreated) return;
            Native.UnregisterHotKey(Handle, HOTKEY_ID);
            hotkeyOk = Native.RegisterHotKey(Handle, HOTKEY_ID, (uint)cfg.HotMods | Native.MOD_NOREPEAT, (uint)cfg.HotVk);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == Native.WM_HOTKEY && m.WParam.ToInt32() == HOTKEY_ID)
            {
                SetRunning(state == RunState.Stopped);
                return;
            }
            if (m.Msg == Native.WM_INPUT) OnRawInput(m.LParam);   // then fall through so Windows can clean up
            base.WndProc(ref m);
        }

        void Shutdown()
        {
            closing = true;
            running = false;
            timer.Stop();
            cfg.Save();
            try { Native.UnregisterHotKey(Handle, HOTKEY_ID); } catch { }
            tray.Visible = false;
            tray.Dispose();
        }

        // ------------------------------------------------------------ Engine
        void SetRunning(bool run)
        {
            running = run;
            if (run)
            {
                state = RunState.Waiting;
                sent = 0;
                focus = FocusResult.NotRunning;
                idleT.Reset(rawOk ? Native.GetTickCount() : Native.LastInputTick());
                lastPhysSeen = physTick;
                timer.Start();
            }
            else
            {
                timer.Stop();
                state = RunState.Stopped;
            }
            RefreshUi();
        }

        void Tick()
        {
            uint last, ownSnap;
            // An auto-accept key press may be in flight on the scanner thread. Skip this tick rather than
            // mistaking our own key press for the user.
            if (!Monitor.TryEnter(injectLock)) return;
            try { last = Native.LastInputTick(); ownSnap = ownTick; }
            finally { Monitor.Exit(injectLock); }

            bool real;
            if (rawOk)
            {
                // Only physical keyboard/mouse events count. Joysticks, macro tools and our own presses don't.
                real = physTick != lastPhysSeen;
                if (real) { lastPhysSeen = physTick; idleT.MarkReal(physTick); }
            }
            else real = idleT.Observe(last, ownSnap);   // fallback: system last-input time, ignoring our own input
            uint idle = idleT.Idle(Native.GetTickCount());
            long now = clock.ElapsedMilliseconds;
            uint thresholdMs = (uint)cfg.IdleSeconds * 1000u;

            if (state == RunState.Waiting)
            {
                if (idle >= thresholdMs)
                {
                    state = RunState.Afk;
                    sent = 0;
                    realAtAfk = idleT.LastReal;
                    nextFireMs = now + (Fire() ? cfg.IntervalSeconds * 1000L : 3000L); // retry soon if focus failed
                    RefreshUi();
                    return;
                }
            }
            else if (state == RunState.Afk)
            {
                if (idleT.LastReal != realAtAfk)
                {
                    // Something other than us produced input: the user is back.
                    state = RunState.Waiting;
                    RefreshUi();
                }
                else if (now >= nextFireMs)
                {
                    nextFireMs = now + (Fire() ? cfg.IntervalSeconds * 1000L : 3000L); // retry soon if focus failed
                }
            }
            RefreshStatus(idle, now);
        }

        /// <summary>Optionally focuses the game, then sends one AFK input. Returns false if the input was skipped.</summary>
        bool Fire()
        {
            bool ok = true;
            try
            {
                if (cfg.FocusGame == 1)
                {
                    focus = GameFocus.Activate();
                    // Star Citizen isn't running, or is running but couldn't be focused: send nothing this
                    // round rather than typing into whatever window happens to be active (e.g. a browser).
                    if (focus != FocusResult.Focused) ok = false;
                }
            }
            catch { ok = false; }

            lock (injectLock)
            {
                try
                {
                    if (ok)
                    {
                        if (cfg.Mode == 1) Injector.Wiggle(10);
                        else Injector.AfkKey((Keys)cfg.KeyVk, 50);
                        sent++;
                    }
                }
                catch { ok = false; }
                Thread.Sleep(40); // let the system register our own input before fingerprinting it
                ownTick = Native.LastInputTick();
            }
            return ok;
        }

        // ------------------------------------------------------ Invite scanner
        // Runs on a background thread: grab the top-centre of the game window (where the HUD shows
        // "Party Invite Received" / "Contract Shared" with ACCEPT [ and DECLINE ] buttons), OCR it, and
        // press [ when a prompt for an enabled option is on screen.

        void SetScan(string msg)
        {
            string full = acceptedNote.Length > 0 ? msg + "\n" + acceptedNote : msg;
            if (full == lastScanText) return;
            try
            {
                if (IsHandleCreated && !IsDisposed)
                {
                    lastScanText = full;
                    BeginInvoke(new MethodInvoker(delegate { lblScan.Text = full; }));
                }
            }
            catch { }
        }

        static string Snip(string s, int n)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            return s.Length <= n ? s : s.Substring(0, n) + "...";
        }

        void ScanLoop()
        {
            while (!closing)
            {
                try
                {
                    if (!running) SetScan("Scanner idle - press Start");
                    else if (cfg.AcceptMission == 0 && cfg.AcceptParty == 0) SetScan("Both auto-accept options are off");
                    else ScanOnce();
                }
                catch (Exception ex) { SetScan("Scan error: " + WinOcr.Short(ex)); }
                Thread.Sleep(cfg.ScanMs);
            }
        }

        byte[] Grab(Rectangle reg, double scale, out int w, out int h)
        {
            using (Bitmap src = new Bitmap(reg.Width, reg.Height, PixelFormat.Format32bppArgb))
            {
                using (Graphics g1 = Graphics.FromImage(src))
                    g1.CopyFromScreen(reg.X, reg.Y, 0, 0, reg.Size, CopyPixelOperation.SourceCopy);
                if (scale == 1.0) { w = src.Width; h = src.Height; return ImageOps.ToBgra(src); }
                w = (int)(src.Width * scale); h = (int)(src.Height * scale);
                using (Bitmap big = new Bitmap(w, h, PixelFormat.Format32bppArgb))
                {
                    using (Graphics g2 = Graphics.FromImage(big))
                    {
                        g2.InterpolationMode = InterpolationMode.HighQualityBicubic;
                        g2.PixelOffsetMode = PixelOffsetMode.HighQuality;
                        g2.DrawImage(src, new Rectangle(0, 0, w, h), 0, 0, src.Width, src.Height, GraphicsUnit.Pixel);
                    }
                    return ImageOps.ToBgra(big);
                }
            }
        }

        string Classify(string text)
        {
            if (Matcher.ContainsAny(text, cfg.PartyWords)) return "party";
            if (Matcher.ContainsAny(text, cfg.MissionWords)) return "mission";
            return null;
        }

        void ScanOnce()
        {
            IntPtr hwnd; uint pid; Rectangle rc;
            if (!GameFocus.TryGetWindow(out hwnd, out pid)) { SetScan("Star Citizen is not running"); return; }
            if (Native.IsIconic(hwnd)) { SetScan("Star Citizen is minimized - can't read the screen"); return; }
            if (!GameFocus.ClientScreenRect(hwnd, out rc) || rc.Width < 640 || rc.Height < 360) { SetScan("Can't read the Star Citizen window size"); return; }

            // The prompt sits at the top centre; read a band around it (28%-72% across, top 22%).
            Rectangle reg = new Rectangle(rc.X + (int)(rc.Width * 0.28), rc.Y, (int)(rc.Width * 0.44), (int)(rc.Height * 0.22));
            double scale = rc.Height >= 1300 ? 1.0 : 2.0;      // small HUD text reads better enlarged
            if (reg.Width * scale > 2500) scale = 2500.0 / reg.Width;

            int w, h, maxLuma;
            byte[] px = Grab(reg, scale, out w, out h);
            byte[] bw = ImageOps.BrightToBlack(px, 150, out maxLuma);
            if (maxLuma < 8)
            {
                if (++blankScans >= 3) SetScan("Capture is black - use Borderless or Windowed mode");
                return;
            }
            blankScans = 0;

            string t1 = WinOcr.Recognize(bw, w, h);
            if (t1 == null) { SetScan("OCR unavailable: " + WinOcr.Error); return; }
            string kind = Classify(t1), t2 = null, t3 = null, kindText = t1;
            if (kind == null && Matcher.LooksLikePrompt(t1))
            {
                // A prompt is on screen but its title wasn't read: retry with other image processing.
                t2 = WinOcr.Recognize(ImageOps.Opaque(px), w, h);
                if (t2 != null) { kind = Classify(t2); kindText = t2; }
                if (kind == null)
                {
                    int ml;
                    t3 = WinOcr.Recognize(ImageOps.BrightToBlack(px, 110, out ml), w, h);
                    if (t3 != null) { kind = Classify(t3); kindText = t3; }
                }
            }

            if (cfg.Debug == 1) DebugDump(px, bw, w, h, t1, t2, t3, kind);

            if (kind == null)
            {
                if (++clearScans >= 2) promptUp = false;   // prompt is gone: ready for the next one
                if (Matcher.LooksLikePrompt(t1)) SetScan("Prompt seen but not recognized: " + Snip(t1, 24));
                else SetScan("Watching for invites...");
                return;
            }
            clearScans = 0;

            bool wanted = kind == "mission" ? cfg.AcceptMission == 1 : cfg.AcceptParty == 1;
            string label = kind == "mission" ? "mission invite" : "party invite";
            if (!wanted) { SetScan("Saw a " + label + " (auto-accept for it is off)"); return; }
            Accept(label, pid, kind == "mission" ? Matcher.ContractName(kindText, cfg.MissionWords) : "");
        }

        void Accept(string label, uint pid, string contract)
        {
            long now = clock.ElapsedMilliseconds;
            if (now - lastAcceptMs < 3000) return;   // give the prompt time to go away after the key press
            if (promptUp && now - lastAcceptMs < 15000) return;   // one press per prompt; retry only if it's still up after 15 s

            if (!GameFocus.IsForeground(pid))
            {
                if (cfg.FocusGame != 1) { SetScan("Saw a " + label + " but Star Citizen isn't the active window"); return; }
                FocusResult fr = FocusResult.Failed;
                try { fr = (FocusResult)Invoke(new Func<FocusResult>(GameFocus.Activate)); } catch { }
                if (fr != FocusResult.Focused) { SetScan("Saw a " + label + " but couldn't focus Star Citizen"); return; }
            }

            lock (injectLock)
            {
                Injector.TapKey((Keys)cfg.AcceptVk, 80);
                Thread.Sleep(40);
                ownTick = Native.LastInputTick();   // so the AFK logic doesn't mistake this for the user
            }
            lastAcceptMs = now;
            promptUp = true;
            accepted++;
            acceptedNote = "Last accepted: " + label + " at " + DateTime.Now.ToString("h:mm:ss tt") + "  (" + accepted + " total)";
            if (contract.Length > 0)
            {
                lock (recentContracts)
                {
                    recentContracts.Insert(0, contract);
                    while (recentContracts.Count > MaxRecent) recentContracts.RemoveAt(recentContracts.Count - 1);
                }
                ShowRecent();
            }
            SetScan("Accepted a " + label);
        }

        string RecentText()
        {
            StringBuilder sb = new StringBuilder("Last " + MaxRecent + " accepted contracts:");
            lock (recentContracts)
            {
                if (recentContracts.Count == 0) sb.Append("\n  none yet");
                for (int i = 0; i < recentContracts.Count; i++)
                {
                    string n = recentContracts[i];
                    if (n.Length > 46) n = n.Substring(0, 45).TrimEnd(new char[] { (char)32 }) + "...";
                    sb.Append("\n" + (i + 1) + ".  " + n);
                }
            }
            return sb.ToString();
        }

        void ShowRecent()
        {
            string t = RecentText();
            try
            {
                if (IsHandleCreated && !IsDisposed) BeginInvoke(new MethodInvoker(delegate { lblRecent.Text = t; }));
            }
            catch { }
        }

        void DebugDump(byte[] raw, byte[] bw, int w, int h, string t1, string t2, string t3, string kind)
        {
            try
            {
                string dir = Path.Combine(Settings.DataDir(), "ocr-debug");
                Directory.CreateDirectory(dir);
                ImageOps.SavePng(ImageOps.Opaque(raw), w, h, Path.Combine(dir, "last_raw.png"));
                ImageOps.SavePng(bw, w, h, Path.Combine(dir, "last_bw.png"));
                File.WriteAllText(Path.Combine(dir, "last_ocr.txt"),
                    DateTime.Now.ToString("s") + "  kind=" + (kind ?? "none") + "\r\n--- bw150 ---\r\n" + t1 +
                    "\r\n--- raw ---\r\n" + t2 + "\r\n--- bw110 ---\r\n" + t3 + "\r\n");
            }
            catch { }
        }

        string FocusNote()
        {
            if (cfg.FocusGame != 1) return "";
            switch (focus)
            {
                case FocusResult.Focused: return "  |  Star Citizen focused";
                case FocusResult.Failed: return "  |  couldn't focus Star Citizen, retrying";
                default: return "  |  Star Citizen not running";
            }
        }

        // --------------------------------------------------------- Status UI
        static string Fmt(long seconds)
        {
            return (seconds / 60) + ":" + (seconds % 60).ToString("00");
        }

        void RefreshUi()
        {
            btn.Running = state != RunState.Stopped;
            miToggle.Text = state == RunState.Stopped ? "Start" : "Stop";
            Icon ic = icons[(int)state];
            Icon = ic;
            tray.Icon = ic;
            string tip = state == RunState.Stopped ? "Star Citizen Anti AFK - stopped" : (state == RunState.Waiting ? "Star Citizen Anti AFK - waiting for idle" : "Star Citizen Anti AFK - AFK input active");
            tray.Text = tip;
            RefreshStatus(idleT.Idle(Native.GetTickCount()), clock.ElapsedMilliseconds);
        }

        void RefreshStatus(uint idleMs, long now)
        {
            string hk = Gfx.HotkeyName(cfg.HotMods, (Keys)cfg.HotVk);
            switch (state)
            {
                case RunState.Stopped:
                    status.Set(hotkeyOk ? Theme.Grey : Theme.Stop, "Stopped",
                        hotkeyOk ? "Press Start or " + hk : "Hotkey " + hk + " is taken by another app");
                    break;
                case RunState.Waiting:
                    status.Set(Theme.Amber, "Waiting for you to go idle",
                        "Idle " + Fmt(idleMs / 1000) + " of " + Fmt(cfg.IdleSeconds));
                    break;
                default:
                    long left = Math.Max(0, (nextFireMs - now + 999) / 1000);
                    bool withheld = cfg.FocusGame == 1 && focus != FocusResult.Focused;
                    if (withheld)
                        status.Set(Theme.Amber, "AFK mode - no input sent",
                            (focus == FocusResult.Failed ? "Couldn't focus Star Citizen" : "Star Citizen not running") +
                            "   |   checking again in " + left + " s");
                    else
                        status.Set(Theme.Green, "AFK mode active",
                            "Sent " + sent + "  |  next in " + left + " s" + FocusNote());
                    break;
            }
        }
    }

    static class Program
    {
        [STAThread]
        static void Main()
        {
            bool created;
            using (Mutex mtx = new Mutex(true, "Local\\StarCitizenAntiAFK.SingleInstance", out created))
            {
                if (!created)
                {
                    MessageBox.Show("Star Citizen Anti AFK is already running.\n\nLook for its icon in the system tray (the ^ arrow next to the clock), " +
                        "or end \"StarCitizenAntiAFK\" in Task Manager and start it again.",
                        "Star Citizen Anti AFK", MessageBoxButtons.OK, MessageBoxIcon.Information);
                    return;
                }
                try
                {
                    using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) Theme.Scale = g.DpiX / 96f;
                    Application.EnableVisualStyles();
                    Application.SetCompatibleTextRenderingDefault(false);
                    Application.Run(new MainForm());
                }
                catch (Exception ex)
                {
                    MessageBox.Show("Star Citizen Anti AFK couldn't start:\n\n" + ex, "Star Citizen Anti AFK",
                        MessageBoxButtons.OK, MessageBoxIcon.Error);
                }
            }
        }
    }
}
