using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using HeThongHoTroChoThueThietBi.Features.Operations.DTOs;
using HeThongHoTroChoThueThietBi.Features.Operations.Entities;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Services;

public sealed class HandoverService(AppDbContext db, INotificationDispatcher notificationDispatcher)
{
    private const string CodePrefix = "BG";

    private static readonly RentalContractStatus[] PreparableStatuses =
    [
        RentalContractStatus.Preparing,
        RentalContractStatus.PartiallyDelivered,
    ];

    public async Task<List<HandoverContractResponse>> GetContractsToPrepareAsync(
        string? keyword,
        Guid currentUserId,
        bool isStorekeeperOnly,
        CancellationToken cancellationToken = default)
    {
        var incidentContractMap = await GetIncidentReplacementStatesAsync(null, cancellationToken);
        var incidentContractIds = incidentContractMap.Keys.ToList();

        var query =
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where PreparableStatuses.Contains(contract.Status)
                  || (contract.Status == RentalContractStatus.Delivered && incidentContractIds.Contains(contract.Id))
            select new { contract, company };

        if (isStorekeeperOnly)
        {
            // Ẩn các hợp đồng mà phiếu bàn giao đang mở (chưa hoàn tất/hủy) đã có Thủ kho khác tiếp nhận phụ trách
            query = query.Where(x => !db.HandoverNotes.Any(n =>
                n.RentalContractId == x.contract.Id &&
                n.Status != HandoverNoteStatus.Completed &&
                n.Status != HandoverNoteStatus.Cancelled &&
                n.AssignedStorekeeperId != null &&
                n.AssignedStorekeeperId != currentUserId));
        }

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

        var contractIds = rows.Select(x => x.contract.Id).ToList();
        var progress = await GetProgressAsync(contractIds, cancellationToken);
        var pendingDefReturns = await GetPendingDefectiveReturnsAsync(contractIds, cancellationToken);

        return rows
            .Select(x => ToContractResponse(x.contract, x.company.CompanyCode, x.company.Name, progress, pendingDefReturns, incidentContractMap))
            .ToList();
    }

    public async Task<HandoverContractDetailResponse> GetContractDetailAsync(
        Guid contractId,
        Guid currentUserId,
        bool isStorekeeperOnly,
        CancellationToken cancellationToken = default)
    {
        var row = await (
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where contract.Id == contractId
            select new { contract, company }
        ).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

        if (isStorekeeperOnly)
        {
            var claimedByOther = await db.HandoverNotes.AsNoTracking()
                .AnyAsync(n => n.RentalContractId == contractId
                               && n.Status != HandoverNoteStatus.Completed
                               && n.Status != HandoverNoteStatus.Cancelled
                               && n.AssignedStorekeeperId != null
                               && n.AssignedStorekeeperId != currentUserId, cancellationToken);
            if (claimedByOther)
            {
                throw new AppException(
                    "HANDOVER_OWNED_BY_ANOTHER_STOREKEEPER",
                    "Hợp đồng này đang có phiếu bàn giao do Thủ kho khác tiếp nhận phụ trách.",
                    StatusCodes.Status403Forbidden);
            }
        }

        var progress = await GetProgressAsync([contractId], cancellationToken);
        var pendingDefReturns = await GetPendingDefectiveReturnsAsync([contractId], cancellationToken);
        var incidentStates = await GetIncidentReplacementStatesAsync([contractId], cancellationToken);
        var allocatedByModel = await AllocatedByEffectiveModelAsync(contractId, excludeNoteId: null, cancellationToken);

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

        var notesQuery = NoteQuery().Where(x => x.note.RentalContractId == contractId);
        if (isStorekeeperOnly)
        {
            notesQuery = notesQuery.Where(x =>
                x.note.AssignedStorekeeperId == null ||
                x.note.AssignedStorekeeperId == currentUserId ||
                x.note.Status == HandoverNoteStatus.Completed);
        }

        var notes = await notesQuery
            .OrderByDescending(x => x.note.CreatedAt)
            .ToListAsync(cancellationToken);

        return new HandoverContractDetailResponse(
            ToContractResponse(row.contract, row.company.CompanyCode, row.company.Name, progress, pendingDefReturns, incidentStates),
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

    public async Task<LookupSerialResponse> LookupSerialAsync(
        string serial,
        Guid contractId,
        CancellationToken cancellationToken = default)
    {
        var normalized = (serial ?? string.Empty).Trim();
        if (string.IsNullOrEmpty(normalized))
        {
            throw new AppException("SERIAL_REQUIRED", "Vui lòng nhập hoặc quét mã serial thiết bị.", StatusCodes.Status400BadRequest, "serial");
        }

        var row = await (
            from e in db.Equipments.AsNoTracking()
            join m in db.EquipmentModels.AsNoTracking() on e.EquipmentModelId equals m.Id
            join w in db.Warehouses.AsNoTracking() on e.WarehouseId equals w.Id
            where EF.Functions.ILike(e.SerialNumber, normalized)
            select new { e, m, WarehouseName = w.Name }
        ).FirstOrDefaultAsync(cancellationToken)
            ?? throw new AppException("SERIAL_NOT_FOUND", $"Không tìm thấy thiết bị có serial '{normalized}' trong hệ thống.", StatusCodes.Status404NotFound, "serial");

        var contractModelIds = await db.RentalContractItems
            .AsNoTracking()
            .Where(i => i.RentalContractId == contractId)
            .Select(i => i.EquipmentModelId)
            .ToListAsync(cancellationToken);

        var isContractModel = contractModelIds.Contains(row.e.EquipmentModelId);
        var isAvailable = row.e.Status == EquipmentStatus.Available && row.e.Condition != "DAMAGED";

        string? unavailableReason = null;
        if (row.e.Condition == "DAMAGED")
            unavailableReason = "Thiết bị này đang ở tình trạng HỎNG (DAMAGED), không thể xuất giao.";
        else if (row.e.Status != EquipmentStatus.Available)
            unavailableReason = $"Thiết bị đang ở trạng thái {EnumName.ToDatabaseValue(row.e.Status)}, không sẵn sàng để xuất kho.";

        return new LookupSerialResponse(
            row.e.Id,
            row.e.SerialNumber,
            row.e.EquipmentModelId,
            row.m.Code,
            row.m.Name,
            row.e.WarehouseId,
            row.WarehouseName,
            row.e.Condition,
            EnumName.ToDatabaseValue(row.e.Status),
            isAvailable,
            isContractModel,
            unavailableReason);
    }

    public async Task<List<HandoverNoteResponse>> GetNotesAsync(
        string? status,
        string? keyword,
        Guid currentUserId,
        bool isStorekeeperOnly,
        CancellationToken cancellationToken = default)
    {
        var query = NoteQuery();

        if (isStorekeeperOnly)
        {
            // Thủ kho chỉ thấy phiếu CHƯA AI TIẾP NHẬN (AssignedStorekeeperId == null) hoặc phiếu DO CHÍNH MÌNH PHỤ TRÁCH
            query = query.Where(x =>
                x.note.AssignedStorekeeperId == null ||
                x.note.AssignedStorekeeperId == currentUserId);
        }

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

    public async Task<HandoverNoteDetailResponse> GetNoteDetailAsync(
        Guid id,
        Guid? currentUserId = null,
        bool isStorekeeperOnly = false,
        CancellationToken cancellationToken = default)
    {
        var row = await NoteQuery().SingleOrDefaultAsync(x => x.note.Id == id, cancellationToken)
                  ?? throw NoteNotFound();

        if (isStorekeeperOnly && currentUserId.HasValue
            && row.note.AssignedStorekeeperId.HasValue
            && row.note.AssignedStorekeeperId.Value != currentUserId.Value)
        {
            throw new AppException(
                "HANDOVER_OWNED_BY_ANOTHER_STOREKEEPER",
                "Phiếu bàn giao này đã được Thủ kho khác tiếp nhận phụ trách.",
                StatusCodes.Status403Forbidden);
        }

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

        var subMap = await (
            from req in db.ReplacementRequests.AsNoTracking()
            join ri in db.ReplacementRequestItems.AsNoTracking() on req.Id equals ri.ReplacementRequestId
            join targetModel in db.EquipmentModels.AsNoTracking() on ri.ReplacementModelId equals targetModel.Id
            where req.HandoverId == id && req.Status != ReplacementRequestStatus.Cancelled
            select new { ri.OldEquipmentId, TargetModelId = targetModel.Id, TargetCode = targetModel.Code, TargetName = targetModel.Name }
        ).ToDictionaryAsync(x => x.OldEquipmentId, cancellationToken);

        return new HandoverNoteDetailResponse(
            (await ToNoteResponsesAsync([row], cancellationToken)).Single(),
            contract.DeliveryAddress,
            contract.DeliveryNote,
            items.Select(x =>
            {
                subMap.TryGetValue(x.equipment.Id, out var sub);
                return new HandoverItemResponse(
                    x.item.Id,
                    x.equipment.Id,
                    x.equipment.SerialNumber,
                    x.equipment.EquipmentModelId,
                    x.Code,
                    x.ModelName,
                    x.WarehouseName,
                    x.equipment.Condition,
                    EnumName.ToDatabaseValue(x.equipment.Status),
                    x.item.Note,
                    sub?.TargetModelId,
                    sub?.TargetCode,
                    sub?.TargetName);
            }).ToList());
    }

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
            result.Add(await GetNoteDetailAsync(id, cancellationToken: cancellationToken));
        return result;
    }

    /// <summary>Thủ kho tiếp nhận phụ trách soạn phiếu bàn giao chưa có người nhận (khóa không cho Thủ kho khác thấy/sửa).</summary>
    public async Task<HandoverNoteDetailResponse> ClaimAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var note = await db.HandoverNotes
            .FromSqlInterpolated($"SELECT * FROM handover_notes WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw NoteNotFound();

        EnsureStatus(note, HandoverNoteStatus.Preparing);

        if (note.AssignedStorekeeperId.HasValue && note.AssignedStorekeeperId.Value != currentUserId)
        {
            throw new AppException(
                "ALREADY_CLAIMED_BY_ANOTHER_STOREKEEPER",
                "Phiếu bàn giao này đã được Thủ kho khác tiếp nhận trước đó.",
                StatusCodes.Status409Conflict);
        }

        if (!note.AssignedStorekeeperId.HasValue)
        {
            note.AssignedStorekeeperId = currentUserId;
            note.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetNoteDetailAsync(id, currentUserId, isStorekeeperOnly: true, cancellationToken);
    }

    public async Task<HandoverNoteDetailResponse> CreateAsync(
        CreateHandoverRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var contract = await LockContractAsync(request.RentalContractId!.Value, cancellationToken);

        var isIncidentReplacementFlow = contract.Status == RentalContractStatus.Delivered || request.IncidentId.HasValue;

        if (!PreparableStatuses.Contains(contract.Status) && !isIncidentReplacementFlow)
        {
            throw new AppException(
                "CONTRACT_NOT_PREPARABLE",
                "Chỉ hợp đồng đang chuẩn bị, giao một phần hoặc có sự cố cần giao thay thế mới được soạn bàn giao.",
                StatusCodes.Status409Conflict);
        }

        var activeInitialNote = await db.HandoverNotes
            .AsNoTracking()
            .Where(n => n.RentalContractId == contract.Id
                        && n.HandoverType == "INITIAL"
                        && n.Status != HandoverNoteStatus.Cancelled)
            .OrderByDescending(n => n.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        var handoverType = "INITIAL";
        Guid? parentHandoverId = null;
        Guid? linkedIncidentId = null;
        var deliveryAttempt = 1;

        if (isIncidentReplacementFlow)
        {
            var incStates = await GetIncidentReplacementStatesAsync([contract.Id], cancellationToken);
            if (!incStates.TryGetValue(contract.Id, out var incState))
            {
                throw new AppException(
                    "NO_INCIDENT_FOR_REPLACEMENT",
                    "Hợp đồng này hiện không có sự cố RETURN_REQUIRED nào cần lập phiếu giao máy thay thế.",
                    StatusCodes.Status409Conflict);
            }

            if (!string.IsNullOrWhiteSpace(incState.BlockedReason) || !incState.ReadyIncidentId.HasValue)
            {
                throw new AppException(
                    "INCIDENT_REPLACEMENT_BLOCKED",
                    incState.BlockedReason ?? "Chưa đủ điều kiện lập phiếu giao máy thay thế cho sự cố này.",
                    StatusCodes.Status409Conflict);
            }

            var hasOpenIncRepl = await db.HandoverNotes.AsNoTracking()
                .AnyAsync(n => n.IncidentId == incState.ReadyIncidentId.Value
                               && n.HandoverType == "INCIDENT_REPLACEMENT"
                               && n.Status != HandoverNoteStatus.Completed
                               && n.Status != HandoverNoteStatus.Cancelled, cancellationToken);
            if (hasOpenIncRepl)
            {
                throw new AppException(
                    "INCIDENT_REPLACEMENT_ALREADY_OPEN",
                    $"Sự cố {incState.IncidentCode} đã có phiếu giao máy thay thế đang mở. Vui lòng sửa trực tiếp trên phiếu đó.",
                    StatusCodes.Status409Conflict);
            }

            var latestCompletedNote = await db.HandoverNotes.AsNoTracking()
                .Where(n => n.RentalContractId == contract.Id && n.Status == HandoverNoteStatus.Completed)
                .OrderByDescending(n => n.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            handoverType = "INCIDENT_REPLACEMENT";
            parentHandoverId = latestCompletedNote?.Id;
            linkedIncidentId = incState.ReadyIncidentId.Value;
            deliveryAttempt = 1;
        }
        else if (activeInitialNote != null)
        {
            // Chỉ cho phép mở phiếu giao bù (SUPPLEMENTARY) khi HĐ đang PARTIALLY_DELIVERED,
            // đã có ít nhất 1 phiếu hoàn tất có máy lỗi cần giao bù, và chưa có phiếu SUPPLEMENTARY nào đang mở.
            if (contract.Status != RentalContractStatus.PartiallyDelivered)
            {
                throw new AppException(
                    "SINGLE_HANDOVER_ONLY",
                    "Hợp đồng này đã có phiếu bàn giao gốc. Hệ thống chỉ cho phép tạo phiếu giao bù khi lần giao trước có máy lỗi cần mang về.",
                    StatusCodes.Status409Conflict);
            }

            var hasOpenSupplementary = await db.HandoverNotes
                .AsNoTracking()
                .AnyAsync(n => n.RentalContractId == contract.Id
                               && n.HandoverType == "SUPPLEMENTARY"
                               && n.Status != HandoverNoteStatus.Completed
                               && n.Status != HandoverNoteStatus.Cancelled, cancellationToken);

            if (hasOpenSupplementary)
            {
                throw new AppException(
                    "SUPPLEMENTARY_ALREADY_OPEN",
                    "Hợp đồng này đang có một phiếu giao bù chưa hoàn tất. Vui lòng sửa trực tiếp trên phiếu giao bù hiện tại.",
                    StatusCodes.Status409Conflict);
            }

            // QUY TẮC BẮT BUỘC: Phải hoàn tất thu hồi toàn bộ máy lỗi về kho (DEFECTIVE_ON_DELIVERY = COMPLETED) trước khi lập phiếu giao bù
            var pendingDefReturn = await db.EquipmentReturns.AsNoTracking()
                .Where(r => r.RentalContractId == contract.Id
                            && r.ReturnType == "DEFECTIVE_ON_DELIVERY"
                            && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (pendingDefReturn != null)
            {
                throw new AppException(
                    "DEFECTIVE_RETURN_NOT_COMPLETED",
                    $"Phải hoàn tất kiểm nhận phiếu thu hồi máy lỗi {pendingDefReturn.ReturnCode} về kho trước khi lập phiếu giao bù.",
                    StatusCodes.Status409Conflict);
            }

            var latestCompletedNote = await db.HandoverNotes
                .AsNoTracking()
                .Where(n => n.RentalContractId == contract.Id && n.Status == HandoverNoteStatus.Completed)
                .OrderByDescending(n => n.UpdatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (latestCompletedNote == null)
            {
                throw new AppException(
                    "NO_COMPLETED_HANDOVER",
                    "Chưa có phiếu bàn giao hoàn tất nào để lập phiếu giao bù.",
                    StatusCodes.Status409Conflict);
            }

            handoverType = "SUPPLEMENTARY";
            parentHandoverId = latestCompletedNote.Id;
            // Phiếu giao bù miễn phí vận chuyển & không tính lượt giao thất bại (giữ = 1 để thỏa CHECK delivery_attempt_number > 0)
            deliveryAttempt = 1;
        }

        var now = DateTimeOffset.UtcNow;

        var note = new HandoverNote
        {
            Id = Guid.CreateVersion7(),
            HandoverCode = await GenerateCodeAsync(cancellationToken),
            RentalContractId = contract.Id,
            HandoverType = handoverType,
            ParentHandoverId = parentHandoverId,
            IncidentId = linkedIncidentId,
            DeliveryAttemptNumber = deliveryAttempt,
            Status = HandoverNoteStatus.Preparing,
            AssignedStorekeeperId = currentUserId,
            CreatedBy = currentUserId,
            CreatedAt = now,
            UpdatedAt = now,
        };

        ApplyInfo(note, request, contract);
        db.HandoverNotes.Add(note);

        await ReserveItemsAsync(note, contract, request.Items, currentItems: [], currentUserId, cancellationToken);

        if (contract.Status == RentalContractStatus.Signed)
        {
            ChangeContractStatus(contract, RentalContractStatus.Preparing, currentUserId,
                $"Kho bắt đầu soạn bàn giao {note.HandoverCode}");
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetNoteDetailAsync(note.Id, cancellationToken: cancellationToken);
    }

    public async Task<HandoverNoteDetailResponse> UpdateAsync(
        Guid id,
        SaveHandoverRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var note = await db.HandoverNotes
            .FromSqlInterpolated($"SELECT * FROM handover_notes WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
                   ?? throw NoteNotFound();
        var contract = await LockContractAsync(note.RentalContractId, cancellationToken);

        EnsureStatus(note, HandoverNoteStatus.Preparing);
        EnsureOwnedOrAutoClaim(note, currentUserId);

        var currentItems = await db.HandoverItems
            .Where(i => i.HandoverNoteId == id)
            .ToListAsync(cancellationToken);

        ApplyInfo(note, request, contract);
        note.UpdatedAt = DateTimeOffset.UtcNow;

        await ReserveItemsAsync(note, contract, request.Items, currentItems, currentUserId, cancellationToken);

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetNoteDetailAsync(id, cancellationToken: cancellationToken);
    }

    public async Task<HandoverNoteDetailResponse> SubmitAsync(Guid id, Guid currentUserId, CancellationToken cancellationToken = default)
    {
        var note = await db.HandoverNotes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken)
                   ?? throw NoteNotFound();

        EnsureStatus(note, HandoverNoteStatus.Preparing);
        EnsureOwnedOrAutoClaim(note, currentUserId);

        if (!await db.HandoverItems.AnyAsync(i => i.HandoverNoteId == id, cancellationToken))
        {
            throw new AppException("HANDOVER_EMPTY", "Phiếu bàn giao chưa có máy nào.", StatusCodes.Status409Conflict);
        }

        note.Status = HandoverNoteStatus.PendingApproval;
        note.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);

        var contractInfo = await db.RentalContracts.AsNoTracking()
            .Where(c => c.Id == note.RentalContractId)
            .Select(c => new { c.ContractCode, c.ApprovedBy })
            .FirstOrDefaultAsync(cancellationToken);

        var mgrIds = contractInfo?.ApprovedBy.HasValue == true
            ? [contractInfo.ApprovedBy.Value]
            : await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "MANAGER"
                select ur.UserId
            ).Distinct().ToListAsync(cancellationToken);

        if (mgrIds.Count > 0)
        {
            await notificationDispatcher.NotifyUsersAsync(
                mgrIds,
                $"Phiếu bàn giao chờ duyệt xuất kho ({note.HandoverCode})",
                $"Thủ kho đã soạn xong phiếu bàn giao {note.HandoverCode} (hợp đồng {contractInfo?.ContractCode ?? ""}) và gửi chờ Quản lý duyệt xuất kho.",
                "HANDOVER_PENDING_APPROVAL",
                cancellationToken: cancellationToken);
        }

        return await GetNoteDetailAsync(id, cancellationToken: cancellationToken);
    }

    /// <summary>
    /// Hoàn tất bàn giao tại hiện trường (KTV / Manager):
    /// - Các máy đạt chuyển sang RENTED.
    /// - Nếu có máy lỗi lúc giao/lắp: ghi HandoverIssue + HandoverIssueItems, tự tạo phiếu thu hồi
    ///   (EquipmentReturn: status=PROCESSING, return_type=DEFECTIVE_ON_DELIVERY) và chuyển HĐ sang PARTIALLY_DELIVERED
    ///   để Kho được phép mở phiếu giao bù (SUPPLEMENTARY).
    /// - Nếu tổng máy đã giao OK bằng đủ số lượng HĐ → chuyển HĐ sang DELIVERED.
    /// </summary>
    public async Task<HandoverNoteDetailResponse> CompleteDeliveryAsync(
        Guid id,
        CompleteHandoverDeliveryRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var note = await db.HandoverNotes.SingleOrDefaultAsync(n => n.Id == id, cancellationToken)
            ?? throw NoteNotFound();

        EnsureStatus(note, HandoverNoteStatus.Shipping);

        var contract = await LockContractAsync(note.RentalContractId, cancellationToken);
        var handoverItems = await db.HandoverItems
            .Where(i => i.HandoverNoteId == note.Id)
            .ToListAsync(cancellationToken);

        var handoverEquipmentIds = handoverItems.Select(i => i.EquipmentId).ToHashSet();
        var defectiveByEqId = new Dictionary<Guid, DefectiveDeliveryItemRequest>();

        foreach (var d in request.DefectiveItems)
        {
            var eqId = d.EquipmentId!.Value;
            if (!handoverEquipmentIds.Contains(eqId))
            {
                throw new AppException(
                    "EQUIPMENT_NOT_IN_HANDOVER",
                    "Có thiết bị báo lỗi không thuộc phiếu bàn giao này.",
                    StatusCodes.Status400BadRequest,
                    "defectiveItems");
            }

            defectiveByEqId[eqId] = d;
        }

        var now = DateTimeOffset.UtcNow;
        var equipments = await db.Equipments
            .Where(e => handoverEquipmentIds.Contains(e.Id))
            .ToListAsync(cancellationToken);

        foreach (var eq in equipments)
        {
            if (defectiveByEqId.ContainsKey(eq.Id))
            {
                // Máy lỗi mang về kho chờ Thủ kho kiểm nhận phiếu thu hồi → vẫn giữ trạng thái chờ thu hồi/bảo dưỡng
                eq.Condition = "DAMAGED";
                eq.UpdatedAt = now;
            }
            else
            {
                eq.Status = EquipmentStatus.Rented;
                eq.UpdatedAt = now;
            }
        }

        if (defectiveByEqId.Count > 0)
        {
            var issue = new HandoverIssue
            {
                Id = Guid.CreateVersion7(),
                HandoverNoteId = note.Id,
                Description = string.IsNullOrWhiteSpace(request.Note)
                    ? $"Phát hiện {defectiveByEqId.Count} thiết bị lỗi khi giao/lắp đặt ({note.HandoverCode})."
                    : request.Note.Trim(),
                Status = HandoverIssueStatus.Open,
                CreatedBy = currentUserId,
                CreatedAt = now,
            };
            db.HandoverIssues.Add(issue);

            foreach (var (eqId, d) in defectiveByEqId)
            {
                db.HandoverIssueItems.Add(new HandoverIssueItem
                {
                    Id = Guid.CreateVersion7(),
                    HandoverIssueId = issue.Id,
                    EquipmentId = eqId,
                    IssueType = NormalizeIssueType(d.IssueType),
                    Description = string.IsNullOrWhiteSpace(d.Description) ? null : d.Description.Trim(),
                    Status = HandoverIssueItemStatus.Open,
                });
            }

            // Tự động sinh phiếu thu hồi máy lỗi (PROCESSING, DEFECTIVE_ON_DELIVERY) — không cần Manager duyệt
            var returnPrefix = $"TH-{EquipmentAvailabilityService.BusinessToday().Year}-";
            var existingReturnCodes = await db.EquipmentReturns.AsNoTracking()
                .Where(r => r.ReturnCode.StartsWith(returnPrefix))
                .Select(r => r.ReturnCode)
                .ToListAsync(cancellationToken);
            var maxRet = existingReturnCodes
                .Select(c => int.TryParse(c[returnPrefix.Length..], out var n) ? n : 0)
                .DefaultIfEmpty(0)
                .Max();

            var retReceipt = new EquipmentReturn
            {
                Id = Guid.CreateVersion7(),
                ReturnCode = $"{returnPrefix}{maxRet + 1:D4}",
                RentalContractId = contract.Id,
                ReturnedAt = now,
                ReceivedBy = currentUserId,
                Status = EquipmentReturnStatus.Processing,
                ReturnType = "DEFECTIVE_ON_DELIVERY",
                Note = $"Thu hồi tự động {defectiveByEqId.Count} máy lỗi khi giao phiếu {note.HandoverCode}. Chờ Thủ kho kiểm nhận & chuyển bảo dưỡng.",
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.EquipmentReturns.Add(retReceipt);

            foreach (var (eqId, d) in defectiveByEqId)
            {
                db.EquipmentReturnItems.Add(new EquipmentReturnItem
                {
                    Id = Guid.CreateVersion7(),
                    EquipmentReturnId = retReceipt.Id,
                    EquipmentId = eqId,
                    Status = EquipmentReturnItemStatus.Returned,
                    Note = string.IsNullOrWhiteSpace(d.Description) ? "Lỗi khi giao/lắp đặt" : d.Description.Trim(),
                });
            }
        }

        note.Status = HandoverNoteStatus.Completed;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            note.Note = string.IsNullOrWhiteSpace(note.Note)
                ? request.Note.Trim()
                : $"{note.Note} [Giao hàng]: {request.Note.Trim()}";
        }
        note.UpdatedAt = now;

        await db.SaveChangesAsync(cancellationToken);

        // Kiểm tra tổng số máy đã giao OK cho hợp đồng để chốt PARTIALLY_DELIVERED hoặc DELIVERED
        var progressMap = await GetProgressAsync([contract.Id], cancellationToken);
        var progress = progressMap[contract.Id];

        if (defectiveByEqId.Count > 0 || progress.Allocated < progress.Total)
        {
            if (contract.Status != RentalContractStatus.PartiallyDelivered)
            {
                ChangeContractStatus(contract, RentalContractStatus.PartiallyDelivered, currentUserId,
                    $"Hoàn tất phiếu {note.HandoverCode} ({progress.Allocated}/{progress.Total} máy đạt, cần giao bù {Math.Max(0, progress.Total - progress.Allocated)} máy).");
            }
        }
        else if (contract.Status != RentalContractStatus.Delivered)
        {
            ChangeContractStatus(contract, RentalContractStatus.Delivered, currentUserId,
                $"Đã bàn giao đủ {progress.Total} thiết bị qua phiếu {note.HandoverCode}.");
        }

        // Nếu toàn bộ các điểm dừng trong chuyến ServiceAssignment đã COMPLETED thì đóng chuyến
        if (note.ServiceAssignmentId.HasValue)
        {
            var hasRemainingStops = await db.HandoverNotes.AsNoTracking()
                .AnyAsync(n => n.ServiceAssignmentId == note.ServiceAssignmentId.Value
                               && n.Id != note.Id
                               && n.Status != HandoverNoteStatus.Completed
                               && n.Status != HandoverNoteStatus.Cancelled, cancellationToken);
            if (!hasRemainingStops)
            {
                var assignment = await db.ServiceAssignments
                    .FirstOrDefaultAsync(a => a.Id == note.ServiceAssignmentId.Value, cancellationToken);
                if (assignment != null && assignment.Status != ServiceAssignmentStatus.Completed)
                {
                    assignment.Status = ServiceAssignmentStatus.Completed;
                    assignment.CompletedAt = now;
                    assignment.UpdatedAt = now;
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetNoteDetailAsync(note.Id, cancellationToken: cancellationToken);
    }

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
        EnsureOwnedOrAutoClaim(note, currentUserId);

        var equipmentIds = await db.HandoverItems
            .Where(i => i.HandoverNoteId == id)
            .Select(i => i.EquipmentId)
            .ToListAsync(cancellationToken);

        await ReleaseAsync(equipmentIds, cancellationToken);

        var pendingSubs = await db.ReplacementRequests
            .Where(r => r.HandoverId == id && r.Status == ReplacementRequestStatus.Pending)
            .ToListAsync(cancellationToken);
        foreach (var sub in pendingSubs)
        {
            sub.Status = ReplacementRequestStatus.Cancelled;
            sub.UpdatedAt = DateTimeOffset.UtcNow;
        }

        var reason = request.Reason.Trim();
        note.Status = HandoverNoteStatus.Cancelled;
        note.Note = string.IsNullOrWhiteSpace(note.Note) ? $"Hủy: {reason}" : $"{note.Note}\nHủy: {reason}";
        note.UpdatedAt = DateTimeOffset.UtcNow;

        // Hợp đồng đã duyệt bản ký & đủ cọc nên giữ nguyên trạng thái PREPARING để Kho lập lại phiếu mới
        // (không trả ngược về SIGNED để tránh xung đột bước duyệt bản ký & đặt cọc).
        if (contract.Status == RentalContractStatus.Preparing)
        {
            db.ContractStatusHistory.Add(new ContractStatusHistory
            {
                Id = Guid.CreateVersion7(),
                RentalContractId = contract.Id,
                PreviousStatus = RentalContractStatus.Preparing,
                Status = RentalContractStatus.Preparing,
                ChangedBy = currentUserId,
                ChangedAt = DateTimeOffset.UtcNow,
                Note = $"Hủy phiếu bàn giao {note.HandoverCode}: {reason}",
            });
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        return await GetNoteDetailAsync(id, cancellationToken: cancellationToken);
    }

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
        var userIds = rows.Select(r => r.note.CreatedBy)
            .Concat(rows.Where(r => r.note.AssignedStorekeeperId.HasValue).Select(r => r.note.AssignedStorekeeperId!.Value))
            .Distinct()
            .ToList();

        var counts = await db.HandoverItems.AsNoTracking()
            .Where(i => ids.Contains(i.HandoverNoteId))
            .GroupBy(i => i.HandoverNoteId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

        var users = await db.Users.AsNoTracking()
            .Where(u => userIds.Contains(u.Id))
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
            users.GetValueOrDefault(r.note.CreatedBy),
            r.note.CreatedAt,
            r.note.UpdatedAt,
            r.note.HandoverType,
            r.note.ParentHandoverId,
            r.note.AssignedStorekeeperId,
            r.note.AssignedStorekeeperId.HasValue ? users.GetValueOrDefault(r.note.AssignedStorekeeperId.Value) : null,
            r.note.IncidentId)).ToList();
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

        var defectivePairs = await (
            from issue in db.HandoverIssues.AsNoTracking()
            join issueItem in db.HandoverIssueItems.AsNoTracking() on issue.Id equals issueItem.HandoverIssueId
            join note in db.HandoverNotes.AsNoTracking() on issue.HandoverNoteId equals note.Id
            where contractIds.Contains(note.RentalContractId)
                  && issue.Status != HandoverIssueStatus.Cancelled
                  && issueItem.Status != HandoverIssueItemStatus.Cancelled
            select new { note.Id, issueItem.EquipmentId }
        ).ToListAsync(cancellationToken);

        var defectiveSet = defectivePairs.Select(x => (x.Id, x.EquipmentId)).ToHashSet();

        var incidentReturned = await (
            from ret in db.EquipmentReturns.AsNoTracking()
            join ri in db.EquipmentReturnItems.AsNoTracking() on ret.Id equals ri.EquipmentReturnId
            where contractIds.Contains(ret.RentalContractId)
                  && ret.ReturnType == "INCIDENT_RETURN"
                  && ret.Status == EquipmentReturnStatus.Completed
            select new { ret.RentalContractId, ri.EquipmentId, ret.CreatedAt }
        ).ToListAsync(cancellationToken);

        var rawAlloc = await (
            from note in db.HandoverNotes.AsNoTracking()
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            where contractIds.Contains(note.RentalContractId) && note.Status != HandoverNoteStatus.Cancelled
            select new { note.RentalContractId, NoteId = note.Id, note.CreatedAt, item.EquipmentId }
        ).ToListAsync(cancellationToken);

        var allocated = rawAlloc
            .Where(x => !defectiveSet.Contains((x.NoteId, x.EquipmentId))
                        && !incidentReturned.Any(r => r.RentalContractId == x.RentalContractId
                                                      && r.EquipmentId == x.EquipmentId
                                                      && x.CreatedAt <= r.CreatedAt))
            .GroupBy(x => x.RentalContractId)
            .ToDictionary(g => g.Key, g => g.Count());

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

    private async Task<Dictionary<Guid, int>> AllocatedByEffectiveModelAsync(
        Guid contractId,
        Guid? excludeNoteId,
        CancellationToken cancellationToken)
    {
        var defectivePairs = await (
            from issue in db.HandoverIssues.AsNoTracking()
            join issueItem in db.HandoverIssueItems.AsNoTracking() on issue.Id equals issueItem.HandoverIssueId
            join note in db.HandoverNotes.AsNoTracking() on issue.HandoverNoteId equals note.Id
            where note.RentalContractId == contractId
                  && issue.Status != HandoverIssueStatus.Cancelled
                  && issueItem.Status != HandoverIssueItemStatus.Cancelled
            select new { note.Id, issueItem.EquipmentId }
        ).ToListAsync(cancellationToken);

        var defectiveSet = defectivePairs.Select(x => (x.Id, x.EquipmentId)).ToHashSet();

        var incidentReturned = await (
            from ret in db.EquipmentReturns.AsNoTracking()
            join ri in db.EquipmentReturnItems.AsNoTracking() on ret.Id equals ri.EquipmentReturnId
            where ret.RentalContractId == contractId
                  && ret.ReturnType == "INCIDENT_RETURN"
                  && ret.Status == EquipmentReturnStatus.Completed
            select new { ri.EquipmentId, ret.CreatedAt }
        ).ToListAsync(cancellationToken);

        var rawItemsAll = await (
            from note in db.HandoverNotes.AsNoTracking()
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            where note.RentalContractId == contractId &&
                  note.Status != HandoverNoteStatus.Cancelled &&
                  (excludeNoteId == null || note.Id != excludeNoteId)
            select new { NoteId = note.Id, note.CreatedAt, item.EquipmentId, equipment.EquipmentModelId }
        ).ToListAsync(cancellationToken);

        var rawItems = rawItemsAll
            .Where(x => !defectiveSet.Contains((x.NoteId, x.EquipmentId))
                        && !incidentReturned.Any(r => r.EquipmentId == x.EquipmentId && x.CreatedAt <= r.CreatedAt))
            .ToList();

        var subTargets = await (
            from req in db.ReplacementRequests.AsNoTracking()
            join ri in db.ReplacementRequestItems.AsNoTracking() on req.Id equals ri.ReplacementRequestId
            where req.RentalContractId == contractId
                  && req.Status != ReplacementRequestStatus.Cancelled
                  && req.Status != ReplacementRequestStatus.Rejected
                  && ri.ReplacementModelId != null
            select new { ri.OldEquipmentId, TargetModelId = ri.ReplacementModelId!.Value }
        ).ToDictionaryAsync(x => x.OldEquipmentId, x => x.TargetModelId, cancellationToken);

        return rawItems
            .GroupBy(x => subTargets.TryGetValue(x.EquipmentId, out var target) ? target : x.EquipmentModelId)
            .ToDictionary(g => g.Key, g => g.Count());
    }

    private sealed record PendingDefectiveReturnInfo(int Count, string? LatestReturnCode);

    private async Task<Dictionary<Guid, PendingDefectiveReturnInfo>> GetPendingDefectiveReturnsAsync(
        List<Guid> contractIds,
        CancellationToken cancellationToken)
    {
        if (contractIds.Count == 0) return [];

        var rows = await db.EquipmentReturns.AsNoTracking()
            .Where(r => contractIds.Contains(r.RentalContractId)
                        && r.ReturnType == "DEFECTIVE_ON_DELIVERY"
                        && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new { r.RentalContractId, r.ReturnCode })
            .ToListAsync(cancellationToken);

        return rows
            .GroupBy(x => x.RentalContractId)
            .ToDictionary(g => g.Key, g => new PendingDefectiveReturnInfo(g.Count(), g.First().ReturnCode));
    }

    private static void EnsureOwnedOrAutoClaim(HandoverNote note, Guid currentUserId)
    {
        if (note.AssignedStorekeeperId.HasValue && note.AssignedStorekeeperId.Value != currentUserId)
        {
            throw new AppException(
                "HANDOVER_OWNED_BY_ANOTHER_STOREKEEPER",
                "Phiếu bàn giao này đã được Thủ kho khác tiếp nhận phụ trách, bạn không thể thao tác.",
                StatusCodes.Status403Forbidden);
        }

        note.AssignedStorekeeperId ??= currentUserId;
    }

    private sealed record IncidentReplacementState(
        Guid? ReadyIncidentId,
        string IncidentCode,
        string? BlockedReason);

    private async Task<Dictionary<Guid, IncidentReplacementState>> GetIncidentReplacementStatesAsync(
        List<Guid>? filterContractIds,
        CancellationToken cancellationToken)
    {
        var openIncidents = await db.EquipmentIncidents.AsNoTracking()
            .Where(i => i.Status == EquipmentIncidentStatus.ReturnRequired)
            .OrderByDescending(i => i.ReportedAt)
            .ToListAsync(cancellationToken);

        if (openIncidents.Count == 0) return [];

        var incIds = openIncidents.Select(i => i.Id).ToList();
        var incItems = await db.EquipmentIncidentItems.AsNoTracking()
            .Where(ii => incIds.Contains(ii.IncidentId))
            .ToListAsync(cancellationToken);

        var eqIds = incItems.Select(ii => ii.EquipmentId).Distinct().ToList();
        if (eqIds.Count == 0) return [];

        var eqContracts = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            where eqIds.Contains(hi.EquipmentId)
            orderby hn.CreatedAt descending
            select new { hi.EquipmentId, hn.RentalContractId }
        ).ToListAsync(cancellationToken);
        var contractByEq = eqContracts.GroupBy(x => x.EquipmentId).ToDictionary(g => g.Key, g => g.First().RentalContractId);

        var itemIds = incItems.Select(ii => ii.Id).ToList();
        var chargesByItem = (await db.IncidentCharges.AsNoTracking()
            .Where(ic => itemIds.Contains(ic.IncidentItemId) && ic.Status != IncidentChargeStatus.Cancelled)
            .ToListAsync(cancellationToken))
            .GroupBy(ic => ic.IncidentItemId)
            .ToDictionary(g => g.Key, g => g.ToList());

        var targetContractIds = contractByEq.Values.Distinct().ToList();
        if (filterContractIds is not null)
            targetContractIds = targetContractIds.Where(id => filterContractIds.Contains(id)).ToList();
        if (targetContractIds.Count == 0) return [];

        var pendingIncReturns = (await db.EquipmentReturns.AsNoTracking()
            .Where(r => targetContractIds.Contains(r.RentalContractId)
                        && r.ReturnType == "INCIDENT_RETURN"
                        && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft))
            .Select(r => new { r.RentalContractId, r.ReturnCode })
            .ToListAsync(cancellationToken))
            .GroupBy(x => x.RentalContractId)
            .ToDictionary(g => g.Key, g => g.First().ReturnCode);

        var result = new Dictionary<Guid, IncidentReplacementState>();

        foreach (var inc in openIncidents)
        {
            var itemsOfInc = incItems.Where(x => x.IncidentId == inc.Id).ToList();
            Guid? contractId = null;
            foreach (var it in itemsOfInc)
            {
                if (contractByEq.TryGetValue(it.EquipmentId, out var cid) && targetContractIds.Contains(cid))
                {
                    contractId = cid;
                    break;
                }
            }
            if (!contractId.HasValue || result.ContainsKey(contractId.Value)) continue;

            if (pendingIncReturns.TryGetValue(contractId.Value, out var pendingRetCode))
            {
                result[contractId.Value] = new IncidentReplacementState(
                    null,
                    inc.IncidentCode,
                    $"Chờ Kho kiểm nhận xong phiếu thu hồi sự cố {pendingRetCode} trước khi giao máy thay thế.");
                continue;
            }

            if (itemsOfInc.Any(it => string.IsNullOrWhiteSpace(it.FaultType)))
            {
                result[contractId.Value] = new IncidentReplacementState(
                    null,
                    inc.IncidentCode,
                    $"Chờ KTV kiểm tra bảo trì & phân định nguyên nhân lỗi cho sự cố {inc.IncidentCode}.");
                continue;
            }

            var charges = itemsOfInc
                .SelectMany(it => chargesByItem.TryGetValue(it.Id, out var list) ? list : [])
                .ToList();

            if (charges.Any(c => c.Status == IncidentChargeStatus.PendingApproval))
            {
                result[contractId.Value] = new IncidentReplacementState(
                    null,
                    inc.IncidentCode,
                    $"Chờ Quản lý duyệt khoản bồi thường sự cố {inc.IncidentCode}.");
                continue;
            }

            if (charges.Any(c => c.Status == IncidentChargeStatus.Pending))
            {
                result[contractId.Value] = new IncidentReplacementState(
                    null,
                    inc.IncidentCode,
                    $"Tạm khóa giao máy thay thế: khách hàng chưa thanh toán đủ khoản bồi thường sự cố {inc.IncidentCode}.");
                continue;
            }

            result[contractId.Value] = new IncidentReplacementState(inc.Id, inc.IncidentCode, null);
        }

        return result;
    }

    private static HandoverContractResponse ToContractResponse(
        RentalContract contract,
        string companyCode,
        string companyName,
        Dictionary<Guid, Progress> progress,
        Dictionary<Guid, PendingDefectiveReturnInfo> pendingDefReturns,
        Dictionary<Guid, IncidentReplacementState> incidentStates)
    {
        var p = progress.GetValueOrDefault(contract.Id) ?? new Progress(0, 0, 0);
        pendingDefReturns.TryGetValue(contract.Id, out var pendingDef);
        incidentStates.TryGetValue(contract.Id, out var incState);

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
            p.ActiveNotes,
            pendingDef?.Count ?? 0,
            pendingDef?.LatestReturnCode,
            incState?.ReadyIncidentId,
            incState?.IncidentCode,
            incState?.BlockedReason);
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

    private async Task ReserveItemsAsync(
        HandoverNote note,
        RentalContract contract,
        List<HandoverItemRequest> requested,
        List<HandoverItem> currentItems,
        Guid currentUserId,
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

        var contractQuantities = await db.RentalContractItems.AsNoTracking()
            .Where(i => i.RentalContractId == contract.Id)
            .ToDictionaryAsync(i => i.EquipmentModelId, i => i.Quantity, cancellationToken);

        var allocatedElsewhere = await AllocatedByEffectiveModelAsync(contract.Id, note.Id, cancellationToken);
        var substitutions = new List<(Equipment.Entities.Equipment Equip, Guid TargetContractModelId, string? Note)>();

        foreach (var group in requested.GroupBy(r =>
                 {
                     var eq = equipments[r.EquipmentId!.Value];
                     return r.ReplacedForModelId ?? eq.EquipmentModelId;
                 }))
        {
            if (!contractQuantities.TryGetValue(group.Key, out var quantity))
            {
                var sampleEq = equipments[group.First().EquipmentId!.Value];
                throw new AppException(
                    "EQUIPMENT_MODEL_NOT_IN_CONTRACT",
                    $"Máy {sampleEq.SerialNumber} không thuộc model nào trong hợp đồng (vui lòng chọn model cần thay thế nếu muốn đề xuất thay thế).",
                    StatusCodes.Status400BadRequest,
                    "items");
            }

            var remaining = quantity - allocatedElsewhere.GetValueOrDefault(group.Key);
            if (group.Count() > remaining)
            {
                throw new AppException(
                    "HANDOVER_QUANTITY_EXCEEDED",
                    $"Model này chỉ cần tối đa {Math.Max(remaining, 0)} máy theo hợp đồng.",
                    StatusCodes.Status400BadRequest,
                    "items");
            }

            foreach (var r in group)
            {
                var eq = equipments[r.EquipmentId!.Value];
                if (eq.EquipmentModelId != group.Key)
                {
                    substitutions.Add((eq, group.Key, r.Note));
                }
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

        var oldPendingSubs = await db.ReplacementRequests
            .Where(r => r.HandoverId == note.Id && r.Status == ReplacementRequestStatus.Pending)
            .ToListAsync(cancellationToken);

        if (oldPendingSubs.Count > 0)
        {
            var oldReqIds = oldPendingSubs.Select(r => r.Id).ToList();
            var oldReqItems = await db.ReplacementRequestItems
                .Where(ri => oldReqIds.Contains(ri.ReplacementRequestId))
                .ToListAsync(cancellationToken);
            db.ReplacementRequestItems.RemoveRange(oldReqItems);
            db.ReplacementRequests.RemoveRange(oldPendingSubs);
        }

        if (substitutions.Count > 0)
        {
            var now = DateTimeOffset.UtcNow;
            var allModelIds = substitutions
                .SelectMany(s => new[] { s.Equip.EquipmentModelId, s.TargetContractModelId })
                .Distinct()
                .ToList();
            var modelNames = await db.EquipmentModels
                .AsNoTracking()
                .Where(m => allModelIds.Contains(m.Id))
                .ToDictionaryAsync(m => m.Id, m => $"{m.Code} ({m.Name})", cancellationToken);

            var repReq = new ReplacementRequest
            {
                Id = Guid.CreateVersion7(),
                RequestCode = $"TT-{EquipmentAvailabilityService.BusinessToday():yyyyMMdd}-{Guid.NewGuid().ToString("N")[..4].ToUpperInvariant()}",
                RentalContractId = contract.Id,
                HandoverId = note.Id,
                Reason = $"Kho đề xuất thay thế {substitutions.Count} thiết bị model tương đương/cao hơn do hết thiết bị gốc (giữ nguyên đơn giá hợp đồng).",
                Status = ReplacementRequestStatus.Pending,
                RequestedBy = currentUserId,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.ReplacementRequests.Add(repReq);

            foreach (var sub in substitutions)
            {
                var actualLabel = modelNames.GetValueOrDefault(sub.Equip.EquipmentModelId, "");
                var targetLabel = modelNames.GetValueOrDefault(sub.TargetContractModelId, "");
                db.ReplacementRequestItems.Add(new ReplacementRequestItem
                {
                    Id = Guid.CreateVersion7(),
                    ReplacementRequestId = repReq.Id,
                    OldEquipmentId = sub.Equip.Id,
                    ReplacementModelId = sub.TargetContractModelId,
                    Note = $"Xuất máy {sub.Equip.SerialNumber} [{actualLabel}] thay thế cho [{targetLabel}] (giữ nguyên đơn giá gốc). {sub.Note}".Trim(),
                    CreatedAt = now,
                });
            }
        }
    }

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

    private static string NormalizeIssueType(string? raw) => (raw ?? "").Trim().ToUpperInvariant() switch
    {
        "DAMAGED" => "DAMAGED",
        "MALFUNCTION" => "MALFUNCTION",
        "OTHER" => "OTHER",
        _ => "DAMAGED",
    };

    private static AppException NoteNotFound() =>
        new("HANDOVER_NOT_FOUND", "Không tìm thấy phiếu bàn giao.", StatusCodes.Status404NotFound);
}
