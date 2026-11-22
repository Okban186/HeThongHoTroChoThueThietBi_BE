namespace HeThongHoTroChoThueThietBi.Features.Organization.DTOs;

/// <summary>Chi tiết công ty kèm các tài khoản đăng nhập của khách hàng.</summary>
public sealed record CompanyDetailResponse(
    CompanyResponse Company,
    List<CompanyAccountResponse> Accounts
);
