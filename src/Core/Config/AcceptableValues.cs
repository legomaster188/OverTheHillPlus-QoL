using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace OverTheHillPlus.Config;

/// <summary>The values a setting accepts; a value outside them is clamped.</summary>
public abstract class AcceptableValueBase
{
    protected AcceptableValueBase(Type valueType) { ValueType = valueType; }

    public Type ValueType { get; }

    public abstract object Clamp(object value);
    public abstract bool IsValid(object value);

    /// <summary>The "# Acceptable ..." comment line written above the setting.</summary>
    public abstract string ToDescriptionString();
}

/// <summary>A range from <see cref="MinValue"/> to <see cref="MaxValue"/>, both included.</summary>
public class AcceptableValueRange<T> : AcceptableValueBase where T : IComparable
{
    public AcceptableValueRange(T minValue, T maxValue) : base(typeof(T))
    {
        if (maxValue.CompareTo(minValue) < 0) throw new ArgumentException("maxValue has to be bigger than minValue");
        MinValue = minValue;
        MaxValue = maxValue;
    }

    public virtual T MinValue { get; }
    public virtual T MaxValue { get; }

    public override object Clamp(object value)
    {
        if (MinValue.CompareTo(value) > 0) return MinValue;
        if (MaxValue.CompareTo(value) < 0) return MaxValue;
        return value;
    }

    public override bool IsValid(object value) => MinValue.CompareTo(value) <= 0 && MaxValue.CompareTo(value) >= 0;

    public override string ToDescriptionString() => $"# Acceptable value range: From {MinValue} to {MaxValue}";
}

/// <summary>A fixed list of values; anything else becomes the first one.</summary>
public class AcceptableValueList<T> : AcceptableValueBase where T : IEquatable<T>
{
    public AcceptableValueList(params T[] acceptableValues) : base(typeof(T))
    {
        if (acceptableValues == null) throw new ArgumentNullException(nameof(acceptableValues));
        if (acceptableValues.Length == 0) throw new ArgumentException("At least one acceptable value is needed", nameof(acceptableValues));
        AcceptableValues = acceptableValues;
    }

    public virtual T[] AcceptableValues { get; }

    public override object Clamp(object value)
    {
        if (IsValid(value)) return value;
        return AcceptableValues[0];
    }

    public override bool IsValid(object value) => value is T v && AcceptableValues.Any(x => x.Equals(v));

    public override string ToDescriptionString() =>
        "# Acceptable values: " + string.Join(", ", AcceptableValues.Select(x => x.ToString()).ToArray());
}

/// <summary>How a value is written to and read from the file.</summary>
public class TypeConverter
{
    public Func<object, Type, string> ConvertToString { get; set; }
    public Func<string, Type, object> ConvertToObject { get; set; }
}

/// <summary>The text form of setting values, the same as BepInEx's: strings escaped, bools in lower case, enums by
/// name (any case when read), numbers in the invariant culture for float / double / decimal.</summary>
public static class TomlTypeConverter
{
    private static readonly Dictionary<Type, TypeConverter> TypeConverters = new()
    {
        [typeof(string)] = new TypeConverter
        {
            ConvertToString = (obj, type) => Escape((string)obj),
            ConvertToObject = (str, type) =>
            {
                // A Windows path with single backslashes (D:\test, not D:\\test) is taken as it is.
                if (Regex.IsMatch(str, @"^""?\w:\\(?!\\)(?!.+\\\\)")) return str;
                return Unescape(str);
            },
        },
        [typeof(bool)] = new TypeConverter
        {
            ConvertToString = (obj, type) => obj.ToString().ToLowerInvariant(),
            ConvertToObject = (str, type) => bool.Parse(str),
        },
        [typeof(byte)] = new TypeConverter { ConvertToString = (obj, type) => obj.ToString(), ConvertToObject = (str, type) => byte.Parse(str) },
        [typeof(sbyte)] = new TypeConverter { ConvertToString = (obj, type) => obj.ToString(), ConvertToObject = (str, type) => sbyte.Parse(str) },
        [typeof(short)] = new TypeConverter { ConvertToString = (obj, type) => obj.ToString(), ConvertToObject = (str, type) => short.Parse(str) },
        [typeof(ushort)] = new TypeConverter { ConvertToString = (obj, type) => obj.ToString(), ConvertToObject = (str, type) => ushort.Parse(str) },
        [typeof(int)] = new TypeConverter { ConvertToString = (obj, type) => obj.ToString(), ConvertToObject = (str, type) => int.Parse(str) },
        [typeof(uint)] = new TypeConverter { ConvertToString = (obj, type) => obj.ToString(), ConvertToObject = (str, type) => uint.Parse(str) },
        [typeof(long)] = new TypeConverter { ConvertToString = (obj, type) => obj.ToString(), ConvertToObject = (str, type) => long.Parse(str) },
        [typeof(ulong)] = new TypeConverter { ConvertToString = (obj, type) => obj.ToString(), ConvertToObject = (str, type) => ulong.Parse(str) },
        [typeof(float)] = new TypeConverter
        {
            ConvertToString = (obj, type) => ((float)obj).ToString(NumberFormatInfo.InvariantInfo),
            ConvertToObject = (str, type) => float.Parse(str, NumberFormatInfo.InvariantInfo),
        },
        [typeof(double)] = new TypeConverter
        {
            ConvertToString = (obj, type) => ((double)obj).ToString(NumberFormatInfo.InvariantInfo),
            ConvertToObject = (str, type) => double.Parse(str, NumberFormatInfo.InvariantInfo),
        },
        [typeof(decimal)] = new TypeConverter
        {
            ConvertToString = (obj, type) => ((decimal)obj).ToString(NumberFormatInfo.InvariantInfo),
            ConvertToObject = (str, type) => decimal.Parse(str, NumberFormatInfo.InvariantInfo),
        },
        [typeof(Enum)] = new TypeConverter
        {
            ConvertToString = (obj, type) => obj.ToString(),
            ConvertToObject = (str, type) => Enum.Parse(type, str, true),
        },
    };

    public static string ConvertToString(object value, Type valueType)
    {
        var conv = GetConverter(valueType);
        if (conv == null) throw new InvalidOperationException($"Cannot convert from type {valueType}");
        return conv.ConvertToString(value, valueType);
    }

    public static T ConvertToValue<T>(string value) => (T)ConvertToValue(value, typeof(T));

    public static object ConvertToValue(string value, Type valueType)
    {
        var conv = GetConverter(valueType);
        if (conv == null) throw new InvalidOperationException($"Cannot convert to type {valueType.Name}");
        return conv.ConvertToObject(value, valueType);
    }

    public static TypeConverter GetConverter(Type valueType)
    {
        if (valueType == null) throw new ArgumentNullException(nameof(valueType));
        if (valueType.IsEnum) return TypeConverters[typeof(Enum)];
        TypeConverters.TryGetValue(valueType, out var result);
        return result;
    }

    public static bool AddConverter(Type type, TypeConverter converter)
    {
        if (type == null) throw new ArgumentNullException(nameof(type));
        if (converter == null) throw new ArgumentNullException(nameof(converter));
        if (CanConvert(type)) return false;
        TypeConverters.Add(type, converter);
        return true;
    }

    public static bool CanConvert(Type type) => GetConverter(type) != null;

    public static IEnumerable<Type> GetSupportedTypes() => TypeConverters.Keys;

    private static string Escape(this string txt)
    {
        if (string.IsNullOrEmpty(txt)) return string.Empty;
        var sb = new StringBuilder(txt.Length + 2);
        foreach (char c in txt)
            switch (c)
            {
                case '\0': sb.Append(@"\0"); break;
                case '\a': sb.Append(@"\a"); break;
                case '\b': sb.Append(@"\b"); break;
                case '\t': sb.Append(@"\t"); break;
                case '\n': sb.Append(@"\n"); break;
                case '\v': sb.Append(@"\v"); break;
                case '\f': sb.Append(@"\f"); break;
                case '\r': sb.Append(@"\r"); break;
                case '\'': sb.Append(@"\'"); break;
                // BepInEx writes a backslash as it is (so D:\games stays D:\games); kept for files to stay the same.
                case '\\': sb.Append(@"\"); break;
                case '\"': sb.Append(@"\"""); break;
                default: sb.Append(c); break;
            }
        return sb.ToString();
    }

    private static string Unescape(this string txt)
    {
        if (string.IsNullOrEmpty(txt)) return txt;
        var sb = new StringBuilder(txt.Length);
        for (int i = 0; i < txt.Length;)
        {
            int num = txt.IndexOf('\\', i);
            if (num < 0 || num == txt.Length - 1) num = txt.Length;
            sb.Append(txt, i, num - i);
            if (num >= txt.Length) break;
            char c = txt[num + 1];
            switch (c)
            {
                case '0': sb.Append('\0'); break;
                case 'a': sb.Append('\a'); break;
                case 'b': sb.Append('\b'); break;
                case 't': sb.Append('\t'); break;
                case 'n': sb.Append('\n'); break;
                case 'v': sb.Append('\v'); break;
                case 'f': sb.Append('\f'); break;
                case 'r': sb.Append('\r'); break;
                case '\'': sb.Append('\''); break;
                case '\"': sb.Append('\"'); break;
                case '\\': sb.Append('\\'); break;
                default: sb.Append('\\').Append(c); break;
            }
            i = num + 2;
        }
        return sb.ToString();
    }
}
