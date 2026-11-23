using System.Globalization;
using HeThongHoTroChoThueThietBi.Features.Rental.Configurations;
using HeThongHoTroChoThueThietBi.Features.Rental.DTOs;
using Microsoft.Extensions.Options;
using QuestPDF.Fluent;
using QuestPDF.Helpers;
using QuestPDF.Infrastructure;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Services;

/// <summary>Dữ liệu điền vào mẫu hợp đồng (ContractService chuẩn bị).</summary>
public sealed record ContractPdfData(
    string ContractCode,
    // Hợp đồng chưa được Manager duyệt: in dòng "BẢN NHÁP" và không dùng để ký.
    bool IsDraft,
    DateOnly IssueDate,
    DateOnly StartDate,
    DateOnly EndDate,
    int RentalDays,
    // SHORT_TERM, LONG_TERM
    string RentalType,
    string? DeliveryAddress,
    string? DeliveryNote,
    string? Note,
    string CompanyName,
    string CompanyTaxCode,
    string? CompanyAddress,
    ContractRepresentativeResponse? Representative,
    List<RentalContractItemResponse> Items,
    List<RentalContractServiceResponse> Services,
    decimal TotalAmount,
    decimal DepositAmount
);

/// <summary>
/// Mẫu hợp đồng cho thuê thiết bị (A4, tiếng Việt), dựng bằng QuestPDF.
/// Bên A lấy từ cấu hình "Lessor"; Bên B, thiết bị, dịch vụ, tiền lấy từ hợp đồng.
/// </summary>
public sealed class ContractPdfGenerator(IOptions<LessorOptions> options)
{
    private static readonly CultureInfo Vi = CultureInfo.GetCultureInfo("vi-VN");

    // Lato đi kèm gói QuestPDF (có đủ dấu tiếng Việt) nên máy nào chạy BE cũng xuất được,
    // không phụ thuộc font cài trên hệ điều hành (QuestPDF mặc định không dùng font hệ thống).
    private static readonly string[] FontFamilies = ["Lato"];

    private const float BaseFontSize = 12;

    private readonly LessorOptions _lessor = options.Value;

    public byte[] Generate(ContractPdfData data)
    {
        return Document.Create(container =>
        {
            container.Page(page =>
            {
                page.Size(PageSizes.A4);
                page.MarginVertical(1.8f, Unit.Centimetre);
                page.MarginLeft(2.5f, Unit.Centimetre);
                page.MarginRight(1.8f, Unit.Centimetre);
                page.DefaultTextStyle(x => x.FontFamily(FontFamilies).FontSize(BaseFontSize).LineHeight(1.3f));

                if (data.IsDraft)
                {
                    page.Header().PaddingBottom(6).AlignCenter().Text(
                            "BẢN NHÁP – HỢP ĐỒNG CHƯA ĐƯỢC DUYỆT, KHÔNG DÙNG ĐỂ KÝ")
                        .FontSize(10).Bold().FontColor(Colors.Red.Medium);
                }

                page.Content().Column(col =>
                {
                    col.Spacing(4);
                    ComposeTitle(col, data);
                    ComposeParties(col, data);
                    var lastArticle = ComposeArticles(col, data);

                    // Điều cuối đi cùng phần chữ ký trên một trang, tránh trang chữ ký đứng riêng.
                    col.Item().ShowEntire().Column(end =>
                    {
                        end.Spacing(4);
                        ComposeGeneralTerms(end, lastArticle + 1, data);
                        ComposeSignatures(end, data);
                    });
                });

                page.Footer().AlignCenter().Text(t =>
                {
                    t.DefaultTextStyle(x => x.FontSize(9).FontColor(Colors.Grey.Darken1));
                    t.Span($"Hợp đồng số {data.ContractCode} – Trang ");
                    t.CurrentPageNumber();
                    t.Span("/");
                    t.TotalPages();
                });
            });
        }).GeneratePdf();
    }

    // ------------------------------------------------------------ phần đầu

    private void ComposeTitle(ColumnDescriptor col, ContractPdfData data)
    {
        col.Item().AlignCenter().Text("CỘNG HÒA XÃ HỘI CHỦ NGHĨA VIỆT NAM").Bold();
        col.Item().AlignCenter().Text("Độc lập – Tự do – Hạnh phúc").Bold();
        col.Item().AlignCenter().Width(160).PaddingTop(2).LineHorizontal(0.8f);

        col.Item().PaddingTop(14).AlignCenter().Text("HỢP ĐỒNG CHO THUÊ THIẾT BỊ").FontSize(16).Bold();
        col.Item().AlignCenter().Text($"Số: {data.ContractCode}").Italic();

        col.Item().PaddingTop(10).Text("Căn cứ:").Italic();
        Bullet(col, "Bộ luật Dân sự số 91/2015/QH13 ngày 24/11/2015;", italic: true);
        Bullet(col, "Luật Thương mại số 36/2005/QH11 ngày 14/06/2005;", italic: true);
        Bullet(col, "Nhu cầu và khả năng của hai bên.", italic: true);

        col.Item().PaddingTop(6).Text(
            $"Hôm nay, ngày {data.IssueDate:dd} tháng {data.IssueDate:MM} năm {data.IssueDate:yyyy}, " +
            $"tại {_lessor.SigningPlace}, chúng tôi gồm:");
    }

    private void ComposeParties(ColumnDescriptor col, ContractPdfData data)
    {
        // Bên A
        PartyHeading(col, "BÊN CHO THUÊ (BÊN A)", _lessor.CompanyName);
        Field(col, "Mã số thuế", _lessor.TaxCode);
        Field(col, "Địa chỉ", _lessor.Address);
        Field(col, "Điện thoại", _lessor.Phone);
        Field(col, "Email", _lessor.Email);

        if (!string.IsNullOrWhiteSpace(_lessor.BankAccountNumber))
        {
            Field(col, "Tài khoản số",
                string.IsNullOrWhiteSpace(_lessor.BankName)
                    ? _lessor.BankAccountNumber
                    : $"{_lessor.BankAccountNumber} tại {_lessor.BankName}");
        }

        Field(col, "Đại diện", $"{_lessor.RepresentativeName}    Chức vụ: {_lessor.RepresentativePosition}");

        // Bên B
        var rep = data.Representative;

        PartyHeading(col, "BÊN THUÊ (BÊN B)", data.CompanyName.ToUpper(Vi));
        Field(col, "Mã số thuế", data.CompanyTaxCode);
        Field(col, "Địa chỉ", data.CompanyAddress);
        Field(col, "Đại diện", rep?.FullName ?? Blank);
        Field(col, "Tư cách", rep?.RepresentativeType == "AUTHORIZED_REPRESENTATIVE"
            ? "Người được ủy quyền (theo giấy ủy quyền kèm theo hợp đồng)"
            : "Người đại diện theo pháp luật");
        Field(col, "Số CCCD", rep?.IdNumber ?? Blank);
        Field(col, "Điện thoại", rep?.Phone);
        Field(col, "Email", rep?.Email);

        col.Item().PaddingTop(6).Text(
            "Sau khi bàn bạc, hai bên thống nhất ký kết hợp đồng cho thuê thiết bị với các điều khoản sau:");
    }

    // ------------------------------------------------------------ các điều khoản

    /// <summary>Điều 1 đến điều chấm dứt/tranh chấp; trả số của điều cuối đã in.</summary>
    private int ComposeArticles(ColumnDescriptor col, ContractPdfData data)
    {
        var article = 0;

        // Điều 1. Thiết bị
        Article(col, ++article, "Thiết bị cho thuê");
        col.Item().Text("Bên A đồng ý cho Bên B thuê các thiết bị sau:");
        col.Item().PaddingVertical(4).Element(c => ItemsTable(c, data.Items));
        Clause(col, $"{article}.1",
            "Thiết bị được giao đúng chủng loại, số lượng, hoạt động bình thường. Số serial và tình trạng cụ thể " +
            "của từng thiết bị được ghi trong biên bản bàn giao có chữ ký của đại diện hai bên.");

        // Điều 2. Thời hạn, địa điểm
        Article(col, ++article, "Thời hạn thuê và địa điểm giao nhận");
        Clause(col, $"{article}.1",
            $"Thời hạn thuê: từ ngày {Date(data.StartDate)} đến hết ngày {Date(data.EndDate)} " +
            $"({data.RentalDays} ngày). Hình thức: {(data.RentalType == "LONG_TERM" ? "thuê dài hạn" : "thuê ngắn hạn")}.");
        Clause(col, $"{article}.2",
            $"Địa điểm giao thiết bị: {Or(data.DeliveryAddress, "theo thỏa thuận của hai bên trước ngày giao")}." +
            (string.IsNullOrWhiteSpace(data.DeliveryNote) ? "" : $" Ghi chú: {data.DeliveryNote.Trim()}."));
        Clause(col, $"{article}.3",
            "Việc giao và trả thiết bị được lập thành biên bản bàn giao, ghi rõ số lượng, số serial và tình trạng thiết bị.");
        Clause(col, $"{article}.4",
            "Nếu muốn gia hạn, Bên B thông báo bằng văn bản cho Bên A ít nhất 03 ngày trước khi hết hạn thuê. " +
            "Việc gia hạn được lập thành phụ lục hợp đồng.");

        // Điều 3. Dịch vụ (chỉ in khi có)
        if (data.Services.Count > 0)
        {
            Article(col, ++article, "Dịch vụ kèm theo");
            col.Item().PaddingVertical(4).Element(c => ServicesTable(c, data.Services));
            Clause(col, $"{article}.1",
                "Dịch vụ do Bên B tự thực hiện thì Bên B tự chịu chi phí và trách nhiệm an toàn trong quá trình thực hiện.");
        }

        // Điều 4. Giá trị, đặt cọc, thanh toán
        var equipmentTotal = data.Items.Sum(i => i.Amount);
        var serviceTotal = data.TotalAmount - equipmentTotal;

        Article(col, ++article, "Giá trị hợp đồng, đặt cọc và thanh toán");
        col.Item().Text(t =>
        {
            t.Justify();
            t.Span($"{article}.1. Tổng giá trị hợp đồng: ");
            t.Span($"{Money(data.TotalAmount)} đồng").Bold();
            t.Span($" (Bằng chữ: {VietnameseMoneyWords.ToWords(data.TotalAmount)})").Italic();
            t.Span(serviceTotal > 0
                ? $", gồm tiền thuê thiết bị {Money(equipmentTotal)} đồng và tiền dịch vụ {Money(serviceTotal)} đồng."
                : ".");
            t.Span(" Giá trên chưa bao gồm thuế giá trị gia tăng (nếu có).");
        });
        col.Item().Text(t =>
        {
            t.Justify();
            t.Span($"{article}.2. Tiền đặt cọc: ");
            t.Span($"{Money(data.DepositAmount)} đồng").Bold();
            t.Span($" (Bằng chữ: {VietnameseMoneyWords.ToWords(data.DepositAmount)})").Italic();
            t.Span(". Bên B đặt cọc trong vòng 03 ngày làm việc kể từ ngày ký và trước khi Bên A giao thiết bị. " +
                   "Tiền cọc được trừ vào tiền thuê khi thanh lý hợp đồng, hoặc hoàn lại cho Bên B sau khi trừ " +
                   "các khoản phí phát sinh (nếu có).");
        });
        Clause(col, $"{article}.3", data.RentalType == "LONG_TERM"
            ? "Bên B thanh toán tiền thuê theo từng tháng, chậm nhất ngày 05 của tháng kế tiếp. Phần còn lại " +
              "(nếu có) được thanh toán trong vòng 07 ngày kể từ ngày hai bên ký biên bản trả thiết bị."
            : "Phần còn lại sau khi trừ tiền cọc được thanh toán trong vòng 07 ngày kể từ ngày hai bên ký " +
              "biên bản trả thiết bị.");
        Clause(col, $"{article}.4",
            "Hình thức thanh toán: chuyển khoản vào tài khoản của Bên A nêu trên, hoặc tiền mặt có phiếu thu của Bên A.");

        // Điều 5. Bên A
        Article(col, ++article, "Quyền và nghĩa vụ của Bên A");
        Bullet(col, "Giao thiết bị đúng chủng loại, số lượng, thời hạn, trong tình trạng hoạt động tốt, kèm hướng dẫn sử dụng.");
        Bullet(col, "Lắp đặt và hướng dẫn vận hành thiết bị khi hai bên có thỏa thuận.");
        Bullet(col, "Sửa chữa hư hỏng do hao mòn tự nhiên hoặc lỗi kỹ thuật của thiết bị trong thời gian thuê; " +
                    "thay thế bằng thiết bị tương đương nếu không sửa được trong thời gian hợp lý.");
        Bullet(col, "Được nhận đủ tiền thuê, tiền cọc theo hợp đồng; được kiểm tra việc sử dụng thiết bị khi báo trước cho Bên B.");
        Bullet(col, "Được thu hồi thiết bị khi hết thời hạn thuê hoặc khi hợp đồng chấm dứt.");

        // Điều 6. Bên B
        Article(col, ++article, "Quyền và nghĩa vụ của Bên B");
        Bullet(col, "Được nhận thiết bị đúng thỏa thuận; được yêu cầu Bên A sửa chữa, thay thế khi thiết bị hư hỏng " +
                    "không do lỗi của Bên B.");
        Bullet(col, "Sử dụng thiết bị đúng công năng và hướng dẫn kỹ thuật; bảo quản thiết bị như tài sản của mình.");
        Bullet(col, "Không cho thuê lại, cầm cố, tự ý tháo dỡ, sửa chữa hoặc di chuyển thiết bị khỏi địa điểm đã thỏa " +
                    "thuận khi chưa có sự đồng ý bằng văn bản của Bên A.");
        Bullet(col, "Thông báo ngay cho Bên A khi thiết bị gặp sự cố.");
        Bullet(col, "Thanh toán đầy đủ, đúng hạn; trả thiết bị đúng thời hạn và trong tình trạng như khi nhận, " +
                    "trừ hao mòn tự nhiên.");

        // Điều 7. Hư hỏng, mất mát, trễ hạn
        Article(col, ++article, "Hư hỏng, mất mát và trả thiết bị trễ hạn");
        Bullet(col, "Thiết bị hư hỏng, mất mát do lỗi của Bên B thì Bên B chịu chi phí sửa chữa hoặc bồi thường theo " +
                    "giá trị thiết bị tại thời điểm xảy ra, căn cứ biên bản xác nhận của hai bên.");
        Bullet(col, "Trả thiết bị trễ hạn mà không có phụ lục gia hạn thì Bên B trả thêm tiền thuê cho thời gian trễ " +
                    "theo đơn giá tại Điều 1.");
        Bullet(col, "Các khoản phí phát sinh được trừ vào tiền cọc; nếu tiền cọc không đủ, Bên B thanh toán phần chênh lệch.");

        // Điều 8. Chấm dứt
        Article(col, ++article, "Chấm dứt hợp đồng");
        col.Item().Text("Hợp đồng chấm dứt trong các trường hợp sau:");
        Bullet(col, "Hết thời hạn thuê và hai bên đã hoàn tất việc trả thiết bị, thanh toán.");
        Bullet(col, "Hai bên thỏa thuận chấm dứt trước thời hạn.");
        Bullet(col, "Một bên đơn phương chấm dứt khi bên kia vi phạm nghiêm trọng nghĩa vụ (Bên B chậm thanh toán quá " +
                    "15 ngày hoặc sử dụng thiết bị sai mục đích; Bên A không giao thiết bị đúng hạn), với điều kiện " +
                    "thông báo bằng văn bản trước ít nhất 03 ngày.");

        // Điều 9. Tranh chấp
        Article(col, ++article, "Giải quyết tranh chấp");
        col.Item().Text(
            "Tranh chấp phát sinh được hai bên giải quyết trước hết bằng thương lượng. Nếu không thương lượng được " +
            "thì một trong hai bên có quyền yêu cầu Tòa án nhân dân có thẩm quyền giải quyết theo quy định của pháp luật.").Justify();

        return article;
    }

    private static void ComposeGeneralTerms(ColumnDescriptor col, int article, ContractPdfData data)
    {
        Article(col, article, "Điều khoản chung");
        Bullet(col, "Hai bên cam kết thực hiện đúng các điều khoản của hợp đồng. Mọi sửa đổi, bổ sung phải được lập " +
                    "thành phụ lục bằng văn bản có chữ ký của hai bên.");
        Bullet(col, "Biên bản bàn giao, bản sao CCCD của người đại diện, giấy phép kinh doanh của Bên B và giấy ủy quyền " +
                    "(nếu có) là bộ phận không tách rời của hợp đồng.");

        if (!string.IsNullOrWhiteSpace(data.Note))
            Bullet(col, $"Thỏa thuận khác: {data.Note.Trim()}");

        Bullet(col, "Hợp đồng có hiệu lực kể từ ngày ký, được lập thành 02 bản có giá trị pháp lý như nhau, " +
                    "mỗi bên giữ 01 bản.");
    }

    private void ComposeSignatures(ColumnDescriptor col, ContractPdfData data)
    {
        var parties = new[]
        {
            ("ĐẠI DIỆN BÊN A", _lessor.RepresentativeName),
            ("ĐẠI DIỆN BÊN B", data.Representative?.FullName ?? ""),
        };

        col.Item().PaddingTop(16).Row(row =>
        {
            foreach (var (party, name) in parties)
            {
                row.RelativeItem().Column(c =>
                {
                    c.Item().AlignCenter().Text(party).Bold();
                    c.Item().AlignCenter().Text("(Ký, ghi rõ họ tên và đóng dấu)").Italic().FontSize(10);
                    c.Item().Height(80);
                    c.Item().AlignCenter().Text(name).Bold();
                });
            }
        });
    }

    // ------------------------------------------------------------ bảng

    private static void ItemsTable(IContainer container, List<RentalContractItemResponse> items)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(28);   // STT
                c.RelativeColumn(4);    // Thiết bị
                c.ConstantColumn(36);   // ĐVT
                c.ConstantColumn(30);   // SL
                c.RelativeColumn(2.2f); // Đơn giá
                c.RelativeColumn(1.4f); // Số kỳ
                c.RelativeColumn(2.4f); // Thành tiền
            });

            table.Header(h =>
            {
                foreach (var title in new[] { "STT", "Thiết bị", "ĐVT", "SL", "Đơn giá (đ)", "Thời gian", "Thành tiền (đ)" })
                    h.Cell().Element(HeaderCell).Text(title).Bold();
            });

            var index = 0;

            foreach (var item in items)
            {
                var unitLabel = item.BillingUnit == "MONTHLY" ? "tháng" : "ngày";

                table.Cell().Element(BodyCell).AlignCenter().Text($"{++index}");
                table.Cell().Element(BodyCell).Text(t =>
                {
                    t.Span(item.EquipmentModelName);
                    t.Span($"\nMã: {item.EquipmentModelCode}").FontSize(9).FontColor(Colors.Grey.Darken2);
                });
                table.Cell().Element(BodyCell).AlignCenter().Text(item.Unit);
                table.Cell().Element(BodyCell).AlignCenter().Text($"{item.Quantity}");
                table.Cell().Element(BodyCell).AlignRight().Text($"{Money(item.AgreedUnitPrice)}/{unitLabel}");
                table.Cell().Element(BodyCell).AlignCenter().Text($"{item.BillableUnits} {unitLabel}");
                table.Cell().Element(BodyCell).AlignRight().Text(Money(item.Amount));
            }

            table.Cell().ColumnSpan(6).Element(BodyCell).AlignRight().Text("Cộng tiền thuê thiết bị").Bold();
            table.Cell().Element(BodyCell).AlignRight().Text(Money(items.Sum(i => i.Amount))).Bold();
        });
    }

    private static void ServicesTable(IContainer container, List<RentalContractServiceResponse> services)
    {
        container.Table(table =>
        {
            table.ColumnsDefinition(c =>
            {
                c.ConstantColumn(28);
                c.RelativeColumn(4);
                c.RelativeColumn(2);
                c.ConstantColumn(30);
                c.RelativeColumn(2);
                c.RelativeColumn(2.2f);
            });

            table.Header(h =>
            {
                foreach (var title in new[] { "STT", "Dịch vụ", "Bên thực hiện", "SL", "Đơn giá (đ)", "Thành tiền (đ)" })
                    h.Cell().Element(HeaderCell).Text(title).Bold();
            });

            var index = 0;

            foreach (var service in services)
            {
                var byLessor = service.ProvidedBy == "RENTAL_COMPANY";

                table.Cell().Element(BodyCell).AlignCenter().Text($"{++index}");
                table.Cell().Element(BodyCell).Text(ServiceLabel(service));
                table.Cell().Element(BodyCell).AlignCenter().Text(byLessor ? "Bên A" : "Bên B tự làm");
                table.Cell().Element(BodyCell).AlignCenter().Text($"{service.Quantity}");
                table.Cell().Element(BodyCell).AlignRight().Text(byLessor ? Money(service.UnitPrice) : "–");
                table.Cell().Element(BodyCell).AlignRight().Text(byLessor ? Money(service.Amount) : "–");
            }
        });
    }

    private static IContainer HeaderCell(IContainer container) => container
        .Border(0.6f).BorderColor(Colors.Grey.Darken1)
        .Background(Colors.Grey.Lighten3)
        .PaddingVertical(3).PaddingHorizontal(4)
        .AlignCenter().AlignMiddle()
        .DefaultTextStyle(x => x.FontSize(10.5f));

    private static IContainer BodyCell(IContainer container) => container
        .Border(0.6f).BorderColor(Colors.Grey.Darken1)
        .PaddingVertical(3).PaddingHorizontal(4)
        .AlignMiddle()
        .DefaultTextStyle(x => x.FontSize(10.5f));

    // ------------------------------------------------------------ helpers

    private const string Blank = "……………………………………";

    private static void PartyHeading(ColumnDescriptor col, string role, string name)
    {
        col.Item().PaddingTop(8).Text(t =>
        {
            t.Span($"{role}: ").Bold();
            t.Span(name).Bold();
        });
    }

    private static void Field(ColumnDescriptor col, string label, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return;

        col.Item().PaddingLeft(12).Text(t =>
        {
            t.Span($"{label}: ");
            t.Span(value.Trim());
        });
    }

    private static void Article(ColumnDescriptor col, int number, string title)
    {
        col.Item().PaddingTop(8).Text($"Điều {number}. {title}").Bold();
    }

    private static void Clause(ColumnDescriptor col, string number, string text)
    {
        col.Item().Text($"{number}. {text}").Justify();
    }

    private static void Bullet(ColumnDescriptor col, string text, bool italic = false)
    {
        col.Item().PaddingLeft(12).Row(row =>
        {
            row.ConstantItem(12).Text("–");
            var span = row.RelativeItem().Text(text).Justify();

            if (italic)
                span.Italic();
        });
    }

    private static string ServiceLabel(RentalContractServiceResponse service)
    {
        var label = service.ServiceType switch
        {
            "DELIVERY" => "Vận chuyển giao, nhận thiết bị",
            "LOADING" => "Bốc xếp thiết bị lên xe",
            "UNLOADING" => "Bốc dỡ thiết bị xuống",
            _ => "Dịch vụ khác",
        };

        return string.IsNullOrWhiteSpace(service.Note) ? label : $"{label} ({service.Note.Trim()})";
    }

    private static string Money(decimal value) => value.ToString("N0", Vi);

    private static string Date(DateOnly date) => date.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture);

    private static string Or(string? value, string fallback) => string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}
