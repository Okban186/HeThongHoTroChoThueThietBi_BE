namespace HeThongHoTroChoThueThietBi.Features.Rental.Services;

/// <summary>Đọc số tiền VND thành chữ, ví dụ 2.300.000 → "Hai triệu ba trăm nghìn đồng".</summary>
public static class VietnameseMoneyWords
{
    private static readonly string[] Digits =
        ["không", "một", "hai", "ba", "bốn", "năm", "sáu", "bảy", "tám", "chín"];

    // Đơn vị của từng nhóm 3 chữ số, tính từ phải sang.
    private static readonly string[] GroupUnits = ["", "nghìn", "triệu", "tỷ", "nghìn tỷ", "triệu tỷ"];

    public static string ToWords(decimal amount)
    {
        var value = (long)Math.Round(Math.Abs(amount), MidpointRounding.AwayFromZero);

        if (value == 0)
            return "Không đồng";

        var groups = new List<int>();

        while (value > 0)
        {
            groups.Add((int)(value % 1000));
            value /= 1000;
        }

        var parts = new List<string>();

        for (var i = groups.Count - 1; i >= 0; i--)
        {
            if (groups[i] == 0)
                continue;

            var isLeading = i == groups.Count - 1;
            parts.Add(ReadGroup(groups[i], isLeading));

            if (GroupUnits[i].Length > 0)
                parts.Add(GroupUnits[i]);
        }

        var text = string.Join(' ', parts) + " đồng";
        return char.ToUpper(text[0]) + text[1..];
    }

    /// <summary>
    /// Đọc một nhóm 3 chữ số. Nhóm đứng đầu bỏ "không trăm"/"linh" ở đầu
    /// (25 → "hai mươi lăm"); các nhóm sau đọc đủ (025 → "không trăm hai mươi lăm").
    /// </summary>
    private static string ReadGroup(int number, bool isLeading)
    {
        var hundreds = number / 100;
        var tens = number / 10 % 10;
        var units = number % 10;
        var words = new List<string>();

        if (!isLeading || hundreds > 0)
        {
            words.Add(Digits[hundreds]);
            words.Add("trăm");
        }

        if (tens == 0)
        {
            if (units > 0 && words.Count > 0)
                words.Add("linh");
        }
        else if (tens == 1)
        {
            words.Add("mười");
        }
        else
        {
            words.Add(Digits[tens]);
            words.Add("mươi");
        }

        if (units > 0)
        {
            words.Add(units switch
            {
                1 when tens >= 2 => "mốt",
                5 when tens >= 1 => "lăm",
                _ => Digits[units],
            });
        }

        return string.Join(' ', words);
    }
}
