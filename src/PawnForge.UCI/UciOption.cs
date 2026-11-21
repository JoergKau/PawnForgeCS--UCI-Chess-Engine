using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Text;

namespace PawnForge.UCI
{
    /// <summary>
    /// UCI option model (spin/check/button/string) with change callbacks and
    /// the `option name ...` line renderer for the `uci` handshake.
    /// Represents a UCI option (spin, check, string, button, combo).
    /// UCI option infrastructure (spin/check/button/string types).
    /// </summary>
    public class UciOption
    {
        public string Name { get; init; } = string.Empty;
        public string Type { get; init; } = string.Empty;
        public string DefaultValue { get; init; } = string.Empty;
        public int? Min { get; init; }
        public int? Max { get; init; }
        public Action<string>? OnChange { get; init; }

        /// <summary>
        /// A hidden option is FUNCTIONAL but is not announced in the `uci`
        /// handshake, so no Chess-GUI lists it in its option dialog (about 80
        /// research/tuning levers would otherwise bury the twelve user-facing
        /// options). Delivery for testers: a direct `setoption name ... value
        /// ...` still resolves through <see cref="OptionsMap.SetOption"/>, and
        /// the `param:Name=value` argv override accepts them at process start.
        /// NOTE: match runners such as fastchess only send `option.*` engine
        /// arguments for options they SEE in the handshake — a hidden lever
        /// must therefore be delivered via `param:` (passed verbatim as an
        /// engine argument) or scripted stdin, not via `option.*`.
        /// </summary>
        public bool Hidden { get; set; }

        private string _currentValue = string.Empty;
        
        public string Value
        {
            get => _currentValue;
            set
            {
                _currentValue = value;
                OnChange?.Invoke(value);
            }
        }
        
        // For spin options
        public UciOption(string name, int defaultValue, int min, int max, Action<string> onChange)
        {
            Name = name;
            Type = "spin";
            DefaultValue = defaultValue.ToString();
            Min = min;
            Max = max;
            _currentValue = DefaultValue;
            OnChange = onChange;
        }
        
        // For check options
        public UciOption(string name, bool defaultValue, Action<string> onChange)
        {
            Name = name;
            Type = "check";
            DefaultValue = defaultValue ? "true" : "false";
            _currentValue = DefaultValue;
            OnChange = onChange;
        }
        
        // For string options
        public UciOption(string name, string defaultValue, Action<string> onChange)
        {
            Name = name;
            Type = "string";
            DefaultValue = defaultValue;
            _currentValue = DefaultValue;
            OnChange = onChange;
        }
        
        // For button options
        public static UciOption Button(string name, Action onPress)
        {
            return new UciOption
            {
                Name = name,
                Type = "button",
                DefaultValue = "",
                _currentValue = "",
                OnChange = _ => onPress()
            };
        }
        
        private UciOption() { }
        
        public override string ToString()
        {
            var sb = new StringBuilder();
            sb.Append($"option name {Name} type {Type}");
            
            if (Type != "button")
            {
                sb.Append($" default {DefaultValue}");
            }
            
            if (Min != null)
            {
                sb.Append($" min {Min}");
            }
            
            if (Max != null)
            {
                sb.Append($" max {Max}");
            }
            
            return sb.ToString();
        }
    }
    
    /// <summary>
    /// Manages all UCI options.
    /// </summary>
    public class OptionsMap
    {
        private readonly Dictionary<string, UciOption> _options = new();

        public void Add(UciOption option)
        {
            _options[option.Name] = option;
        }

        /// <summary>
        /// Registers a research/tuning lever as HIDDEN (see
        /// <see cref="UciOption.Hidden"/>): fully settable through `setoption`
        /// and the `param:` argv override, but absent from the `uci` handshake
        /// so GUI option dialogs stay focused on the user-facing options.
        /// </summary>
        public void AddLever(UciOption option)
        {
            option.Hidden = true;
            Add(option);
        }

        public bool TryGetOption(string name, [NotNullWhen(true)] out UciOption? option)
        {
            return _options.TryGetValue(name, out option);
        }

        public void SetOption(string name, string value)
        {
            if (_options.TryGetValue(name, out var option))
            {
                option.Value = value;
            }
        }

        public string GetValue(string name)
        {
            return _options.TryGetValue(name, out var option) ? option.Value : "";
        }

        /// <summary>
        /// Renders the `option name ...` lines for the `uci` handshake.
        /// Hidden options (research levers) are skipped so GUIs never list
        /// them; they remain resolvable through <see cref="SetOption"/>.
        /// </summary>
        public override string ToString()
        {
            var sb = new StringBuilder();
            foreach (var option in _options.Values)
            {
                if (option.Hidden)
                    continue;
                sb.AppendLine(option.ToString());
            }
            return sb.ToString();
        }

        /// <summary>
        /// Renders every option, hidden ones included. Kept for diagnostics:
        /// `debug on` tooling and tests can still enumerate the full catalog
        /// without exposing it in the handshake.
        /// </summary>
        public string ToFullString()
        {
            var sb = new StringBuilder();
            foreach (var option in _options.Values)
            {
                sb.AppendLine(option.ToString());
            }
            return sb.ToString();
        }
    }
}
