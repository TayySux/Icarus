using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Icarus.Core;

namespace Icarus.Native;

public sealed class WindowsInput : IInputOutput
{
    // Marker reserved for filtering our injected events in a future hook backend.
    private static readonly UIntPtr Marker = new(0x49434152u);
    public void Down(InputToken i) => Send(i, false);
    public void Up(InputToken i) => Send(i, true);
    public void Move(int x, int y) => Submit(new INPUT { Type = 0, Data = new InputUnion { Mouse = new MOUSEINPUT { Dx = x, Dy = y, Flags = 0x0001, ExtraInfo = Marker } } });
    private static void Send(InputToken i, bool up)
    {
        INPUT input;
        if (!i.Mouse)
        {
            input = new INPUT { Type = 1, Data = new InputUnion { Keyboard = new KEYBDINPUT
            { Scan = i.Code, Flags = 0x0008u | (up ? 0x0002u : 0u) | (i.Extended ? 0x0001u : 0u), ExtraInfo = Marker } } };
        }
        else
        {
            uint flags = i.Code switch { 1 => up ? 0x0004u : 0x0002u, 2 => up ? 0x0010u : 0x0008u, 3 => up ? 0x0040u : 0x0020u, 4 or 5 => up ? 0x0100u : 0x0080u, _ => throw new ArgumentOutOfRangeException(nameof(i)) };
            input = new INPUT { Type = 0, Data = new InputUnion { Mouse = new MOUSEINPUT { Flags = flags, MouseData = i.Code == 4 ? 1u : i.Code == 5 ? 2u : 0u, ExtraInfo = Marker } } };
        }
        Submit(input);
    }
    private static void Submit(INPUT input)
    {
        if (SendInput(1, [input], Marshal.SizeOf<INPUT>()) != 1)
            throw new Win32Exception(Marshal.GetLastWin32Error(), "SendInput failed. Integrity-level or input restrictions may block output.");
    }
    [StructLayout(LayoutKind.Sequential)] private struct INPUT { public uint Type; public InputUnion Data; }
    [StructLayout(LayoutKind.Explicit)] private struct InputUnion
    {
        [FieldOffset(0)] public MOUSEINPUT Mouse;
        [FieldOffset(0)] public KEYBDINPUT Keyboard;
    }
    [StructLayout(LayoutKind.Sequential)] private struct MOUSEINPUT
    {
        public int Dx, Dy; public uint MouseData, Flags, Time; public UIntPtr ExtraInfo;
    }
    [StructLayout(LayoutKind.Sequential)] private struct KEYBDINPUT
    {
        public ushort VirtualKey, Scan; public uint Flags, Time; public UIntPtr ExtraInfo;
    }
    [DllImport("user32.dll", SetLastError = true)] private static extern uint SendInput(uint count, INPUT[] inputs, int size);
}
public static class Foreground
{
    public static string ProcessName
    {
        get
        {
            GetWindowThreadProcessId(GetForegroundWindow(), out uint id);
            if (id == 0) return "";
            try { using var p = Process.GetProcessById((int)id); return p.ProcessName; }
            catch (ArgumentException) { return ""; }
            catch (Win32Exception) { return ""; }
            catch (InvalidOperationException) { return ""; }
        }
    }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint id);
}
