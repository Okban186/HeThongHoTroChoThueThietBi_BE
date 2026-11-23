namespace HeThongHoTroChoThueThietBi.Features.Dashboard.DTOs;

/// <summary>Số liệu trang Tổng quan của nhân viên kinh doanh (chỉ tính dữ liệu do chính họ tạo).</summary>
public sealed record SalesDashboardResponse(
    // Tổng số phiếu giữ chỗ đã lập.
    int TotalReservations,
    // Phiếu còn hiệu lực: PENDING hoặc APPROVED, chưa quá hạn giữ chỗ.
    int ProcessingReservations,
    // Phiếu PENDING chưa có thiết bị.
    int ReservationsToComplete,
    // Hợp đồng đang hiệu lực: SIGNED, PREPARING, PARTIALLY_DELIVERED, DELIVERED.
    int ActiveContracts,
    // Hợp đồng bị Manager từ chối, cần sửa và gửi lại.
    int RejectedContracts,
    // Số công ty khách hàng đang hoạt động (dữ liệu dùng chung).
    int Companies,
    List<SalesActivityResponse> RecentActivities
);

public sealed record SalesActivityResponse(
    // RESERVATION, CONTRACT, COMPANY
    string Type,
    Guid ReferenceId,
    string Title,
    string Description,
    DateTimeOffset OccurredAt
);
