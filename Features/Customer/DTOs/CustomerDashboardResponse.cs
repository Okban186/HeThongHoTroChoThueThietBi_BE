namespace HeThongHoTroChoThueThietBi.Features.Customer.DTOs;

/// <summary>Công ty của tài khoản khách hàng đang đăng nhập, kèm nhân viên kinh doanh phụ trách.</summary>
public sealed record CustomerCompanyResponse(
    Guid Id,
    string CompanyCode,
    string Name,
    string TaxCode,
    string? Address,
    string Status,
    // Nhân viên kinh doanh tạo công ty (đầu mối liên hệ), có thể null với dữ liệu cũ.
    string? SalesName,
    string? SalesPhone,
    string? SalesEmail
);

/// <summary>Số liệu màn Tổng quan của khách hàng.</summary>
public sealed record CustomerDashboardResponse(
    CustomerCompanyResponse Company,
    // Đã ký, đang chuẩn bị, đang giao, đã giao.
    int ActiveContracts,
    int PendingSignatureContracts,
    // Hợp đồng đang hiệu lực kết thúc trong 7 ngày tới.
    int EndingSoonContracts,
    // Tổng số máy trong các hợp đồng đang hiệu lực.
    int RentedUnits,
    decimal ActiveContractsValue,
    // Các khoản phí chưa thanh toán (phần còn lại).
    decimal OutstandingAmount,
    // Hợp đồng đang hiệu lực/chờ ký, sắp kết thúc trước.
    List<CustomerContractBrief> UpcomingContracts
);

public sealed record CustomerContractBrief(
    Guid Id,
    string ContractCode,
    DateOnly StartDate,
    DateOnly EndDate,
    string Status,
    decimal ContractTotalAmount,
    // Số ngày còn lại tới ngày kết thúc (âm nếu đã quá hạn).
    int DaysLeft
);
