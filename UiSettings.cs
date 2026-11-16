using System.Text.Json;

namespace QDesktopExtension;

/// <summary>
/// 界面自身的偏好（目前是"关窗口时怎么办"），单独存一个文件，
/// 不塞进 appconfig.json —— 那是给服务和手机端看的参数。
/// </summary>
public sealed class UiSettings
{
    /// <summary>ask = 每次询问，tray = 只关面板（服务继续），exit = 关窗就退出并停服务。</summary>
    public string CloseAction { get; set; } = "ask";

    private string _path = string.Empty;

    public static UiSettings Load(string path)
    {
        var s = new UiSettings { _path = path };
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<UiSettings>(File.ReadAllText(path));
                if (loaded != null && !string.IsNullOrWhiteSpace(loaded.CloseAction))
                    s.CloseAction = Normalize(loaded.CloseAction);
            }
        }
        catch
        {
            // 读坏了就用默认值，不影响启动
        }
        return s;
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(_path, json, new System.Text.UTF8Encoding(false));
        }
        catch
        {
        }
    }

    public static string Normalize(string action) =>
        action switch
        {
            "tray" => "tray",
            "exit" => "exit",
            _ => "ask"
        };
}
