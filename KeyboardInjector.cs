namespace QDesktopExtension;

/// <summary>
/// 键盘注入。
/// - 普通按键：虚拟键码（受目标机器键盘布局影响，适合快捷键与功能键）
/// - 文本输入：KEYEVENTF_UNICODE 直接送字符码点，不依赖键盘布局，中文/符号都能打
/// </summary>
public sealed class KeyboardInjector
{
    /// <summary>单次文本注入上限，防止一次消息塞进超长文本。</summary>
    public const int MaxTextLength = 256;

    private readonly object _sync = new();

    /// <summary>按下或抬起一个虚拟键。</summary>
    public bool Key(int vk, bool down)
    {
        if (vk <= 0 || vk > 0xFF) return false;
        var flags = down ? 0u : SendInputHelper.KEYEVENTF_KEYUP;
        if (SendInputHelper.NeedsExtendedKey(vk)) flags |= SendInputHelper.KEYEVENTF_EXTENDEDKEY;
        lock (_sync) return SendInputHelper.KeyEvent((uint)vk, 0, flags);
    }

    /// <summary>直接输入一个 UTF-16 码元（Unicode 模式，不走键盘布局）。</summary>
    public bool Char(char c, bool down)
    {
        var flags = SendInputHelper.KEYEVENTF_UNICODE;
        if (!down) flags |= SendInputHelper.KEYEVENTF_KEYUP;
        lock (_sync) return SendInputHelper.KeyEvent(0, c, flags);
    }

    /// <summary>输入一段文本（过滤控制字符，按码元逐个发送，代理对也能正常输入表情）。</summary>
    public int TypeText(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        if (text.Length > MaxTextLength) text = text.Substring(0, MaxTextLength);
        var count = 0;
        foreach (var ch in text)
        {
            if (char.IsControl(ch)) continue;
            Char(ch, true);
            Char(ch, false);
            count++;
        }
        return count;
    }
}
