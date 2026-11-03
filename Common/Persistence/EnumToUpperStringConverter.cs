using Microsoft.EntityFrameworkCore.Storage.ValueConversion;

namespace HeThongHoTroChoThueThietBi.Common.Persistence;

public sealed class EnumToUpperStringConverter<TEnum>() : ValueConverter<TEnum, string>(
    value => EnumName.ToDatabaseValue(value),
    value => EnumName.FromDatabaseValue<TEnum>(value))
    where TEnum : struct, Enum
{
}

internal static class EnumName
{
    public static string ToDatabaseValue<TEnum>(TEnum value) where TEnum : struct, Enum
    {
        var name = value.ToString();
        var chars = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            var c = name[i];
            if (char.IsUpper(c) && i > 0 && (char.IsLower(name[i - 1]) || (i + 1 < name.Length && char.IsLower(name[i + 1]))))
                chars.Append('_');
            chars.Append(char.ToUpperInvariant(c));
        }
        return chars.ToString();
    }

    public static TEnum FromDatabaseValue<TEnum>(string value) where TEnum : struct, Enum
    {
        var pascal = string.Concat(value.Split('_').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
        return Enum.Parse<TEnum>(pascal, ignoreCase: true);
    }
}
