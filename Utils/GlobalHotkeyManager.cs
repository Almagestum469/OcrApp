using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace OcrApp.Utils
{

  public class GlobalHotkeyManager : IDisposable
  {
    // P/Invoke declarations for global keyboard hook
    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Auto, SetLastError = true)]
    private static extern IntPtr GetModuleHandle(string lpModuleName);

    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_SYSKEYDOWN = 0x0104;

    private IntPtr _hookID = IntPtr.Zero;
    private LowLevelKeyboardProc _proc;
    private Timer? _triggerDelayTimer;
    private bool _disposed = false;    // 快捷键配置
    public int TriggerHotkeyCode { get; set; } = 0; // 默认未设置
    public int TriggerDelayMs { get; set; } = 600; // 默认600毫秒延迟
    public bool IsSettingHotkey { get; set; } = false;

    // 事件定义
    public event Action<int>? HotkeyPressed; // 快捷键按下事件
    public event Action<int>? HotkeySetRequested; // 请求设置快捷键事件
    public event Action? TriggerRequested; // 触发操作事件

    public GlobalHotkeyManager()
    {
      _proc = HookCallback;
      _hookID = SetHook(_proc);
    }

    private IntPtr SetHook(LowLevelKeyboardProc proc)
    {
      using (var curProcess = System.Diagnostics.Process.GetCurrentProcess())
      using (var curModule = curProcess.MainModule)
      {
        if (curModule != null)
        {
          return SetWindowsHookEx(WH_KEYBOARD_LL, proc, GetModuleHandle(curModule.ModuleName), 0);
        }
        return IntPtr.Zero;
      }
    }

    private IntPtr HookCallback(int nCode, IntPtr wParam, IntPtr lParam)
    {
      if (nCode >= 0 && (wParam == (IntPtr)WM_KEYDOWN || wParam == (IntPtr)WM_SYSKEYDOWN))
      {
        int vkCode = Marshal.ReadInt32(lParam);

        // 如果正在设置快捷键，则捕获当前按键并设置为新的快捷键
        if (IsSettingHotkey)
        {
          HotkeySetRequested?.Invoke(vkCode);
          // 不再传递事件，避免快捷键对应用程序产生影响
          return (IntPtr)1;
        }        // 检查是否匹配当前设置的快捷键或F12键(0x7B)
        else if ((TriggerHotkeyCode != 0 && vkCode == TriggerHotkeyCode) || vkCode == 0x7B)
        {
          HotkeyPressed?.Invoke(vkCode);
          TriggerWithDelay();
        }
      }
      return CallNextHookEx(_hookID, nCode, wParam, lParam);
    }

    private void TriggerWithDelay()
    {
      // 停止之前的计时器（如果存在）
      _triggerDelayTimer?.Dispose();

      // 创建新的计时器
      _triggerDelayTimer = new Timer(
          _ => TriggerRequested?.Invoke(),
          null,
          TriggerDelayMs,
          Timeout.Infinite
      );
    }
    public static string GetKeyNameFromVirtualKey(int vkCode)
    {
      // 未设置状态
      if (vkCode == 0) return "未设置";

      // 特殊键映射
      switch (vkCode)
      {
        case 0x20: return "空格";
        case 0x1B: return "Esc";
        case 0x09: return "Tab";
        case 0x0D: return "Enter";
        case 0x08: return "Back";
        case 0x2E: return "Delete";
        case 0x25: return "←";
        case 0x26: return "↑";
        case 0x27: return "→";
        case 0x28: return "↓";
        default:
          // 对于标准字母数字键，直接转换为字符
          if ((vkCode >= 0x30 && vkCode <= 0x39) || // 0-9
              (vkCode >= 0x41 && vkCode <= 0x5A))   // A-Z
          {
            return ((char)vkCode).ToString();
          }

          // F1-F12
          if (vkCode >= 0x70 && vkCode <= 0x7B)
          {
            return $"F{vkCode - 0x6F}";
          }

          // 对于其他键，返回虚拟键码
          return $"Key({vkCode})";
      }
    }

    public void Dispose()
    {
      if (!_disposed)
      {
        UnhookWindowsHookEx(_hookID);
        _triggerDelayTimer?.Dispose();
        _disposed = true;
      }
    }
  }
}
