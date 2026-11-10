using System;
using System.Linq;
using System.Text;

namespace OverTheHillPlus.Config;

/// <summary>One bound setting, untyped (see <see cref="ConfigEntry{T}"/>).</summary>
public abstract class ConfigEntryBase
{
    protected internal ConfigEntryBase(ConfigFile configFile, ConfigDefinition definition, Type settingType, object defaultValue,
                                       ConfigDescription configDescription)
    {
        ConfigFile = configFile ?? throw new ArgumentNullException(nameof(configFile));
        Definition = definition ?? throw new ArgumentNullException(nameof(definition));
        SettingType = settingType ?? throw new ArgumentNullException(nameof(settingType));
        Description = configDescription ?? ConfigDescription.Empty;
        if (Description.AcceptableValues != null && !SettingType.IsAssignableFrom(Description.AcceptableValues.ValueType))
            throw new ArgumentException("configDescription.AcceptableValues is for a different type than the type of this setting");
        DefaultValue = defaultValue;
        // The setter clamps (a default outside the acceptable values is clamped as in BepInEx) and raises no event yet.
        BoxedValue = defaultValue;
    }

    public ConfigFile ConfigFile { get; }
    public ConfigDefinition Definition { get; }
    public ConfigDescription Description { get; }
    public Type SettingType { get; }
    public object DefaultValue { get; }

    /// <summary>The value as an object; setting it converts and clamps like <c>Value</c>.</summary>
    public abstract object BoxedValue { get; set; }

    /// <summary>The value as written in the file.</summary>
    public string GetSerializedValue() => TomlTypeConverter.ConvertToString(BoxedValue, SettingType);

    /// <summary>Sets the value from its text in the file; text that can't be read is reported and ignored.</summary>
    public void SetSerializedValue(string value)
    {
        try
        {
            object newValue = TomlTypeConverter.ConvertToValue(value, SettingType);
            BoxedValue = newValue;
        }
        catch (Exception e)
        {
            ConfigFile.Warn?.Invoke($"Config value of setting \"{Definition}\" could not be parsed and will be ignored. Reason: {e.Message}; Value: {value}");
        }
    }

    protected T ClampValue<T>(T value)
    {
        if (Description.AcceptableValues != null) return (T)Description.AcceptableValues.Clamp(value);
        return value;
    }

    protected void OnSettingChanged(object sender) => ConfigFile.OnSettingChanged(sender, this);

    /// <summary>The comment lines above the setting: description, type, default and acceptable values.</summary>
    public void WriteDescription(StringBuilder sb)
    {
        if (!string.IsNullOrEmpty(Description.Description))
            ConfigFile.Line(sb, $"## {Description.Description.Replace("\n", "\n## ")}");
        ConfigFile.Line(sb, "# Setting type: " + SettingType.Name);
        ConfigFile.Line(sb, "# Default value: " + TomlTypeConverter.ConvertToString(DefaultValue, SettingType));
        if (Description.AcceptableValues != null)
        {
            ConfigFile.Line(sb, Description.AcceptableValues.ToDescriptionString());
        }
        else if (SettingType.IsEnum)
        {
            ConfigFile.Line(sb, "# Acceptable values: " + string.Join(", ", Enum.GetNames(SettingType)));
            if (SettingType.GetCustomAttributes(typeof(FlagsAttribute), true).Any())
                ConfigFile.Line(sb, "# Multiple values can be set at the same time by separating them with , (e.g. Debug, Warning)");
        }
    }
}

/// <summary>A bound setting of type <typeparamref name="T"/>.</summary>
public sealed class ConfigEntry<T> : ConfigEntryBase
{
    private T _typedValue;

    internal ConfigEntry(ConfigFile configFile, ConfigDefinition definition, T defaultValue, ConfigDescription configDescription)
        : base(configFile, definition, typeof(T), defaultValue, configDescription)
    {
        configFile.SettingChanged += (object sender, SettingChangedEventArgs args) =>
        {
            if (args.ChangedSetting == this) SettingChanged?.Invoke(sender, args);
        };
    }

    /// <summary>Raised when this setting's value changes.</summary>
    public event EventHandler SettingChanged;

    /// <summary>The value; a new one is clamped to the acceptable values, and a change raises SettingChanged.</summary>
    public T Value
    {
        get => _typedValue;
        set
        {
            value = ClampValue(value);
            if (Equals(_typedValue, value)) return;
            _typedValue = value;
            OnSettingChanged(this);
        }
    }

    public override object BoxedValue
    {
        get => Value;
        set => Value = (T)value;
    }
}

/// <summary>Section and key of a setting.</summary>
public sealed class ConfigDefinition : IEquatable<ConfigDefinition>
{
    private static readonly char[] InvalidConfigChars = { '=', '\n', '\t', '\\', '"', '\'', '[', ']' };

    public ConfigDefinition(string section, string key)
    {
        CheckInvalidConfigChars(section, nameof(section));
        CheckInvalidConfigChars(key, nameof(key));
        Key = key;
        Section = section;
    }

    public string Section { get; }
    public string Key { get; }

    private static void CheckInvalidConfigChars(string val, string name)
    {
        if (val == null) throw new ArgumentNullException(name);
        if (val != val.Trim()) throw new ArgumentException("Cannot use whitespace characters at start or end of section and key names", name);
        if (val.Any(c => InvalidConfigChars.Contains(c)))
            throw new ArgumentException(@"Cannot use any of the following characters in section and key names: = \n \t \ "" ' [ ]", name);
    }

    public bool Equals(ConfigDefinition other) =>
        other != null && string.Equals(Key, other.Key) && string.Equals(Section, other.Section);

    public override bool Equals(object obj) => obj is ConfigDefinition other && Equals(other);

    public override int GetHashCode()
    {
        unchecked
        {
            int hashCode = Key != null ? Key.GetHashCode() : 0;
            hashCode = (hashCode * 397) ^ (Section != null ? Section.GetHashCode() : 0);
            return hashCode;
        }
    }

    public static bool operator ==(ConfigDefinition left, ConfigDefinition right) => Equals(left, right);
    public static bool operator !=(ConfigDefinition left, ConfigDefinition right) => !Equals(left, right);

    public override string ToString() => Section + "." + Key;
}

/// <summary>A setting's description, acceptable values and free tags.</summary>
public class ConfigDescription
{
    public ConfigDescription(string description, AcceptableValueBase acceptableValues = null, params object[] tags)
    {
        AcceptableValues = acceptableValues;
        Tags = tags;
        Description = description ?? throw new ArgumentNullException(nameof(description));
    }

    public string Description { get; }
    public AcceptableValueBase AcceptableValues { get; }
    public object[] Tags { get; }

    public static ConfigDescription Empty { get; } = new("");
}
