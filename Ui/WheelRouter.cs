using System.Drawing;
using System.Runtime.InteropServices;

namespace OpenCord;

// Windows delivers WM_MOUSEWHEEL to the *focused* control, not the one under the pointer. Left
// alone that means a channel list only scrolls after you click it first, which reads as broken.
// Every app that feels right re-routes the wheel to whatever is under the cursor; this is that, in
// one message filter, installed once in Program.
sealed class WheelRouter : IMessageFilter
{
    const int WM_MOUSEWHEEL = 0x020A;

    [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(Point p);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    public bool PreFilterMessage(ref Message m)
    {
        if (m.Msg != WM_MOUSEWHEEL) return false;

        int lp = m.LParam.ToInt32();
        var under = Control.FromHandle(WindowFromPoint(new Point((short)(lp & 0xFFFF), (short)(lp >> 16))));

        // Not one of ours, or already the intended target: let it through untouched. The handle
        // check is what stops this from re-posting the message to itself forever.
        if (under == null || under.IsDisposed || under.Handle == m.HWnd) return false;

        SendMessage(under.Handle, WM_MOUSEWHEEL, m.WParam, m.LParam);
        return true;
    }
}
