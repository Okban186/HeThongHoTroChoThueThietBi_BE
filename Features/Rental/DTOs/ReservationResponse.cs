namespace HeThongHoTroChoThueThietBi.Features.Rental.DTOs;

/// <summary>Phiếu giữ chỗ (danh sách).</summary>
public sealed record ReservationResponse(
    Guid Id,
    string ReservationCode,
    Guid CompanyId,
    string CompanyCode,
    string CompanyName,
    DateOnly RequestedFrom,
    DateOnly RequestedTo,
    string ContactName,
    string ContactPhone,
    // Hạn giữ chỗ; quá hạn mà chưa tạo hợp đồng thì chuyển EXPIRED.
    DateTimeOffset ExpiresAt,
    // PENDING, APPROVED, CANCELLED, EXPIRED, CONVERTED (REJECTED không dùng).
    string Status,
    string? Note,
    int ItemCount,
    int TotalQuantity,
    Guid CreatedById,
    string? CreatedByName,
    // Người đang đăng nhập là người tạo phiếu.
    bool CanEdit,
    DateTimeOffset CreatedAt
);
