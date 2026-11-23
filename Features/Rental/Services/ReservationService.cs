using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Organization.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Configurations;
using HeThongHoTroChoThueThietBi.Features.Rental.DTOs;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Services;

/// <summary>
/// Phiếu giữ chỗ (reservation) của nhân viên kinh doanh.
///
/// Luồng: PENDING (lập phiếu, thêm thiết bị) → APPROVED (Sales tự xác nhận)
///        → CONVERTED (khi tạo hợp đồng, ở nghiệp vụ hợp đồng).
///        PENDING/APPROVED → CANCELLED (hủy) hoặc EXPIRED (quá hạn giữ chỗ).
///        APPROVED → PENDING ("Mở lại để chỉnh sửa", giữ nguyên hạn).
///        REJECTED có trong DB nhưng không dùng.
///
/// Quy tắc:
/// - Sales chỉ thấy phiếu do mình lập (danh sách và chi tiết) và chỉ người lập được thao tác.
///   Manager xem được mọi phiếu nhưng không thao tác.
/// - Mã phiếu PH-YYYY-NNNN. Hạn giữ chỗ = min(lúc tạo + HoldDays, hết ngày bắt đầu thuê).
/// - Ngày bắt đầu không trước hôm nay; công ty phải đang hoạt động.
/// - Thêm/sửa thiết bị, đổi ngày, xác nhận đều kiểm tra số lượng khả dụng bằng
///   EquipmentAvailabilityService, trong transaction có khóa model để chống giữ chỗ trùng.
/// - Phiếu quá hạn được chuyển EXPIRED khi có truy cập (không cần job nền);
///   công thức khả dụng cũng đã bỏ qua phiếu quá hạn.
/// </summary>
public sealed class ReservationService(
    AppDbContext db,
    EquipmentAvailabilityService availabilityService,
    IOptions<ReservationOptions> options)
{
    private const string CodePrefix = "PH";

    private readonly ReservationOptions _options = options.Value;

    public async Task<List<ReservationResponse>> GetReservationsAsync(
        string? keyword,
        string? status,
        Guid currentUserId,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        await ExpireOverdueAsync(cancellationToken);

        var query =
            from reservation in db.Reservations.AsNoTracking()
            join company in db.Companies.AsNoTracking() on reservation.CompanyId equals company.Id
            select new { reservation, company };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";

            query = query.Where(x =>
                EF.Functions.ILike(x.reservation.ReservationCode, pattern) ||
                EF.Functions.ILike(x.company.Name, pattern) ||
                EF.Functions.ILike(x.company.CompanyCode, pattern) ||
                EF.Functions.ILike(x.reservation.ContactName, pattern) ||
                EF.Functions.ILike(x.reservation.ContactPhone, pattern));
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var reservationStatus = ParseStatus(status);
            query = query.Where(x => x.reservation.Status == reservationStatus);
        }

        if (!canViewAll)
            query = query.Where(x => x.reservation.CreatedBy == currentUserId);

        var rows = await query
            .OrderByDescending(x => x.reservation.CreatedAt)
            .Select(x => x.reservation)
            .ToListAsync(cancellationToken);

        return await ToResponsesAsync(rows, currentUserId, cancellationToken);
    }

    /// <summary>Chi tiết theo quyền người xem: Sales mở phiếu của người khác nhận 404 như phiếu không tồn tại.</summary>
    public async Task<ReservationDetailResponse> GetDetailForViewerAsync(
        Guid id,
        Guid currentUserId,
        bool canViewAll,
        CancellationToken cancellationToken = default)
    {
        if (!canViewAll)
        {
            var reservation = await FindAsync(id, tracking: false, cancellationToken);

            if (reservation.CreatedBy != currentUserId)
                throw NotFound();
        }

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    public async Task<ReservationDetailResponse> GetDetailAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await ExpireOverdueAsync(cancellationToken);

        var reservation = await FindAsync(id, tracking: false, cancellationToken);
        var response = (await ToResponsesAsync([reservation], currentUserId, cancellationToken)).Single();

        var items = await (
            from item in db.ReservationItems.AsNoTracking()
            join model in db.EquipmentModels.AsNoTracking() on item.EquipmentModelId equals model.Id
            where item.ReservationId == id
            orderby model.Name
            select new { item, model }
        ).ToListAsync(cancellationToken);

        var availability = await availabilityService.GetAvailabilityAsync(
            items.Select(x => x.model.Id).ToList(),
            reservation.RequestedFrom,
            reservation.RequestedTo,
            excludeReservationId: id,
            cancellationToken: cancellationToken);

        return new ReservationDetailResponse(
            response,
            items.Select(x => new ReservationItemResponse(
                x.item.Id,
                x.model.Id,
                x.model.Code,
                x.model.Name,
                x.model.Unit,
                x.model.RentalPriceDaily,
                x.model.RentalPriceMonthly,
                x.item.Quantity,
                x.item.Note,
                availability[x.model.Id].AvailableQuantity)).ToList());
    }

    public async Task<ReservationDetailResponse> CreateAsync(
        CreateReservationRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        var (from, to) = ValidateDates(request.RequestedFrom!.Value, request.RequestedTo!.Value);
        await EnsureCompanyActiveAsync(request.CompanyId!.Value, cancellationToken);

        var now = DateTimeOffset.UtcNow;

        var reservation = new Reservation
        {
            Id = Guid.CreateVersion7(),
            ReservationCode = await GenerateCodeAsync(now, cancellationToken),
            CompanyId = request.CompanyId!.Value,
            RequestedFrom = from,
            RequestedTo = to,
            ContactName = request.ContactName.Trim(),
            ContactPhone = request.ContactPhone.Trim(),
            ExpiresAt = CalculateExpiresAt(now, from),
            Note = NormalizeOptional(request.Note),
            Status = ReservationStatus.Pending,
            CreatedBy = currentUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Reservations.Add(reservation);
        await SaveChangesAsync(cancellationToken);

        return await GetDetailAsync(reservation.Id, currentUserId, cancellationToken);
    }

    public async Task<ReservationDetailResponse> UpdateAsync(
        Guid id,
        UpdateReservationRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await ExpireOverdueAsync(cancellationToken);

        var (from, to) = ValidateDates(request.RequestedFrom!.Value, request.RequestedTo!.Value);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await FindAsync(id, tracking: true, cancellationToken);
        EnsureEditable(reservation, currentUserId, ReservationStatus.Pending);

        if (request.CompanyId!.Value != reservation.CompanyId)
            await EnsureCompanyActiveAsync(request.CompanyId!.Value, cancellationToken);

        var datesChanged = from != reservation.RequestedFrom || to != reservation.RequestedTo;

        if (datesChanged)
        {
            var items = await db.ReservationItems
                .AsNoTracking()
                .Where(i => i.ReservationId == id)
                .ToListAsync(cancellationToken);

            await EnsureAvailableAsync(
                items.ToDictionary(i => i.EquipmentModelId, i => i.Quantity),
                from,
                to,
                id,
                cancellationToken);
        }

        reservation.CompanyId = request.CompanyId!.Value;
        reservation.RequestedFrom = from;
        reservation.RequestedTo = to;
        reservation.ContactName = request.ContactName.Trim();
        reservation.ContactPhone = request.ContactPhone.Trim();
        reservation.Note = NormalizeOptional(request.Note);
        // Đổi ngày bắt đầu sớm hơn có thể rút ngắn hạn, nhưng không bao giờ kéo dài quá hạn ban đầu.
        reservation.ExpiresAt = Min(reservation.ExpiresAt, CalculateExpiresAt(reservation.CreatedAt, from));
        reservation.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    public async Task<ReservationDetailResponse> UpdateItemsAsync(
        Guid id,
        UpdateReservationItemsRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await ExpireOverdueAsync(cancellationToken);

        // Gộp số lượng theo model để không lách giới hạn bằng cách tách nhiều dòng.
        var merged = request.Items
            .GroupBy(i => i.EquipmentModelId!.Value)
            .Select(g => new
            {
                EquipmentModelId = g.Key,
                Quantity = g.Sum(i => i.Quantity),
                Note = string.Join("; ", g.Select(i => NormalizeOptional(i.Note)).OfType<string>()),
            })
            .ToList();

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await FindAsync(id, tracking: true, cancellationToken);
        EnsureEditable(reservation, currentUserId, ReservationStatus.Pending);

        await EnsureModelsActiveAsync(merged.Select(i => i.EquipmentModelId).ToList(), cancellationToken);

        await EnsureAvailableAsync(
            merged.ToDictionary(i => i.EquipmentModelId, i => i.Quantity),
            reservation.RequestedFrom,
            reservation.RequestedTo,
            id,
            cancellationToken);

        var existing = await db.ReservationItems
            .Where(i => i.ReservationId == id)
            .ToListAsync(cancellationToken);

        db.ReservationItems.RemoveRange(existing);
        db.ReservationItems.AddRange(merged.Select(i => new ReservationItem
        {
            Id = Guid.CreateVersion7(),
            ReservationId = id,
            EquipmentModelId = i.EquipmentModelId,
            Quantity = i.Quantity,
            Note = string.IsNullOrEmpty(i.Note) ? null : i.Note,
        }));

        reservation.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    /// <summary>Sales xác nhận phiếu. Chỉ phiếu đã xác nhận mới được tạo hợp đồng.</summary>
    public async Task<ReservationDetailResponse> ApproveAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await ExpireOverdueAsync(cancellationToken);

        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var reservation = await FindAsync(id, tracking: true, cancellationToken);
        EnsureEditable(reservation, currentUserId, ReservationStatus.Pending);

        var items = await db.ReservationItems
            .AsNoTracking()
            .Where(i => i.ReservationId == id)
            .ToListAsync(cancellationToken);

        if (items.Count == 0)
        {
            throw new AppException(
                "RESERVATION_EMPTY",
                "Phiếu chưa có thiết bị nào. Hãy thêm model trước khi xác nhận.",
                StatusCodes.Status409Conflict);
        }

        // Kiểm tra lại: số máy có thể đã thay đổi từ lúc thêm thiết bị.
        await EnsureAvailableAsync(
            items.ToDictionary(i => i.EquipmentModelId, i => i.Quantity),
            reservation.RequestedFrom,
            reservation.RequestedTo,
            id,
            cancellationToken);

        reservation.Status = ReservationStatus.Approved;
        reservation.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    /// <summary>Đưa phiếu đã xác nhận về PENDING để chỉnh sửa (giữ nguyên hạn giữ chỗ).</summary>
    public async Task<ReservationDetailResponse> ReopenAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await ExpireOverdueAsync(cancellationToken);

        var reservation = await FindAsync(id, tracking: true, cancellationToken);
        EnsureEditable(reservation, currentUserId, ReservationStatus.Approved);

        reservation.Status = ReservationStatus.Pending;
        reservation.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveChangesAsync(cancellationToken);

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    public async Task<ReservationDetailResponse> CancelAsync(
        Guid id,
        CancelReservationRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await ExpireOverdueAsync(cancellationToken);

        var reservation = await FindAsync(id, tracking: true, cancellationToken);
        EnsureEditable(reservation, currentUserId, ReservationStatus.Pending, ReservationStatus.Approved);

        var reason = NormalizeOptional(request.Reason);

        if (reason is not null)
        {
            var line = $"[Hủy] {reason}";
            reservation.Note = reservation.Note is null ? line : $"{reservation.Note}\n{line}";
        }

        reservation.Status = ReservationStatus.Cancelled;
        reservation.UpdatedAt = DateTimeOffset.UtcNow;

        await SaveChangesAsync(cancellationToken);

        return await GetDetailAsync(id, currentUserId, cancellationToken);
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>Chuyển phiếu PENDING/APPROVED đã quá hạn giữ chỗ sang EXPIRED.</summary>
    private Task ExpireOverdueAsync(CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        return db.Reservations
            .Where(r => (r.Status == ReservationStatus.Pending || r.Status == ReservationStatus.Approved)
                        && r.ExpiresAt <= now)
            .ExecuteUpdateAsync(setters => setters
                    .SetProperty(r => r.Status, ReservationStatus.Expired)
                    .SetProperty(r => r.UpdatedAt, now),
                cancellationToken);
    }

    private DateTimeOffset CalculateExpiresAt(DateTimeOffset createdAt, DateOnly requestedFrom)
    {
        var holdUntil = createdAt.AddDays(_options.HoldDays).ToUniversalTime();
        var endOfStartDay = EquipmentAvailabilityService
            .StartOfBusinessDay(requestedFrom.AddDays(1))
            .ToUniversalTime();

        return holdUntil <= endOfStartDay ? holdUntil : endOfStartDay;
    }

    private static DateTimeOffset Min(DateTimeOffset a, DateTimeOffset b) => a <= b ? a : b;

    private static (DateOnly From, DateOnly To) ValidateDates(DateOnly from, DateOnly to)
    {
        if (from < EquipmentAvailabilityService.BusinessToday())
        {
            throw new AppException(
                "REQUESTED_FROM_IN_PAST",
                "Ngày bắt đầu thuê không được trước hôm nay.",
                StatusCodes.Status400BadRequest,
                "requestedFrom");
        }

        if (to < from)
        {
            throw new AppException(
                "INVALID_DATE_RANGE",
                "Ngày kết thúc phải lớn hơn hoặc bằng ngày bắt đầu.",
                StatusCodes.Status400BadRequest,
                "requestedTo");
        }

        return (from, to);
    }

    private async Task EnsureCompanyActiveAsync(Guid companyId, CancellationToken cancellationToken)
    {
        var company = await db.Companies
            .AsNoTracking()
            .Where(c => c.Id == companyId)
            .Select(c => new { c.Status })
            .SingleOrDefaultAsync(cancellationToken);

        if (company is null)
        {
            throw new AppException(
                "COMPANY_NOT_FOUND",
                "Không tìm thấy công ty.",
                StatusCodes.Status400BadRequest,
                "companyId");
        }

        if (company.Status != CompanyStatus.Active)
        {
            throw new AppException(
                "COMPANY_INACTIVE",
                "Công ty đang ngừng hoạt động, không thể lập phiếu.",
                StatusCodes.Status409Conflict,
                "companyId");
        }
    }

    private async Task EnsureModelsActiveAsync(List<Guid> modelIds, CancellationToken cancellationToken)
    {
        var models = await db.EquipmentModels
            .AsNoTracking()
            .Where(m => modelIds.Contains(m.Id))
            .Select(m => new { m.Id, m.Code, m.Status })
            .ToListAsync(cancellationToken);

        if (models.Count != modelIds.Count)
        {
            throw new AppException(
                "EQUIPMENT_MODEL_NOT_FOUND",
                "Có model thiết bị không tồn tại.",
                StatusCodes.Status400BadRequest,
                "items");
        }

        var inactive = models.FirstOrDefault(m => m.Status != EquipmentModelStatus.Active);

        if (inactive is not null)
        {
            throw new AppException(
                "EQUIPMENT_MODEL_INACTIVE",
                $"Model {inactive.Code} đã ngừng cho thuê.",
                StatusCodes.Status409Conflict,
                "items");
        }
    }

    /// <summary>
    /// Khóa các model rồi kiểm tra số lượng khả dụng (loại trừ chính phiếu này).
    /// Phải gọi trong transaction để khóa giữ đến khi lưu xong.
    /// </summary>
    private async Task EnsureAvailableAsync(
        Dictionary<Guid, int> quantityByModel,
        DateOnly from,
        DateOnly to,
        Guid reservationId,
        CancellationToken cancellationToken)
    {
        if (quantityByModel.Count == 0)
            return;

        var modelIds = quantityByModel.Keys.ToList();

        await availabilityService.LockModelsAsync(modelIds, cancellationToken);

        var availability = await availabilityService.GetAvailabilityAsync(
            modelIds,
            from,
            to,
            excludeReservationId: reservationId,
            cancellationToken: cancellationToken);

        var shortages = quantityByModel
            .Where(x => x.Value > availability[x.Key].AvailableQuantity)
            .Select(x => new { ModelId = x.Key, Requested = x.Value, Available = availability[x.Key].AvailableQuantity })
            .ToList();

        if (shortages.Count == 0)
            return;

        var codes = await db.EquipmentModels
            .AsNoTracking()
            .Where(m => shortages.Select(s => s.ModelId).Contains(m.Id))
            .ToDictionaryAsync(m => m.Id, m => m.Code, cancellationToken);

        var details = string.Join("; ", shortages.Select(s =>
            $"{codes.GetValueOrDefault(s.ModelId, "?")}: cần {s.Requested}, còn {s.Available}"));

        throw new AppException(
            "INSUFFICIENT_EQUIPMENT",
            $"Không đủ thiết bị trong khoảng ngày đã chọn ({details}).",
            StatusCodes.Status409Conflict,
            "items");
    }

    private async Task<Reservation> FindAsync(Guid id, bool tracking, CancellationToken cancellationToken)
    {
        var query = tracking ? db.Reservations : db.Reservations.AsNoTracking();

        return await query.SingleOrDefaultAsync(r => r.Id == id, cancellationToken)
               ?? throw NotFound();
    }

    private static AppException NotFound()
        => new("RESERVATION_NOT_FOUND", "Không tìm thấy phiếu đặt thuê.", StatusCodes.Status404NotFound);

    private static void EnsureEditable(
        Reservation reservation,
        Guid currentUserId,
        params ReservationStatus[] allowedStatuses)
    {
        if (reservation.CreatedBy != currentUserId)
        {
            throw new AppException(
                "RESERVATION_NOT_OWNED",
                "Chỉ nhân viên lập phiếu mới được thao tác.",
                StatusCodes.Status403Forbidden);
        }

        if (!allowedStatuses.Contains(reservation.Status))
        {
            var message = reservation.Status switch
            {
                ReservationStatus.Approved => "Phiếu đã xác nhận. Hãy mở lại để chỉnh sửa.",
                ReservationStatus.Expired => "Phiếu đã hết hạn giữ chỗ.",
                ReservationStatus.Cancelled => "Phiếu đã bị hủy.",
                ReservationStatus.Converted => "Phiếu đã được tạo hợp đồng.",
                _ => "Trạng thái phiếu không cho phép thao tác này.",
            };

            throw new AppException("INVALID_RESERVATION_STATUS", message, StatusCodes.Status409Conflict);
        }
    }

    /// <summary>PH-YYYY-NNNN, đánh số lại từ 0001 mỗi năm.</summary>
    private async Task<string> GenerateCodeAsync(DateTimeOffset now, CancellationToken cancellationToken)
    {
        var year = EquipmentAvailabilityService.BusinessToday().Year;
        var prefix = $"{CodePrefix}-{year}-";

        var codes = await db.Reservations
            .AsNoTracking()
            .Where(r => r.ReservationCode.StartsWith(prefix))
            .Select(r => r.ReservationCode)
            .ToListAsync(cancellationToken);

        var maxNumber = codes
            .Select(code => int.TryParse(code[prefix.Length..], out var number) ? number : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{maxNumber + 1:D4}";
    }

    private async Task<List<ReservationResponse>> ToResponsesAsync(
        List<Reservation> reservations,
        Guid currentUserId,
        CancellationToken cancellationToken)
    {
        var ids = reservations.Select(r => r.Id).ToList();
        var companyIds = reservations.Select(r => r.CompanyId).Distinct().ToList();
        var creatorIds = reservations.Select(r => r.CreatedBy).Distinct().ToList();

        var companies = await db.Companies
            .AsNoTracking()
            .Where(c => companyIds.Contains(c.Id))
            .ToDictionaryAsync(c => c.Id, c => new { c.CompanyCode, c.Name }, cancellationToken);

        var creators = await db.Users
            .AsNoTracking()
            .Where(u => creatorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        var itemStats = await db.ReservationItems
            .AsNoTracking()
            .Where(i => ids.Contains(i.ReservationId))
            .GroupBy(i => i.ReservationId)
            .Select(g => new { ReservationId = g.Key, Count = g.Count(), Total = g.Sum(i => i.Quantity) })
            .ToDictionaryAsync(x => x.ReservationId, cancellationToken);

        return reservations.Select(r =>
        {
            var company = companies[r.CompanyId];
            var stats = itemStats.GetValueOrDefault(r.Id);

            return new ReservationResponse(
                r.Id,
                r.ReservationCode,
                r.CompanyId,
                company.CompanyCode,
                company.Name,
                r.RequestedFrom,
                r.RequestedTo,
                r.ContactName,
                r.ContactPhone,
                r.ExpiresAt,
                EnumName.ToDatabaseValue(r.Status),
                r.Note,
                stats?.Count ?? 0,
                stats?.Total ?? 0,
                r.CreatedBy,
                creators.GetValueOrDefault(r.CreatedBy),
                r.CreatedBy == currentUserId,
                r.CreatedAt);
        }).ToList();
    }

    private static ReservationStatus ParseStatus(string status)
    {
        return status.Trim().ToUpperInvariant() switch
        {
            "PENDING" => ReservationStatus.Pending,
            "APPROVED" => ReservationStatus.Approved,
            "REJECTED" => ReservationStatus.Rejected,
            "CANCELLED" => ReservationStatus.Cancelled,
            "EXPIRED" => ReservationStatus.Expired,
            "CONVERTED" => ReservationStatus.Converted,
            _ => throw new AppException(
                "INVALID_RESERVATION_STATUS",
                "Trạng thái không hợp lệ.",
                StatusCodes.Status400BadRequest,
                "status"),
        };
    }

    private async Task SaveChangesAsync(CancellationToken cancellationToken)
    {
        try
        {
            await db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException ex) when (ex.InnerException is Npgsql.PostgresException { SqlState: "23505" })
        {
            // Trùng mã phiếu do hai phiếu được tạo cùng lúc.
            throw new AppException(
                "DUPLICATE_RESERVATION_CODE",
                "Mã phiếu bị trùng do tạo đồng thời, vui lòng thử lại.",
                StatusCodes.Status409Conflict);
        }
    }

    private static string? NormalizeOptional(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
