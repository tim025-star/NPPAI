using System;
using System.IO;
using System.Text.Json;

namespace NPPAI.Config
{
    /// <summary>
    /// Plugin configuration, persisted as NPPAI.json next to the plugin DLL.
    /// All fields are public so System.Text.Json can (de)serialize them directly.
    /// </summary>
    public class PluginConfig
    {
        public bool enabled = true;
        public string model = "qwen2.5:3b";
        public bool enable_corrections = true;
        public string correction_model = "qwen3.5:9b";
        public int correction_delay_ms = 1500;
        public int correction_timeout_ms = 90000;
        public int context_chars = 1000;
        public int debounce_ms = 250;
        public bool enable_learning = true;
        public bool enable_memory = true;
        public int max_prediction_words = 6;
        public string writing_profile = "soc";
        public string[] protected_terms = Array.Empty<string>();
        public double temperature = 0.2;
        public bool enable_ghost_text = true;
        public string ghost_color = "#808080";
        public int request_timeout_ms = 20000;

        private static string _path;
        private static readonly object _pathLock = new();

        private static readonly JsonSerializerOptions JsonOpts = new()
        {
            IncludeFields = true,
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };

        /// <summary>Absolute path of the config file (next to the DLL by default).</summary>
        public static string ConfigPath
        {
            get
            {
                lock (_pathLock)
                {
                    if (_path == null)
                        _path = Path.Combine(AppContext.BaseDirectory, "NPPAI.json");
                    return _path;
                }
            }
        }

        /// <summary>Override the config location (tests / non-standard installs).</summary>
        public static void SetBaseDirectory(string dir)
        {
            lock (_pathLock)
                _path = Path.Combine(dir, "NPPAI.json");
        }

        public static PluginConfig Load()
        {
            PluginConfig cfg = null;
            try
            {
                if (File.Exists(ConfigPath))
                {
                    string json = File.ReadAllText(ConfigPath);
                    cfg = JsonSerializer.Deserialize<PluginConfig>(json, JsonOpts);
                }
            }
            catch
            {
                cfg = null; // corrupt file -> fall back to defaults
            }

            if (cfg == null)
            {
                cfg = new PluginConfig();
                cfg.Save();
            }
            cfg.Clamp();
            return cfg;
        }

        public void Save()
        {
            try
            {
                File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
            }
            catch
            {
            }
        }

        /// <summary>Clamp values into safe ranges.</summary>
        public void Clamp()
        {
            correction_delay_ms = Math.Clamp(correction_delay_ms, 500, 10000);
            if (writing_profile != "general") writing_profile = "soc";
            correction_timeout_ms = Math.Clamp(correction_timeout_ms, 1000, 180000);
            context_chars = Math.Max(100, Math.Min(4000, context_chars));
            debounce_ms = Math.Max(100, Math.Min(1000, debounce_ms));
            max_prediction_words = Math.Max(1, Math.Min(50, max_prediction_words));
            request_timeout_ms = Math.Max(1000, Math.Min(120000, request_timeout_ms));
            if (string.IsNullOrWhiteSpace(model))
                model = "qwen2.5:3b";
        }

        /// <summary>Parse a "#RRGGBB" color into 0x00BBGGRR (Scintilla BGR).</summary>
        public int GhostColorBgr()
        {
            string c = ghost_color ?? "#808080";
            if (c.StartsWith("#") && c.Length == 7)
            {
                try
                {
                    int r = Convert.ToInt32(c.Substring(1, 2), 16);
                    int g = Convert.ToInt32(c.Substring(3, 2), 16);
                    int b = Convert.ToInt32(c.Substring(5, 2), 16);
                    return (b << 16) | (g << 8) | r;
                }
                catch
                {
                }
            }
            return 0x808080;
        }
    }
}
