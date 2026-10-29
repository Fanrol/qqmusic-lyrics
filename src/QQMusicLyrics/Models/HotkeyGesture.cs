using System;
using System.Collections.Generic;
using System.Text;
using System.Windows.Input;

namespace QQMusicLyrics.Models;

/// <summary>快捷键里的修饰键。</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>
/// 一个全局快捷键：修饰键 + 主键，最多 3 个键（例如 Alt+C、Ctrl+Shift+F5）。
/// 负责显示文本、持久化字符串、以及转成 RegisterHotKey 需要的修饰位与虚拟键码。
/// </summary>
public readonly record struct HotkeyGesture(HotkeyModifiers Modifiers, Key Key)
{
    /// <summary>默认快捷键：Alt + C。</summary>
    public static HotkeyGesture Default => new(HotkeyModifiers.Alt, Key.C);

    public bool IsEmpty => Key == Key.None;

    /// <summary>展示用文本，例如 "Alt + C"。</summary>
    public string Display
    {
        get
        {
            if (IsEmpty) return "（未设置）";

            var parts = new List<string>(4);
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) parts.Add("Ctrl");
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
            parts.Add(KeyName(Key));
            return string.Join(" + ", parts);
        }
    }

    public override string ToString() => Display;

    /// <summary>注册全局热键时用的修饰位（user32 的 MOD_*）。</summary>
    public uint NativeModifiers
    {
        get
        {
            uint value = 0;
            if (Modifiers.HasFlag(HotkeyModifiers.Alt)) value |= 0x0001;
            if (Modifiers.HasFlag(HotkeyModifiers.Control)) value |= 0x0002;
            if (Modifiers.HasFlag(HotkeyModifiers.Shift)) value |= 0x0004;
            if (Modifiers.HasFlag(HotkeyModifiers.Win)) value |= 0x0008;
            return value;
        }
    }

    /// <summary>注册全局热键时用的虚拟键码。</summary>
    public uint NativeVirtualKey => (uint)KeyInterop.VirtualKeyFromKey(Key);

    /// <summary>键总数（修饰键 + 主键），用来限制"最多三个键"。</summary>
    public int KeyCount => CountBits((int)Modifiers) + 1;

    /// <summary>从持久化字符串解析，例如 "Alt+C"、"Ctrl+Shift+F5"。解析失败返回 false。</summary>
    public static bool TryParse(string? text, out HotkeyGesture gesture)
    {
        gesture = default;
        if (string.IsNullOrWhiteSpace(text)) return false;

        var modifiers = HotkeyModifiers.None;
        Key key = Key.None;

        foreach (string raw in text.Split('+', StringSplitOptions.RemoveEmptyEntries))
        {
            string part = raw.Trim();
            if (part.Length == 0) continue;

            switch (part.ToLowerInvariant())
            {
                case "alt": modifiers |= HotkeyModifiers.Alt; continue;
                case "ctrl":
                case "control": modifiers |= HotkeyModifiers.Control; continue;
                case "shift": modifiers |= HotkeyModifiers.Shift; continue;
                case "win":
                case "windows": modifiers |= HotkeyModifiers.Win; continue;
            }

            if (!TryParseKey(part, out Key parsed)) return false;
            key = parsed;
        }

        if (key == Key.None) return false;
        gesture = new HotkeyGesture(modifiers, key);
        return true;
    }

    /// <summary>把设置里的字符串解析成快捷键，失败时用默认值。</summary>
    public static HotkeyGesture ParseOrDefault(string? text)
        => TryParse(text, out HotkeyGesture gesture) ? gesture : Default;

    /// <summary>键名 → WPF Key（持久化用，只覆盖常用键）。</summary>
    private static bool TryParseKey(string text, out Key key)
    {
        string upper = text.ToUpperInvariant();

        if (upper.Length == 1 && upper[0] >= 'A' && upper[0] <= 'Z')
        {
            key = (Key)Enum.Parse(typeof(Key), upper);
            return true;
        }
        if (upper.Length == 1 && upper[0] >= '0' && upper[0] <= '9')
        {
            key = (Key)Enum.Parse(typeof(Key), "D" + upper);
            return true;
        }

        if (upper.StartsWith("F") && int.TryParse(upper.AsSpan(1), out int function) && function is >= 1 and <= 24)
        {
            key = (Key)Enum.Parse(typeof(Key), upper);
            return true;
        }

        // 其余（Return / Space / Left / Escape…）直接按枚举名解析
        if (Enum.TryParse(upper, true, out key) && key != Key.None) return true;

        key = Key.None;
        return false;
    }

    /// <summary>WPF Key → 展示名（数字键显示成 1..0，其余用枚举名）。</summary>
    public static string KeyName(Key key)
    {
        if (key >= Key.D0 && key <= Key.D9) return ((char)('0' + (key - Key.D0))).ToString();
        if (key >= Key.NumPad0 && key <= Key.NumPad9) return "Num" + (key - Key.NumPad0);

        return key switch
        {
            Key.OemComma => ",",
            Key.OemPeriod => ".",
            Key.OemMinus => "-",
            Key.OemPlus => "=",
            Key.OemQuestion => "/",
            Key.OemSemicolon => ";",
            Key.OemQuotes => "'",
            Key.OemOpenBrackets => "[",
            Key.OemCloseBrackets => "]",
            Key.OemPipe => "\\",
            Key.OemTilde => "`",
            Key.Space => "Space",
            Key.Return => "Enter",
            Key.Escape => "Esc",
            Key.Back => "Backspace",
            _ => key.ToString(),
        };
    }

    private static int CountBits(int value)
    {
        int count = 0;
        while (value != 0)
        {
            count += value & 1;
            value >>= 1;
        }
        return count;
    }
}
