using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Entities;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Services;

/// <summary>
/// Thủ kho soạn phiếu bàn giao cho hợp đồng đã ký.
///
/// Luồng phiếu: thủ kho tạo (PREPARING, chọn máy theo serial) → soạn xong (PENDING_APPROVAL)
/// → quản lý duyệt xuất kho, kỹ thuật viên giao (SHIPPING → COMPLETED, phần Manager/Technician).
///
/// Quy tắc:
/// - Chỉ hợp đồng SIGNED / PREPARING / PARTIALLY_DELIVERED mới soạn bàn giao. Phiếu đầu tiên
///   chuyển hợp đồng SIGNED → PREPARING. Một hợp đồng có thể giao nhiều đợt (nhiều phiếu).
/// - Máy chọn phải thuộc model trong hợp đồng, đang AVAILABLE và không hỏng; khi vào phiếu
///   chuyển RESERVED (giữ cho phiếu), bỏ khỏi phiếu hoặc hủy phiếu thì trả về AVAILABLE.
/// - Tổng số máy mỗi model trên các phiếu chưa hủy không vượt số lượng hợp đồng.
/// - Mã phiếu BG-YYYY-NNNN.
/// </summary>
public sealed class HandoverService(AppDbContext db)
{
    private const string CodePrefix = "BG";

    private static readonly RentalContractStatus[] PreparableStatuses =
    [
        RentalContractStatus.Signed,
        RentalContractStatus.Preparing,
        RentalContractStatus.PartiallyDelivered,
    ];

    // ================================================================ đọc

    /// <summary>Hợp đồng cần soạn bàn giao, hợp đồng giao sớm nhất trước.</summary>
    public async Task<List<HandoverContractResponse>> GetContractsToPrepareAsync(
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        var query =
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where PreparableStatuses.Contains(contract.Status)
            select new { contract, company };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.contract.ContractCode, pattern) ||
                EF.Functions.ILike(x.company.Name, pattern) ||
                EF.Functions.ILike(x.company.CompanyCode, pattern));
        }

        var rows = await query
            .OrderBy(x => x.contract.StartDate)
            .ThenBy(x => x.contract.ContractCode)
            .ToListAsync(cancellationToken);

        var progress = await GetProgressAsync(rows.Select(x => x.contract.Id).ToList(), cancellationToken);

        return rows
            .Select(x => ToContractResponse(x.contract, x.company.CompanyCode, x.company.Name, progress))
            .ToList();
    }

    public async Task<HandoverContractDetailResponse> GetContractDetailAsync(
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        var row = await (
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where contract.Id == contractId
            select new { contract, company }
        ).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

        var progress = await GetProgressAsync([contractId], cancellationToken);
        var allocatedByModel = await AllocatedByModelAsync(contractId, excludeNoteId: null, cancellationToken);

        var items = await (
            from item in db.RentalContractItems.AsNoTracking()
            join model in db.EquipmentModels.AsNoTracking() on item.EquipmentModelId equals model.Id
            where item.RentalContractId == contractId
            orderby model.Name
            select new { item.EquipmentModelId, model.Code, model.Name, model.Unit, item.Quantity }
        ).ToListAsync(cancellationToken);

        var modelIds = items.Select(i => i.EquipmentModelId).ToList();

        var inStock = await AvailableEquipmentQuery()
            .Where(e => modelIds.Contains(e.EquipmentModelId))
            .GroupBy(e => e.EquipmentModelId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var representative = await db.RentalContractRepresentatives
            .AsNoTracking()
            .Where(r => r.RentalContractId == contractId)
            .Select(r => new { r.FullName, r.Phone })
            .SingleOrDefaultAsync(cancellationToken);

        var notes = await NoteQuery()
            .Where(x => x.note.RentalContractId == contractId)
            .OrderByDescending(x => x.note.CreatedAt)
            .ToListAsync(cancellationToken);

        return new HandoverContractDetailResponse(
            ToContractResponse(row.contract, row.company.CompanyCode, row.company.Name, progress),
            row.contract.DeliveryNote,
            representative?.FullName,
            representative?.Phone,
            items.Select(i => new HandoverContractItemResponse(
                i.EquipmentModelId,
                i.Code,
                i.Name,
                i.Unit,
                i.Quantity,
                allocatedByModel.GetValueOrDefault(i.EquipmentModelId),
                inStock.GetValueOrDefault(i.EquipmentModelId))).ToList(),
            await ToNoteResponsesAsync(notes, cancellationToken));
    }

    /// <summary>Máy sẵn sàng của một model để chọn vào phiếu (theo kho, serial).</summary>
    public async Task<List<AvailableEquipmentResponse>> GetAvailableEquipmentsAsync(
        Guid equipmentModelId,
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        var query =
            from equipment in AvailableEquipmentQuery()
            join warehouse in db.Warehouses.AsNoTracking() on equipment.WarehouseId equals warehouse.Id
            where equipment.EquipmentModelId == equipmentModelId
            select new { equipment, warehouse.Name };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(x => EF.Functions.ILike(x.equipment.SerialNumber, pattern));
        }

        return await query
            .OrderBy(x => x.Name)
            .ThenBy(x => x.equipment.SerialNumber)
            .Select(x => new AvailableEquipmentResponse(
                x.equipment.Id,
                x.equipment.SerialNumber,
                x.equipment.EquipmentModelId,
                x.equipment.WarehouseId,
                x.Name,
                x.equipment.Condition))
            .ToListAsync(cancellationToken);
    }

    public async Task<List<HandoverNoteResponse>> GetNotesAsync(
        string? status,
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        var query = NoteQuery();

        if (!string.IsNullOrWhiteSpace(status))
        {
            var noteStatus = ParseStatus(status);
            query = query.Where(x => x.note.Status == noteStatus);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.note.HandoverCode, pattern) ||
                EF.Functions.ILike(x.ContractCode, pattern) ||
                EF.Functions.ILike(x.CompanyName, pattern));
        }

        var notes = await query
            .OrderByDescending(x => x.note.CreatedAt)
            .ToListAsync(cancellationToken);

        return await ToNoteResponsesAsync(notes, cancellationToken);
    }

    public async Task<HandoverNoteDetailResponse> GetNoteDetailAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var row = await NoteQuery().SingleOrDefaultAsync(x => x.note.Id == id, cancellationToken)
                  ?? throw NoteNotFound();

        var contract = await db.RentalContracts
            .AsNoTracking()
            .Where(c => c.Id == row.note.RentalContractId)
            .Select(c => new { c.DeliveryAddress, c.DeliveryNote })
            .SingleAsync(cancellationToken);

        var items = await (
            from item in db.HandoverItems.AsNoTracking()
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            join model in db.EquipmentModels.AsNoTracking() on equipment.EquipmentModelId equals model.Id
            join warehouse in db.Warehouses.AsNoTracking() on equipment.WarehouseId equals warehouse.Id
            where item.HandoverNoteId == id
            orderby model.Name, equipment.SerialNumber
            select new { item, equipment, model.Code, ModelName = model.Name, WarehouseName = warehouse.Name }
        ).ToListAsync(cancellationToken);

        return new HandoverNoteDetailResponse(
            (await ToNoteResponsesAsync([row], cancellationToken)).Single(),
            contract.DeliveryAddress,
            contract.DeliveryNote,
            items.Select(x => new HandoverItemResponse(
                x.item.Id,
                x.equipment.Id,
                x.equipment.SerialNumber,
                x.equipment.EquipmentModelId,
                x.Code,
                x.ModelName,
                x.WarehouseName,
                x.equipment.Condition,
                EnumName.ToDatabaseValue(x.equipment.Status),
                x.item.Note)).ToList());
    }

    /// <summary>
    /// Các đợt giao của một hợp đồng (bỏ phiếu đã hủy), kèm danh sách máy — dùng cho khách
    /// theo dõi bàn giao.
    /// </summary>
    public async Task<List<HandoverNoteDetailResponse>> GetContractDeliveriesAsync(
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        var ids = await db.HandoverNotes
            .AsNoTracking()
            .Where(n => n.RentalContractId == contractId && n.Status != HandoverNoteStatus.Cancelled)
            .OrderBy(n => n.HandoverDate)
            .ThenBy(n => n.CreatedAt)
            .Select(n => n.Id)
            .ToListAsync(cancellationToken);

        var result = new List<HandoverNoteDetailResponse>(ids.Count);

        foreach (var id in ids)
            result.Add(await GetNoteDetailAsync(id, cancellationToken));

        return result;
    }

    // ================================================================ ghi

    public async Task<HandoverNoteDetailResponse> CreateAsync(
        CreateHandoverRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var contract = await LockContractAsync(request.RentalContractId!.Value, cancellationToken);

        if (!PreparableStatuses.Contains(contract.Status))
        {
            throw new AppException(
                "CONTRACT_NOT_PREPARABLE",
                "Chỉ hợp đồng đã ký và chưa giao đủ thiết bị mới được soạn bàn giao.",
                StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;

        var note = new HandoverNote
        {
            Id = Guid.CreateVersion7(),
            HandoverCode = await GenerateCodeAsync(cancellationToken),
            RentalContractId = contract.Id,
            DeliveryAttemptNumber = 1,
            Status = HandoverNoteStatus.Preparing,
            CreatedBy = currentUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApplyInfo(note, request, contract);
        db.HandoverNotes.Add(note);

        await ReserveItemsAsync(note, contract, request.Items, currentItems: [], cancellationToken);

        if (contract.Status == RentalContractStatus.Signed)
        {
            ChangeContractStatus(contract, RentalContractStatus.Preparing, currentUserId,
                $"Kho bắt đầu soạn bàn giao {note.HandoverCode}");
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetNoteDetailAsync(note.Id, cancellationToken);
    }

    public async Task<HandoverNoteDetailResponse> UpdateAsync(
        Guid id,
        SaveHandoverRequest request,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var note = await db.HandoverNotes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken)
                   ?? throw NoteNotFound();
        var contract = await LockContractAsync(note.RentalContractId, cancellationToken);

        EnsureStatus(note, HandoverNoteStatus.Preparing);

        var currentItems = await db.HandoverItems
            .Where(i => i.HandoverNoteId == id)
            .ToListAsync(cancellationToken);

        ApplyInfo(note, request, contract);
        note.UpdatedAt = DateTimeOffset.UtcNow;

        await ReserveItemsAsync(note, contract, request.Items, currentItems, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetNoteDetailAsync(id, cancellationToken);
    }

    /// <summary>Thủ kho soạn xong, chuyển phiếu chờ quản lý duyệt xuất kho.</summary>
    public async Task<HandoverNoteDetailResponse> SubmitAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var note = await db.HandoverNotes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken)
                   ?? throw NoteNotFound();

        EnsureStatus(note, HandoverNoteStatus.Preparing);

        if (!await db.HandoverItems.AnyAsync(i => i.HandoverNoteId == id, cancellationToken))
        {
            throw new AppException(
                "HANDOVER_EMPTY",
                "Phiếu bàn giao chưa có máy nào.",
                StatusCodes.Status409Conflict);
        }

        note.Status = HandoverNoteStatus.PendingApproval;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        return await GetNoteDetailAsync(id, cancellationToken);
    }

    /// <summary>
    /// Hủy phiếu khi chưa xuất kho (PREPARING, PENDING_APPROVAL): trả máy về AVAILABLE.
    /// Hợp đồng không còn phiếu nào thì quay lại SIGNED.
    /// </summary>
    public async Task<HandoverNoteDetailResponse> CancelAsync(
        Guid id,
        CancelHandoverRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var note = await db.HandoverNotes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken)
                   ?? throw NoteNotFound();
        var contract = await LockContractAsync(note.RentalContractId, cancellationToken);

        EnsureStatus(note, HandoverNoteStatus.Preparing, HandoverNoteStatus.PendingApproval);

        var equipmentIds = await db.HandoverItems
            .Where(i => i.HandoverNoteId == id)
            .Select(i => i.EquipmentId)
            .ToListAsync(cancellationToken);

        await ReleaseAsync(equipmentIds, cancellationToken);

        var reason = request.Reason.Trim();
        note.Status = HandoverNoteStatus.Cancelled;
        note.Note = string.IsNullOrWhiteSpace(note.Note) ? $"Hủy: {reason}" : $"{note.Note}\nHủy: {reason}";
        note.UpdatedAt = DateTimeOffset.UtcNow;

        var hasOtherNotes = await db.HandoverNotes.AnyAsync(n =>
            n.RentalContractId == contract.Id &&
            n.Id != id &&
            n.Status != HandoverNoteStatus.Cancelled, cancellationToken);

        if (!hasOtherNotes && contract.Status == RentalContractStatus.Preparing)
        {
            ChangeContractStatus(contract, RentalContractStatus.Signed, currentUserId,
                $"Hủy phiếu bàn giao {note.HandoverCode}: {reason}");
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetNoteDetailAsync(id, cancellationToken);
    }

    // ================================================================ hỗ trợ

    private IQueryable<Equipment.Entities.Equipment> AvailableEquipmentQuery() =>
        db.Equipments.AsNoTracking().Where(e =>
            e.Status == EquipmentStatus.Available && e.Condition != "DAMAGED");

    private IQueryable<NoteRow> NoteQuery() =>
        from note in db.HandoverNotes.AsNoTracking()
        join contract in db.RentalContracts.AsNoTracking() on note.RentalContractId equals contract.Id
        join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
        select new NoteRow { note = note, ContractCode = contract.ContractCode, CompanyName = company.Name };

    private sealed class NoteRow
    {
        public HandoverNote note { get; init; } = null!;
        public string ContractCode { get; init; } = null!;
        public string CompanyName { get; init; } = null!;
    }

    private async Task<List<HandoverNoteResponse>> ToNoteResponsesAsync(
        List<NoteRow> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.note.Id).ToList();
        var creatorIds = rows.Select(r => r.note.CreatedBy).Distinct().ToList();

        var counts = await db.HandoverItems.AsNoTracking()
            .Where(i => ids.Contains(i.HandoverNoteId))
            .GroupBy(i => i.HandoverNoteId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var creators = await db.Users.AsNoTracking()
            .Where(u => creatorIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return rows.Select(r => new HandoverNoteResponse(
            r.note.Id,
            r.note.HandoverCode,
            r.note.RentalContractId,
            r.ContractCode,
            r.CompanyName,
            r.note.HandoverDate,
            r.note.ReceivedByName,
            r.note.ReceivedByPhone,
            r.note.Note,
            EnumName.ToDatabaseValue(r.note.Status),
            counts.GetValueOrDefault(r.note.Id),
            creators.GetValueOrDefault(r.note.CreatedBy),
            r.note.CreatedAt,
            r.note.UpdatedAt)).ToList();
    }

    private sealed record Progress(int Total, int Allocated, int ActiveNotes);

    private async Task<Dictionary<Guid, Progress>> GetProgressAsync(
        List<Guid> contractIds,
        CancellationToken cancellationToken)
    {
        var totals = await db.RentalContractItems.AsNoTracking()
            .Where(i => contractIds.Contains(i.RentalContractId))
            .GroupBy(i => i.RentalContractId)
            .Select(g => new { g.Key, Total = g.Sum(i => i.Quantity) })
            .ToDictionaryAsync(x => x.Key, x => x.Total, cancellationToken);

        var allocated = await (
            from note in db.HandoverNotes.AsNoTracking()
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            where contractIds.Contains(note.RentalContractId) && note.Status != HandoverNoteStatus.Cancelled
            group item by note.RentalContractId into g
            select new { g.Key, Count = g.Count() }
        ).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var noteCounts = await db.HandoverNotes.AsNoTracking()
            .Where(n => contractIds.Contains(n.RentalContractId) && n.Status != HandoverNoteStatus.Cancelled)
            .GroupBy(n => n.RentalContractId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        return contractIds.ToDictionary(id => id, id => new Progress(
            totals.GetValueOrDefault(id),
            allocated.GetValueOrDefault(id),
            noteCounts.GetValueOrDefault(id)));
    }

    /// <summary>Số máy mỗi model đã nằm trên các phiếu chưa hủy của hợp đồng.</summary>
    private async Task<Dictionary<Guid, int>> AllocatedByModelAsync(
        Guid contractId,
        Guid? excludeNoteId,
        CancellationToken cancellationToken)
    {
        return await (
            from note in db.HandoverNotes.AsNoTracking()
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            where note.RentalContractId == contractId &&
                  note.Status != HandoverNoteStatus.Cancelled &&
                  (excludeNoteId == null || note.Id != excludeNoteId)
            group item by equipment.EquipmentModelId into g
            select new { g.Key, Count = g.Count() }
        ).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);
    }

    private static HandoverContractResponse ToContractResponse(
        RentalContract contract,
        string companyCode,
        string companyName,
        Dictionary<Guid, Progress> progress)
    {
        var p = progress.GetValueOrDefault(contract.Id) ?? new Progress(0, 0, 0);

        return new HandoverContractResponse(
            contract.Id,
            contract.ContractCode,
            companyCode,
            companyName,
            contract.StartDate,
            contract.EndDate,
            EnumName.ToDatabaseValue(contract.Status),
            contract.DeliveryAddress,
            p.Total,
            p.Allocated,
            p.ActiveNotes);
    }

    private static void ApplyInfo(HandoverNote note, SaveHandoverRequest request, RentalContract contract)
    {
        var date = request.HandoverDate!.Value;

        if (date < EquipmentAvailabilityService.BusinessToday() || date > contract.EndDate)
        {
            throw new AppException(
                "INVALID_HANDOVER_DATE",
                "Ngày giao phải từ hôm nay đến ngày kết thúc hợp đồng.",
                StatusCodes.Status400BadRequest,
                "handoverDate");
        }

        note.HandoverDate = date;
        note.ReceivedByName = request.ReceivedByName.Trim();
        note.ReceivedByPhone = string.IsNullOrWhiteSpace(request.ReceivedByPhone) ? null : request.ReceivedByPhone.Trim();
        note.Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
    }

    /// <summary>
    /// Đặt danh sách máy của phiếu: máy mới phải AVAILABLE, đúng model hợp đồng và còn trong
    /// số lượng; giữ (RESERVED) máy mới, trả (AVAILABLE) máy bị bỏ ra.
    /// </summary>
    private async Task ReserveItemsAsync(
        HandoverNote note,
        RentalContract contract,
        List<HandoverItemRequest> requested,
        List<HandoverItem> currentItems,
        CancellationToken cancellationToken)
    {
        var requestedIds = requested.Select(i => i.EquipmentId!.Value).ToList();

        if (requestedIds.Distinct().Count() != requestedIds.Count)
        {
            throw new AppException("DUPLICATE_EQUIPMENT", "Một máy chỉ được chọn một lần.",
                StatusCodes.Status400BadRequest, "items");
        }

        var equipments = await db.Equipments.AsNoTracking()
            .Where(e => requestedIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        if (equipments.Count != requestedIds.Count)
        {
            throw new AppException("EQUIPMENT_NOT_FOUND", "Có máy không tồn tại.",
                StatusCodes.Status404NotFound, "items");
        }

        // Số lượng theo model: phiếu này + các phiếu khác chưa hủy ≤ số lượng hợp đồng.
        var contractQuantities = await db.RentalContractItems.AsNoTracking()
            .Where(i => i.RentalContractId == contract.Id)
            .ToDictionaryAsync(i => i.EquipmentModelId, i => i.Quantity, cancellationToken);
        var allocatedElsewhere = await AllocatedByModelAsync(contract.Id, note.Id, cancellationToken);

        foreach (var group in equipments.Values.GroupBy(e => e.EquipmentModelId))
        {
            if (!contractQuantities.TryGetValue(group.Key, out var quantity))
            {
                throw new AppException(
                    "EQUIPMENT_MODEL_NOT_IN_CONTRACT",
                    $"Máy {group.First().SerialNumber} không thuộc model nào trong hợp đồng.",
                    StatusCodes.Status400BadRequest,
                    "items");
            }

            var remaining = quantity - allocatedElsewhere.GetValueOrDefault(group.Key);

            if (group.Count() > remaining)
            {
                throw new AppException(
                    "HANDOVER_QUANTITY_EXCEEDED",
                    $"Model của máy {group.First().SerialNumber} chỉ còn {Math.Max(remaining, 0)} máy cần giao theo hợp đồng.",
                    StatusCodes.Status400BadRequest,
                    "items");
            }
        }

        var currentIds = currentItems.Select(i => i.EquipmentId).ToHashSet();
        var added = requestedIds.Where(id => !currentIds.Contains(id)).ToList();
        var removed = currentItems.Where(i => !requestedIds.Contains(i.EquipmentId)).ToList();

        if (added.Any(id => equipments[id].Condition == "DAMAGED"))
        {
            throw new AppException("EQUIPMENT_DAMAGED", "Không thể giao máy đang hỏng.",
                StatusCodes.Status400BadRequest, "items");
        }

        // Giữ máy mới: chỉ đổi được máy còn AVAILABLE (chặn hai phiếu chọn cùng máy).
        if (added.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var reserved = await db.Equipments
                .Where(e => added.Contains(e.Id) && e.Status == EquipmentStatus.Available)
                .ExecuteUpdateAsync(s => s
                    .SetProperty(e => e.Status, EquipmentStatus.Reserved)
                    .SetProperty(e => e.UpdatedAt, now), cancellationToken);

            if (reserved != added.Count)
            {
                throw new AppException(
                    "EQUIPMENT_NOT_AVAILABLE",
                    "Có máy vừa được chọn cho phiếu khác hoặc không còn sẵn sàng. Hãy tải lại danh sách máy.",
                    StatusCodes.Status409Conflict,
                    "items");
            }
        }

        await ReleaseAsync(removed.Select(i => i.EquipmentId).ToList(), cancellationToken);
        db.HandoverItems.RemoveRange(removed);

        var notes = requested.ToDictionary(i => i.EquipmentId!.Value, i => string.IsNullOrWhiteSpace(i.Note) ? null : i.Note.Trim());

        foreach (var item in currentItems.Where(i => requestedIds.Contains(i.EquipmentId)))
            item.Note = notes[item.EquipmentId];

        foreach (var id in added)
        {
            db.HandoverItems.Add(new HandoverItem
            {
                Id = Guid.CreateVersion7(),
                HandoverNoteId = note.Id,
                EquipmentId = id,
                Note = notes[id],
            });
        }
    }

    /// <summary>Trả máy đang giữ cho phiếu về AVAILABLE.</summary>
    private async Task ReleaseAsync(List<Guid> equipmentIds, CancellationToken cancellationToken)
    {
        if (equipmentIds.Count == 0)
            return;

        var now = DateTimeOffset.UtcNow;

        await db.Equipments
            .Where(e => equipmentIds.Contains(e.Id) && e.Status == EquipmentStatus.Reserved)
            .ExecuteUpdateAsync(s => s
                .SetProperty(e => e.Status, EquipmentStatus.Available)
                .SetProperty(e => e.UpdatedAt, now), cancellationToken);
    }

    /// <summary>Khóa dòng hợp đồng để các thao tác soạn phiếu của cùng hợp đồng chạy lần lượt.</summary>
    private async Task<RentalContract> LockContractAsync(Guid contractId, CancellationToken cancellationToken)
    {
        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM rental_contracts WHERE id = {contractId} FOR UPDATE",
            cancellationToken);

        return await db.RentalContracts.SingleOrDefaultAsync(c => c.Id == contractId, cancellationToken)
               ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);
    }

    private void ChangeContractStatus(RentalContract contract, RentalContractStatus status, Guid changedBy, string note)
    {
        var previous = contract.Status;
        contract.Status = status;
        contract.UpdatedAt = DateTimeOffset.UtcNow;

        db.ContractStatusHistory.Add(new ContractStatusHistory
        {
            Id = Guid.CreateVersion7(),
            RentalContractId = contract.Id,
            Status = status,
            PreviousStatus = previous,
            ChangedBy = changedBy,
            ChangedAt = DateTimeOffset.UtcNow,
            Note = note,
        });
    }

    private static void EnsureStatus(HandoverNote note, params HandoverNoteStatus[] allowed)
    {
        if (allowed.Contains(note.Status))
            return;

        var message = note.Status switch
        {
            HandoverNoteStatus.PendingApproval => "Phiếu đã soạn xong, đang chờ duyệt xuất kho.",
            HandoverNoteStatus.Shipping => "Phiếu đang được giao, không thể thay đổi.",
            HandoverNoteStatus.Completed => "Phiếu đã giao xong.",
            HandoverNoteStatus.Cancelled => "Phiếu đã bị hủy.",
            _ => "Trạng thái phiếu không cho phép thao tác này.",
        };

        throw new AppException("HANDOVER_INVALID_STATUS", message, StatusCodes.Status409Conflict);
    }

    private async Task<string> GenerateCodeAsync(CancellationToken cancellationToken)
    {
        var prefix = $"{CodePrefix}-{EquipmentAvailabilityService.BusinessToday().Year}-";

        var codes = await db.HandoverNotes
            .AsNoTracking()
            .Where(n => n.HandoverCode.StartsWith(prefix))
            .Select(n => n.HandoverCode)
            .ToListAsync(cancellationToken);

        var max = codes
            .Select(code => int.TryParse(code[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{max + 1:D4}";
    }

    private static HandoverNoteStatus ParseStatus(string status)
    {
        try
        {
            return EnumName.FromDatabaseValue<HandoverNoteStatus>(status.Trim().ToUpperInvariant());
        }
        catch (ArgumentException)
        {
            throw new AppException("INVALID_HANDOVER_STATUS", "Trạng thái không hợp lệ.", StatusCodes.Status400BadRequest, "status");
        }
    }

    private static AppException NoteNotFound() =>
        new("HANDOVER_NOT_FOUND", "Không tìm thấy phiếu bàn giao.", StatusCodes.Status404NotFound);
}
