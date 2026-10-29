// PointerProbe —— 被动探针：记录本窗口收到的所有 WM_POINTER* 消息。
// 用途：对照「真实触摸板」与「SpenTablet 注入」在前台应用眼里各是什么消息流。
// 用法：启动后把鼠标光标停在本窗口上，然后用【真实触摸板】做双指捏合→停住→松开，
//       日志写到 exe 同目录 pointerprobe.log（时间戳相对启动，毫秒）。
// 注意：本程序不注入任何输入，纯只读观察。
using System;
using System.Diagnostics;
using System.IO;
using System.Windows.Forms;

class ProbeForm : Form
{
    const int WM_POINTERUPDATE = 0x0245;
    const int WM_POINTERDOWN = 0x0246;
    const int WM_POINTERUP = 0x0247;
    const int WM_POINTERENTER = 0x0249;
    const int WM_POINTERLEAVE = 0x024A;
    const int WM_POINTERCAPTURECHANGED = 0x024C;
    const int WM_MOUSEWHEEL = 0x020A;
    const int WM_MOUSEHWHEEL = 0x020E;
    const int WM_KEYDOWN = 0x0100;
    const int WM_KEYUP = 0x0101;
    const int WM_SYSKEYDOWN = 0x0104;
    const int WM_SYSKEYUP = 0x0105;
    const int VK_CONTROL = 0x11;
    const int WM_POINTERWHEEL = 0x024E;
    const int WM_POINTERHWHEEL = 0x024F;

    // POINTER_MESSAGE_* 标志位（winuser.h）
    const uint F_NEW = 0x00000001, F_INRANGE = 0x00000002, F_INCONTACT = 0x00000004;
    const uint F_PRIMARY = 0x00002000, F_CANCELED = 0x00008000;
    const uint F_DOWN = 0x00010000, F_UPDATE = 0x00020000, F_UP = 0x00040000;

    readonly double _t0 = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency;
    readonly string _logPath;
    int _nDown, _nUpdate, _nUp, _nWheel, _nOther;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    static extern bool SetProcessDPIAware();

    public ProbeForm()
    {
        SetProcessDPIAware();   // 物理像素坐标，与注入坐标同一参考系
        Text = "PointerProbe — 光标停在我上面，用真实触摸板捏合/停住/松开";
        ClientSize = new System.Drawing.Size(560, 220);
        StartPosition = FormStartPosition.Manual;
        Location = new System.Drawing.Point(100, 100);
        TopMost = true;
        _logPath = Path.Combine(AppContext.BaseDirectory, "pointerprobe.log");
        File.AppendAllText(_logPath, $"===== session start {DateTime.Now:HH:mm:ss.fff} =====\r\n");
    }

    string Name(int m) => m switch
    {
        WM_POINTERUPDATE => "PT_UPDATE",
        WM_POINTERDOWN => "PT_DOWN",
        WM_POINTERUP => "PT_UP",
        WM_POINTERENTER => "PT_ENTER",
        WM_POINTERLEAVE => "PT_LEAVE",
        WM_POINTERCAPTURECHANGED => "PT_CAPTCHG",
        WM_POINTERWHEEL => "PT_WHEEL",
        WM_POINTERHWHEEL => "PT_HWHEEL",
        WM_MOUSEWHEEL => "MS_WHEEL",
        WM_MOUSEHWHEEL => "MS_HWHEEL",
        WM_KEYDOWN => "KEY_DOWN",
        WM_KEYUP => "KEY_UP",
        WM_SYSKEYDOWN => "SYSKEY_DN",
        WM_SYSKEYUP => "SYSKEY_UP",
        _ => $"0x{m:X4}",
    };

    string Flags(uint w)
    {
        var s = "";
        if ((w & F_INCONTACT) != 0) s += " CONTACT";
        else if ((w & F_INRANGE) != 0) s += " HOVER";
        if ((w & F_DOWN) != 0) s += " DOWN";
        if ((w & F_UPDATE) != 0) s += " UPDATE";
        if ((w & F_UP) != 0) s += " UP";
        if ((w & F_CANCELED) != 0) s += " CANCELED";
        if ((w & F_PRIMARY) != 0) s += " PRIM";
        return s.Length == 0 ? " -" : s;
    }

    protected override void WndProc(ref Message m)
    {
        switch (m.Msg)
        {
            case WM_POINTERDOWN: _nDown++; break;
            case WM_POINTERUPDATE: _nUpdate++; break;
            case WM_POINTERUP: _nUp++; break;
            case WM_POINTERENTER:
            case WM_POINTERLEAVE:
            case WM_POINTERCAPTURECHANGED: _nOther++; break;
            case WM_POINTERWHEEL:
            case WM_POINTERHWHEEL:
            {
                _nWheel++;
                uint id = (uint)m.WParam.ToInt64() & 0xFFFF;
                short delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                Log($"PT_WHEEL id={id} delta={delta,6}{Flags((uint)m.WParam.ToInt64())}");
                break;
            }
            case WM_MOUSEWHEEL:
            case WM_MOUSEHWHEEL:
            {
                _nWheel++;
                short delta = (short)(((long)m.WParam >> 16) & 0xFFFF);
                bool ctrl = ((long)m.WParam & 0x0008) != 0;   // MK_CONTROL
                int x = (short)((long)m.LParam & 0xFFFF);
                int y = (short)(((long)m.LParam >> 16) & 0xFFFF);
                Log($"{Name(m.Msg),-10} delta={delta,6} ctrl={ctrl} xy=({x},{y})");
                break;
            }
            case WM_KEYDOWN:
            case WM_KEYUP:
            case WM_SYSKEYDOWN:
            case WM_SYSKEYUP:
            {
                if ((long)m.WParam == VK_CONTROL)
                    Log($"{Name(m.Msg),-10} VK_CONTROL");
                break;   // 其它按键不记，避免噪音
            }
            default:
                if (m.Msg >= 0x0240 && m.Msg <= 0x025F)   // 指针段兜底：任何漏网消息都记下来
                    Log($"RAW {Name(m.Msg)} wParam=0x{(long)m.WParam:X} lParam=0x{(long)m.LParam:X}");
                base.WndProc(ref m);
                return;   // 非指针消息走默认
        }
        if (m.Msg <= WM_POINTERCAPTURECHANGED && m.Msg >= WM_POINTERUPDATE)
        {
            uint id = (uint)m.WParam.ToInt64() & 0xFFFF;
            int x = (short)((long)m.LParam & 0xFFFF);
            int y = (short)(((long)m.LParam >> 16) & 0xFFFF);
            Log($"{Name(m.Msg),-10} id={id} xy=({x},{y}){Flags((uint)m.WParam.ToInt64())}");
        }
        Text = $"PointerProbe  DOWN={_nDown} UPDATE={_nUpdate} UP={_nUp} WHEEL={_nWheel}  日志:{_logPath}";
        base.WndProc(ref m);
    }

    void Log(string line)
    {
        double ms = Stopwatch.GetTimestamp() * 1000.0 / Stopwatch.Frequency - _t0;
        File.AppendAllText(_logPath, $"{ms,9:F1} ms  {line}\r\n");
    }

    [STAThread]
    static void Main()
    {
        Application.EnableVisualStyles();
        Application.Run(new ProbeForm());
    }
}
