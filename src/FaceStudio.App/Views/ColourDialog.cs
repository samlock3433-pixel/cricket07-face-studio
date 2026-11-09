using System.Numerics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace Cricket07.FaceStudio.Views;

/// <summary>The Windows colour picker (full: colour field, hue, RGB/HSL entry, custom colours kept for the session).</summary>
internal static class ColourDialog
{
    [StructLayout(LayoutKind.Sequential)]
    private struct ChooseColor
    {
        public int lStructSize; public IntPtr hwndOwner, hInstance; public int rgbResult; public IntPtr lpCustColors;
        public int Flags; public IntPtr lCustData, lpfnHook, lpTemplateName;
    }
    [DllImport("comdlg32.dll", EntryPoint = "ChooseColorW")] private static extern bool ChooseColorW(ref ChooseColor cc);
    private static readonly int[] Custom = new int[16];

    /// <summary>Asks for a colour starting from <paramref name="current"/> (0..1 RGB); null when cancelled.</summary>
    public static Vector3? Pick(Window owner, Vector3 current)
    {
        static int Byte(float v) => (int)MathF.Round(Math.Clamp(v, 0, 1) * 255);
        var custom = Marshal.AllocHGlobal(16 * 4);
        try
        {
            Marshal.Copy(Custom, 0, custom, 16);
            var cc = new ChooseColor
            {
                lStructSize = Marshal.SizeOf<ChooseColor>(), hwndOwner = new WindowInteropHelper(owner).Handle,
                rgbResult = Byte(current.X) | Byte(current.Y) << 8 | Byte(current.Z) << 16, // COLORREF is 0x00BBGGRR
                lpCustColors = custom, Flags = 0x1 | 0x2 | 0x100,                            // RGBINIT | FULLOPEN | ANYCOLOR
            };
            bool ok = ChooseColorW(ref cc);
            Marshal.Copy(custom, Custom, 0, 16);
            return ok ? new Vector3(cc.rgbResult & 0xFF, cc.rgbResult >> 8 & 0xFF, cc.rgbResult >> 16 & 0xFF) / 255f : null;
        }
        finally { Marshal.FreeHGlobal(custom); }
    }
}
