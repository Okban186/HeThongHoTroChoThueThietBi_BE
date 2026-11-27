using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Equipment.Services;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
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

public sealed class EquipmentReturnService(AppDbContext db, INotificationDispatcher notificationDispatcher)
{
    private const string CodePrefix = "TH";
    private const decimal LateReturnDailyMultiplier = 1.5m; // 150% đơn giá ngày cho mỗi ngày trả trễ
    private static readonly string[] ReturnConditions = ["GOOD", "FAIR", "DAMAGED"];

    private static readonly RentalContractStatus[] ReturnableStatuses =
    [
        RentalContractStatus.PartiallyDelivered,
        RentalContractStatus.Delivered,
        RentalContractStatus.Expired,
        RentalContractStatus.CancellationRequested,
    ];

    public async Task<List<ReturnContractResponse>> GetContractsAsync(
        string? keyword,
        CancellationToken cancellationToken = default)
    {
        var query =
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where ReturnableStatuses.Contains(contract.Status)
            select new { contract, company };

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.contract.ContractCode, pattern) ||
                EF.Functions.ILike(x.company.Name, pattern) ||
                EF.Functions.ILike(x.company.CompanyCode, pattern));
        }

        var rows = await query.OrderBy(x => x.contract.EndDate).ToListAsync(cancellationToken);
        var ids = rows.Select(x => x.contract.Id).ToList();
        var delivered = await DeliveredCountsAsync(ids, cancellationToken);
        var outstanding = (await OutstandingQuery(ids).ToListAsync(cancellationToken))
            .GroupBy(x => x.ContractId)
            .ToDictionary(g => g.Key, g => g.Count());

        return rows
            .Where(x => outstanding.GetValueOrDefault(x.contract.Id) > 0)
            .Select(x => ToContractResponse(x.contract, x.company.CompanyCode, x.company.Name,
                delivered.GetValueOrDefault(x.contract.Id), outstanding.GetValueOrDefault(x.contract.Id)))
            .ToList();
    }

    public async Task<ReturnContractDetailResponse> GetContractDetailAsync(
        Guid contractId,
        Guid? currentUserId = null,
        bool isStorekeeperOnly = false,
        CancellationToken cancellationToken = default)
    {
        var row = await (
            from contract in db.RentalContracts.AsNoTracking()
            join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
            where contract.Id == contractId
            select new { contract, company }
        ).SingleOrDefaultAsync(cancellationToken)
            ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

        var outstanding = await OutstandingQuery([contractId])
            .OrderBy(x => x.ModelName)
            .ThenBy(x => x.SerialNumber)
            .ToListAsync(cancellationToken);
        var delivered = await DeliveredCountsAsync([contractId], cancellationToken);

        var retQ = ReturnQuery().Where(x => x.ret.RentalContractId == contractId);
        if (isStorekeeperOnly && currentUserId.HasValue)
        {
            retQ = retQ.Where(x => x.ret.AssignedStorekeeperId == null || x.ret.AssignedStorekeeperId == currentUserId.Value);
        }

        var returns = await retQ
            .OrderByDescending(x => x.ret.CreatedAt)
            .ToListAsync(cancellationToken);

        return new ReturnContractDetailResponse(
            ToContractResponse(row.contract, row.company.CompanyCode, row.company.Name,
                delivered.GetValueOrDefault(contractId), outstanding.Count),
            outstanding.Select(x => new OutstandingEquipmentResponse(
                x.EquipmentId, x.SerialNumber, x.EquipmentModelId, x.ModelCode, x.ModelName,
                x.Condition, x.HandoverCode)).ToList(),
            await ToResponsesAsync(returns, cancellationToken));
    }

    public async Task<List<EquipmentReturnResponse>> GetReturnsAsync(
        string? keyword,
        Guid? currentUserId = null,
        bool isStorekeeperOnly = false,
        CancellationToken cancellationToken = default)
    {
        var query = ReturnQuery();

        if (isStorekeeperOnly && currentUserId.HasValue)
        {
            // Thủ kho chỉ thấy phiếu thu hồi CHƯA AI TIẾP NHẬN hoặc DO CHÍNH MÌNH PHỤ TRÁCH,
            // và ẩn các phiếu DRAFT đang chờ đội KTV đi thu hồi tại hiện trường (chỉ hiện khi KTV đã mang về -> PROCESSING).
            query = query.Where(x =>
                !(x.ret.Status == EquipmentReturnStatus.Draft && x.ret.ServiceAssignmentId != null) &&
                (x.ret.AssignedStorekeeperId == null ||
                 x.ret.AssignedStorekeeperId == currentUserId.Value));
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var pattern = $"%{keyword.Trim()}%";
            query = query.Where(x =>
                EF.Functions.ILike(x.ret.ReturnCode, pattern) ||
                EF.Functions.ILike(x.ContractCode, pattern) ||
                EF.Functions.ILike(x.CompanyName, pattern));
        }

        var rows = await query
            .OrderByDescending(x => x.ret.Status == EquipmentReturnStatus.Processing)
            .ThenByDescending(x => x.ret.CreatedAt)
            .ToListAsync(cancellationToken);

        return await ToResponsesAsync(rows, cancellationToken);
    }

    public async Task<EquipmentReturnDetailResponse> GetReturnDetailAsync(
        Guid id,
        Guid? currentUserId = null,
        bool isStorekeeperOnly = false,
        CancellationToken cancellationToken = default)
    {
        var row = await ReturnQuery().SingleOrDefaultAsync(x => x.ret.Id == id, cancellationToken)
                  ?? throw new AppException("RETURN_NOT_FOUND", "Không tìm thấy phiếu nhận trả.", StatusCodes.Status404NotFound);

        if (isStorekeeperOnly && currentUserId.HasValue
            && row.ret.AssignedStorekeeperId.HasValue
            && row.ret.AssignedStorekeeperId.Value != currentUserId.Value)
        {
            throw new AppException(
                "RETURN_OWNED_BY_ANOTHER_STOREKEEPER",
                "Phiếu thu hồi này đã được Thủ kho khác tiếp nhận phụ trách.",
                StatusCodes.Status403Forbidden);
        }

        var items = await (
            from item in db.EquipmentReturnItems.AsNoTracking()
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            join model in db.EquipmentModels.AsNoTracking() on equipment.EquipmentModelId equals model.Id
            where item.EquipmentReturnId == id
            orderby model.Name, equipment.SerialNumber
            select new { item, equipment, model.Code, model.Name }
        ).ToListAsync(cancellationToken);

        return new EquipmentReturnDetailResponse(
            (await ToResponsesAsync([row], cancellationToken)).Single(),
            items.Select(x => new EquipmentReturnItemResponse(
                x.item.Id,
                x.equipment.Id,
                x.equipment.SerialNumber,
                x.Code,
                x.Name,
                EnumName.ToDatabaseValue(x.item.Status),
                x.equipment.Condition,
                EnumName.ToDatabaseValue(x.equipment.Status),
                x.item.Note)).ToList());
    }

    public async Task<EquipmentReturnDetailResponse> CreateAsync(
        CreateEquipmentReturnRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var contractId = request.RentalContractId!.Value;

        await db.Database.ExecuteSqlInterpolatedAsync(
            $"SELECT 1 FROM rental_contracts WHERE id = {contractId} FOR UPDATE", cancellationToken);

        var contract = await db.RentalContracts.SingleOrDefaultAsync(c => c.Id == contractId, cancellationToken)
                       ?? throw new AppException("CONTRACT_NOT_FOUND", "Không tìm thấy hợp đồng.", StatusCodes.Status404NotFound);

        var requestedIds = request.Items.Select(i => i.EquipmentId!.Value).ToList();

        if (requestedIds.Distinct().Count() != requestedIds.Count)
        {
            throw new AppException("DUPLICATE_EQUIPMENT", "Một máy chỉ được kiểm một lần.",
                StatusCodes.Status400BadRequest, "items");
        }

        var outstandingIds = (await OutstandingQuery([contractId]).Select(x => x.EquipmentId).ToListAsync(cancellationToken))
            .ToHashSet();

        if (requestedIds.Any(id => !outstandingIds.Contains(id)))
        {
            throw new AppException(
                "EQUIPMENT_NOT_OUTSTANDING",
                "Có máy không thuộc hợp đồng này hoặc đã được nhận lại trước đó.",
                StatusCodes.Status400BadRequest,
                "items");
        }

        var now = DateTimeOffset.UtcNow;
        var returnedAt = request.ReturnedAt ?? now;

        if (returnedAt > now.AddMinutes(5))
        {
            throw new AppException("INVALID_RETURNED_AT", "Thời điểm nhận không được ở tương lai.",
                StatusCodes.Status400BadRequest, "returnedAt");
        }

        var equipments = await db.Equipments
            .Where(e => requestedIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var equipmentReturn = new EquipmentReturn
        {
            Id = Guid.CreateVersion7(),
            ReturnCode = await GenerateCodeAsync(cancellationToken),
            RentalContractId = contractId,
            ReturnedAt = returnedAt,
            ReceivedBy = currentUserId,
            AssignedStorekeeperId = currentUserId,
            Status = EquipmentReturnStatus.Completed,
            ReturnType = "NORMAL",
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.EquipmentReturns.Add(equipmentReturn);

        foreach (var line in request.Items)
        {
            var equipment = equipments[line.EquipmentId!.Value];
            var status = line.Status.Trim().ToUpperInvariant() switch
            {
                "RETURNED" => EquipmentReturnItemStatus.Returned,
                "MISSING" => EquipmentReturnItemStatus.Missing,
                _ => throw new AppException("INVALID_RETURN_ITEM_STATUS",
                    "Kết quả kiểm máy phải là RETURNED hoặc MISSING.", StatusCodes.Status400BadRequest, "items.status"),
            };

            if (status == EquipmentReturnItemStatus.Returned)
            {
                var condition = line.Condition?.Trim().ToUpperInvariant();

                if (condition is null || !ReturnConditions.Contains(condition))
                {
                    throw new AppException("INVALID_CONDITION",
                        $"Máy {equipment.SerialNumber}: tình trạng phải là GOOD, FAIR hoặc DAMAGED.",
                        StatusCodes.Status400BadRequest, "items.condition");
                }

                equipment.Condition = condition;
                equipment.Status = condition == "DAMAGED" ? EquipmentStatus.Damaged : EquipmentStatus.Available;
            }
            else
            {
                equipment.Status = EquipmentStatus.Lost;
            }

            equipment.UpdatedAt = now;

            db.EquipmentReturnItems.Add(new EquipmentReturnItem
            {
                Id = Guid.CreateVersion7(),
                EquipmentReturnId = equipmentReturn.Id,
                EquipmentId = equipment.Id,
                Status = status,
                Note = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim(),
            });
        }

        var returnedDate = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).Date);
        var lateFeeCreated = await ApplyLateReturnChargesIfOverdueAsync(
            contract,
            requestedIds.ToList(),
            returnedDate,
            currentUserId,
            equipmentReturn.ReturnCode,
            cancellationToken);

        var remaining = outstandingIds.Count - requestedIds.Count;
        var notifyManagerSettlement = false;

        if (remaining == 0 && contract.Status is RentalContractStatus.Delivered or RentalContractStatus.Expired)
        {
            var availableDeposit = Math.Max(0m, contract.DepositPaidAmount - contract.DepositAppliedAmount - contract.DepositRefundedAmount);
            var hasOpenCharges = lateFeeCreated > 0m || await db.RentalCharges.AsNoTracking()
                .AnyAsync(rc => rc.RentalContractId == contract.Id
                                && (rc.Status == RentalChargeStatus.Unpaid || rc.Status == RentalChargeStatus.PartiallyPaid)
                                && rc.Amount > rc.PaidAmount, cancellationToken);

            if (availableDeposit == 0m && !hasOpenCharges)
            {
                var previous = contract.Status;
                contract.Status = RentalContractStatus.Completed;
                contract.UpdatedAt = now;

                db.ContractStatusHistory.Add(new ContractStatusHistory
                {
                    Id = Guid.CreateVersion7(),
                    RentalContractId = contract.Id,
                    Status = RentalContractStatus.Completed,
                    PreviousStatus = previous,
                    ChangedBy = currentUserId,
                    ChangedAt = now,
                    Note = $"Kho đã nhận lại toàn bộ thiết bị ({equipmentReturn.ReturnCode}) — Hoàn tất hợp đồng.",
                });
            }
            else
            {
                contract.UpdatedAt = now;
                db.ContractStatusHistory.Add(new ContractStatusHistory
                {
                    Id = Guid.CreateVersion7(),
                    RentalContractId = contract.Id,
                    Status = contract.Status,
                    PreviousStatus = contract.Status,
                    ChangedBy = currentUserId,
                    ChangedAt = now,
                    Note = $"Kho đã nhận đủ toàn bộ thiết bị ({equipmentReturn.ReturnCode})" +
                           (lateFeeCreated > 0 ? $" (phát sinh phí trả trễ {lateFeeCreated:N0} đ)" : "") +
                           " — Chờ Quản lý quyết toán & hoàn cọc.",
                });
                notifyManagerSettlement = true;
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (notifyManagerSettlement && contract.ApprovedBy.HasValue)
        {
            await notificationDispatcher.NotifyUsersAsync(
                [contract.ApprovedBy.Value],
                $"Đã thu hồi đủ máy — Sẵn sàng quyết toán ({contract.ContractCode})",
                $"Kho đã kiểm nhận xong toàn bộ thiết bị của hợp đồng {contract.ContractCode} ({equipmentReturn.ReturnCode})" +
                (lateFeeCreated > 0 ? $", phát sinh phí trả trễ {lateFeeCreated:N0} đ" : "") +
                ". Vui lòng vào chi tiết hợp đồng để thực hiện quyết toán, cấn trừ nợ/phạt và hoàn cọc cho khách.",
                "CONTRACT_READY_FOR_SETTLEMENT",
                cancellationToken: cancellationToken);
        }

        return await GetReturnDetailAsync(equipmentReturn.Id, currentUserId, isStorekeeperOnly: true, cancellationToken);
    }

    /// <summary>
    /// Thủ kho xác nhận kiểm đếm phiếu thu hồi tự động sinh ra khi hủy hợp đồng lúc đang giao (PROCESSING).
    /// Bắt buộc kiểm đủ 100% serial trong phiếu; toàn bộ máy thu hồi về kho chuyển sang MAINTENANCE
    /// và tự động sinh nhiệm vụ kiểm tra bảo dưỡng (INSPECTION) trước khi cho thuê lại.
    /// </summary>
    /// <summary>Thủ kho tiếp nhận phụ trách phiếu thu hồi chưa có người nhận (khóa không cho Thủ kho khác thấy/xác nhận).</summary>
    public async Task<EquipmentReturnDetailResponse> ClaimAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ret = await db.EquipmentReturns
            .FromSqlInterpolated($"SELECT * FROM equipment_returns WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new AppException("RETURN_NOT_FOUND", "Không tìm thấy phiếu thu hồi.", StatusCodes.Status404NotFound);

        if (ret.Status != EquipmentReturnStatus.Processing)
        {
            throw new AppException(
                "RETURN_NOT_PROCESSING",
                "Chỉ có thể tiếp nhận phiếu thu hồi đang chờ kiểm nhận (PROCESSING).",
                StatusCodes.Status409Conflict);
        }

        if (ret.AssignedStorekeeperId.HasValue && ret.AssignedStorekeeperId.Value != currentUserId)
        {
            throw new AppException(
                "ALREADY_CLAIMED_BY_ANOTHER_STOREKEEPER",
                "Phiếu thu hồi này đã được Thủ kho khác tiếp nhận trước đó.",
                StatusCodes.Status409Conflict);
        }

        if (!ret.AssignedStorekeeperId.HasValue)
        {
            ret.AssignedStorekeeperId = currentUserId;
            ret.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
        return await GetReturnDetailAsync(id, currentUserId, isStorekeeperOnly: true, cancellationToken);
    }

    public async Task<EquipmentReturnDetailResponse> ConfirmProcessingAsync(
        Guid id,
        ConfirmProcessingReturnRequest request,
        Guid currentUserId,
        CancellationToken cancellationToken = default)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken);

        var ret = await db.EquipmentReturns
            .FromSqlInterpolated($"SELECT * FROM equipment_returns WHERE id = {id} FOR UPDATE")
            .SingleOrDefaultAsync(cancellationToken)
                  ?? throw new AppException("RETURN_NOT_FOUND", "Không tìm thấy phiếu thu hồi.", StatusCodes.Status404NotFound);

        if (ret.AssignedStorekeeperId.HasValue && ret.AssignedStorekeeperId.Value != currentUserId)
        {
            throw new AppException(
                "RETURN_OWNED_BY_ANOTHER_STOREKEEPER",
                "Phiếu thu hồi này đã được Thủ kho khác tiếp nhận phụ trách, bạn không thể xác nhận.",
                StatusCodes.Status403Forbidden);
        }

        ret.AssignedStorekeeperId ??= currentUserId;

        if (ret.Status != EquipmentReturnStatus.Processing)
        {
            throw new AppException(
                "RETURN_NOT_PROCESSING",
                "Chỉ phiếu thu hồi đang chờ kiểm nhận (PROCESSING) mới có thể xác nhận.",
                StatusCodes.Status409Conflict);
        }

        var existingItems = await db.EquipmentReturnItems
            .Where(i => i.EquipmentReturnId == id)
            .ToListAsync(cancellationToken);

        var expectedIds = existingItems.Select(i => i.EquipmentId).ToHashSet();
        var submittedIds = request.Items.Select(i => i.EquipmentId!.Value).ToList();

        if (submittedIds.Distinct().Count() != submittedIds.Count)
        {
            throw new AppException("DUPLICATE_EQUIPMENT", "Một máy chỉ được kiểm một lần.",
                StatusCodes.Status400BadRequest, "items");
        }

        if (!expectedIds.SetEquals(submittedIds))
        {
            throw new AppException(
                "INCOMPLETE_RETURN_VERIFICATION",
                "Thủ kho bắt buộc kiểm đếm chính xác toàn bộ danh sách thiết bị ghi trên phiếu thu hồi (không được thiếu hoặc thừa serial).",
                StatusCodes.Status400BadRequest,
                "items");
        }

        var now = DateTimeOffset.UtcNow;
        var equipments = await db.Equipments
            .Where(e => expectedIds.Contains(e.Id))
            .ToDictionaryAsync(e => e.Id, cancellationToken);

        var lineMap = request.Items.ToDictionary(i => i.EquipmentId!.Value);

        foreach (var itemEntity in existingItems)
        {
            var line = lineMap[itemEntity.EquipmentId];
            var equipment = equipments[itemEntity.EquipmentId];

            var status = line.Status.Trim().ToUpperInvariant() switch
            {
                "RETURNED" => EquipmentReturnItemStatus.Returned,
                "MISSING" => EquipmentReturnItemStatus.Missing,
                _ => throw new AppException("INVALID_RETURN_ITEM_STATUS",
                    "Kết quả kiểm máy phải là RETURNED hoặc MISSING.", StatusCodes.Status400BadRequest, "items.status"),
            };

            itemEntity.Status = status;
            itemEntity.Note = string.IsNullOrWhiteSpace(line.Note) ? null : line.Note.Trim();

            if (status == EquipmentReturnItemStatus.Returned)
            {
                var condition = string.IsNullOrWhiteSpace(line.Condition)
                    ? equipment.Condition
                    : line.Condition.Trim().ToUpperInvariant();

                if (!ReturnConditions.Contains(condition))
                {
                    throw new AppException("INVALID_CONDITION",
                        $"Máy {equipment.SerialNumber}: tình trạng phải là GOOD, FAIR hoặc DAMAGED.",
                        StatusCodes.Status400BadRequest, "items.condition");
                }

                equipment.Condition = condition;
                equipment.Status = EquipmentStatus.Maintenance;

                var (taskType, taskDesc) = ret.ReturnType.ToUpperInvariant() switch
                {
                    "DEFECTIVE_ON_DELIVERY" => (
                        "CORRECTIVE",
                        $"Kiểm tra & sửa chữa máy lỗi khi giao/lắp đặt ({ret.ReturnCode}) - Serial {equipment.SerialNumber}"
                    ),
                    "INCIDENT_RETURN" => (
                        "CORRECTIVE",
                        $"Kiểm tra & sửa chữa máy thu hồi từ sự cố ({ret.ReturnCode}) - Serial {equipment.SerialNumber}"
                    ),
                    "NORMAL" => (
                        "INSPECTION",
                        $"Kiểm tra & bảo dưỡng thiết bị thu hồi cuối kỳ ({ret.ReturnCode}) - Serial {equipment.SerialNumber}"
                    ),
                    _ => (
                        "INSPECTION",
                        $"Kiểm tra & bảo dưỡng thiết bị thu hồi sau hủy giao giữa chừng ({ret.ReturnCode}) - Serial {equipment.SerialNumber}"
                    ),
                };

                db.MaintenanceTasks.Add(new MaintenanceTask
                {
                    Id = Guid.CreateVersion7(),
                    EquipmentId = equipment.Id,
                    Type = taskType,
                    Description = taskDesc,
                    ScheduledAt = now,
                    Status = MaintenanceTaskStatus.Pending,
                    CreatedAt = now,
                    UpdatedAt = now,
                });
            }
            else
            {
                equipment.Status = EquipmentStatus.Lost;
            }

            equipment.UpdatedAt = now;
        }

        ret.Status = EquipmentReturnStatus.Completed;
        ret.ReceivedBy = currentUserId;
        ret.ReturnedAt = now;
        if (!string.IsNullOrWhiteSpace(request.Note))
        {
            ret.Note = string.IsNullOrWhiteSpace(ret.Note)
                ? request.Note.Trim()
                : $"{ret.Note}\nKho xác nhận: {request.Note.Trim()}";
        }
        ret.UpdatedAt = now;

        var notifyNormalSettlement = false;
        decimal normalLateFee = 0m;
        RentalContract? normalContract = null;

        if (string.Equals(ret.ReturnType, "NORMAL", StringComparison.OrdinalIgnoreCase))
        {
            normalContract = await db.RentalContracts.FirstOrDefaultAsync(c => c.Id == ret.RentalContractId, cancellationToken);
            if (normalContract is not null)
            {
                var returnedDate = DateOnly.FromDateTime(now.ToOffset(TimeSpan.FromHours(7)).Date);
                normalLateFee = await ApplyLateReturnChargesIfOverdueAsync(
                    normalContract,
                    expectedIds.ToList(),
                    returnedDate,
                    currentUserId,
                    ret.ReturnCode,
                    cancellationToken);

                await db.SaveChangesAsync(cancellationToken);

                var remainingOut = await OutstandingQuery([normalContract.Id]).CountAsync(cancellationToken);
                var otherPendingReturns = await db.EquipmentReturns.AsNoTracking()
                    .AnyAsync(r => r.RentalContractId == normalContract.Id
                                   && r.Id != ret.Id
                                   && (r.Status == EquipmentReturnStatus.Draft || r.Status == EquipmentReturnStatus.Processing),
                        cancellationToken);

                if (remainingOut == 0 && !otherPendingReturns
                    && normalContract.Status is RentalContractStatus.Delivered or RentalContractStatus.Expired)
                {
                    var availDep = Math.Max(0m, normalContract.DepositPaidAmount - normalContract.DepositAppliedAmount - normalContract.DepositRefundedAmount);
                    var hasOpenCharges = normalLateFee > 0m || await db.RentalCharges.AsNoTracking()
                        .AnyAsync(rc => rc.RentalContractId == normalContract.Id
                                        && (rc.Status == RentalChargeStatus.Unpaid || rc.Status == RentalChargeStatus.PartiallyPaid)
                                        && rc.Amount > rc.PaidAmount, cancellationToken);

                    if (availDep == 0m && !hasOpenCharges)
                    {
                        var prev = normalContract.Status;
                        normalContract.Status = RentalContractStatus.Completed;
                        normalContract.UpdatedAt = now;
                        db.ContractStatusHistory.Add(new ContractStatusHistory
                        {
                            Id = Guid.CreateVersion7(),
                            RentalContractId = normalContract.Id,
                            PreviousStatus = prev,
                            Status = RentalContractStatus.Completed,
                            ChangedBy = currentUserId,
                            ChangedAt = now,
                            Note = $"Kho kiểm nhận hoàn tất thu hồi toàn bộ thiết bị ({ret.ReturnCode}) — Hoàn tất hợp đồng.",
                        });
                    }
                    else
                    {
                        normalContract.UpdatedAt = now;
                        db.ContractStatusHistory.Add(new ContractStatusHistory
                        {
                            Id = Guid.CreateVersion7(),
                            RentalContractId = normalContract.Id,
                            PreviousStatus = normalContract.Status,
                            Status = normalContract.Status,
                            ChangedBy = currentUserId,
                            ChangedAt = now,
                            Note = $"Kho kiểm nhận thu hồi đủ thiết bị ({ret.ReturnCode})" +
                                   (normalLateFee > 0 ? $" (phát sinh phí trả trễ {normalLateFee:N0} đ)" : "") +
                                   " — Chờ Quản lý quyết toán & hoàn cọc.",
                        });
                        notifyNormalSettlement = true;
                    }
                }
            }
        }

        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);

        if (notifyNormalSettlement && normalContract?.ApprovedBy.HasValue == true)
        {
            await notificationDispatcher.NotifyUsersAsync(
                [normalContract.ApprovedBy.Value],
                $"Đã thu hồi đủ máy — Sẵn sàng quyết toán ({normalContract.ContractCode})",
                $"Kho đã kiểm nhận hoàn tất phiếu thu hồi {ret.ReturnCode} của hợp đồng {normalContract.ContractCode}" +
                (normalLateFee > 0 ? $" (phát sinh phí trả trễ {normalLateFee:N0} đ)" : "") +
                ". Vui lòng vào chi tiết hợp đồng để thực hiện quyết toán & hoàn cọc.",
                "CONTRACT_READY_FOR_SETTLEMENT",
                cancellationToken: cancellationToken);
        }

        // Nếu vừa thu hồi xong máy lỗi khi giao (DEFECTIVE_ON_DELIVERY) và không còn phiếu thu hồi máy lỗi nào đang PROCESSING trên hợp đồng
        // -> báo ngay cho Manager & Thủ kho biết đã sẵn sàng lập phiếu & xếp lịch giao bù!
        if (string.Equals(ret.ReturnType, "DEFECTIVE_ON_DELIVERY", StringComparison.OrdinalIgnoreCase))
        {
            var hasOtherPendingDefReturn = await db.EquipmentReturns.AsNoTracking()
                .AnyAsync(r => r.RentalContractId == ret.RentalContractId
                               && r.Id != ret.Id
                               && r.ReturnType == "DEFECTIVE_ON_DELIVERY"
                               && (r.Status == EquipmentReturnStatus.Processing || r.Status == EquipmentReturnStatus.Draft),
                    cancellationToken);

            if (!hasOtherPendingDefReturn)
            {
                var contract = await db.RentalContracts.AsNoTracking()
                    .FirstOrDefaultAsync(c => c.Id == ret.RentalContractId, cancellationToken);
                if (contract != null && contract.Status == RentalContractStatus.PartiallyDelivered)
                {
                    var notifyIds = new List<Guid>();
                    if (contract.ApprovedBy.HasValue) notifyIds.Add(contract.ApprovedBy.Value);

                    var storekeeperIds = await (
                        from ur in db.UserRoles.AsNoTracking()
                        join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                        where r.Code == "LEAD_STOREKEEPER" || r.Code == "STOREKEEPER"
                        select ur.UserId
                    ).Distinct().ToListAsync(cancellationToken);
                    notifyIds.AddRange(storekeeperIds);

                    if (notifyIds.Count > 0)
                    {
                        await notificationDispatcher.NotifyUsersAsync(
                            notifyIds.Distinct(),
                            $"Đã thu hồi xong máy lỗi — Sẵn sàng giao bù ({contract.ContractCode})",
                            $"Kho đã kiểm nhận hoàn tất phiếu thu hồi máy lỗi {ret.ReturnCode} (chuyển bảo trì). Hợp đồng {contract.ContractCode} đã sẵn sàng soạn phiếu giao bù và xếp lịch giao bù (miễn phí ship).",
                            "SUPPLEMENTARY_READY",
                            cancellationToken: cancellationToken);
                    }
                }
            }
        }

        return await GetReturnDetailAsync(id, currentUserId, isStorekeeperOnly: true, cancellationToken);
    }

    private sealed class OutstandingRow
    {
        public Guid ContractId { get; init; }
        public Guid EquipmentId { get; init; }
        public string SerialNumber { get; init; } = null!;
        public Guid EquipmentModelId { get; init; }
        public string ModelCode { get; init; } = null!;
        public string ModelName { get; init; } = null!;
        public string Condition { get; init; } = null!;
        public string HandoverCode { get; init; } = null!;
    }

    private IQueryable<OutstandingRow> OutstandingQuery(List<Guid> contractIds) =>
        from note in db.HandoverNotes.AsNoTracking()
        join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
        join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
        join model in db.EquipmentModels.AsNoTracking() on equipment.EquipmentModelId equals model.Id
        where contractIds.Contains(note.RentalContractId) &&
              note.Status == HandoverNoteStatus.Completed &&
              equipment.Status == EquipmentStatus.Rented &&
              !(from ret in db.EquipmentReturns
                join retItem in db.EquipmentReturnItems on ret.Id equals retItem.EquipmentReturnId
                where ret.RentalContractId == note.RentalContractId &&
                      ret.Status != EquipmentReturnStatus.Cancelled &&
                      retItem.EquipmentId == equipment.Id
                select retItem.Id).Any()
        select new OutstandingRow
        {
            ContractId = note.RentalContractId,
            EquipmentId = equipment.Id,
            SerialNumber = equipment.SerialNumber,
            EquipmentModelId = model.Id,
            ModelCode = model.Code,
            ModelName = model.Name,
            Condition = equipment.Condition,
            HandoverCode = note.HandoverCode,
        };

    private async Task<Dictionary<Guid, int>> DeliveredCountsAsync(List<Guid> contractIds, CancellationToken cancellationToken) =>
        await (
            from note in db.HandoverNotes.AsNoTracking()
            join item in db.HandoverItems.AsNoTracking() on note.Id equals item.HandoverNoteId
            where contractIds.Contains(note.RentalContractId) && note.Status == HandoverNoteStatus.Completed
            group item by note.RentalContractId into g
            select new { g.Key, Count = g.Count() }
        ).ToDictionaryAsync(x => x.Key, x => x.Count, cancellationToken);

    private sealed class ReturnRow
    {
        public EquipmentReturn ret { get; init; } = null!;
        public string ContractCode { get; init; } = null!;
        public string CompanyName { get; init; } = null!;
    }

    private IQueryable<ReturnRow> ReturnQuery() =>
        from ret in db.EquipmentReturns.AsNoTracking()
        join contract in db.RentalContracts.AsNoTracking() on ret.RentalContractId equals contract.Id
        join company in db.Companies.AsNoTracking() on contract.CompanyId equals company.Id
        select new ReturnRow { ret = ret, ContractCode = contract.ContractCode, CompanyName = company.Name };

    private async Task<List<EquipmentReturnResponse>> ToResponsesAsync(
        List<ReturnRow> rows,
        CancellationToken cancellationToken)
    {
        var ids = rows.Select(r => r.ret.Id).ToList();
        var receiverIds = rows.Select(r => r.ret.ReceivedBy)
            .Concat(rows.Where(r => r.ret.AssignedStorekeeperId.HasValue).Select(r => r.ret.AssignedStorekeeperId!.Value))
            .Distinct()
            .ToList();

        var stats = await (
            from item in db.EquipmentReturnItems.AsNoTracking()
            join equipment in db.Equipments.AsNoTracking() on item.EquipmentId equals equipment.Id
            where ids.Contains(item.EquipmentReturnId)
            group new { item.Status, equipment.Condition } by item.EquipmentReturnId into g
            select new
            {
                g.Key,
                Returned = g.Count(x => x.Status == EquipmentReturnItemStatus.Returned),
                Missing = g.Count(x => x.Status == EquipmentReturnItemStatus.Missing),
                Damaged = g.Count(x => x.Status == EquipmentReturnItemStatus.Returned && x.Condition == "DAMAGED"),
            }
        ).ToDictionaryAsync(x => x.Key, cancellationToken);

        var receivers = await db.Users.AsNoTracking()
            .Where(u => receiverIds.Contains(u.Id))
            .ToDictionaryAsync(u => u.Id, u => u.FullName, cancellationToken);

        return rows.Select(r =>
        {
            var s = stats.GetValueOrDefault(r.ret.Id);

            return new EquipmentReturnResponse(
                r.ret.Id,
                r.ret.ReturnCode,
                r.ret.RentalContractId,
                r.ContractCode,
                r.CompanyName,
                r.ret.ReturnedAt,
                receivers.GetValueOrDefault(r.ret.ReceivedBy),
                EnumName.ToDatabaseValue(r.ret.Status),
                r.ret.ReturnType,
                r.ret.Note,
                s?.Returned ?? 0,
                s?.Missing ?? 0,
                s?.Damaged ?? 0,
                r.ret.CreatedAt,
                r.ret.AssignedStorekeeperId,
                r.ret.AssignedStorekeeperId.HasValue ? receivers.GetValueOrDefault(r.ret.AssignedStorekeeperId.Value) : null);
        }).ToList();
    }

    private static ReturnContractResponse ToContractResponse(
        RentalContract contract, string companyCode, string companyName, int delivered, int outstanding) =>
        new(
            contract.Id,
            contract.ContractCode,
            companyCode,
            companyName,
            contract.StartDate,
            contract.EndDate,
            EnumName.ToDatabaseValue(contract.Status),
            contract.DeliveryAddress,
            delivered,
            outstanding);

    private async Task<string> GenerateCodeAsync(CancellationToken cancellationToken)
    {
        var prefix = $"{CodePrefix}-{EquipmentAvailabilityService.BusinessToday().Year}-";

        var codes = await db.EquipmentReturns
            .AsNoTracking()
            .Where(r => r.ReturnCode.StartsWith(prefix))
            .Select(r => r.ReturnCode)
            .ToListAsync(cancellationToken);

        var max = codes
            .Select(code => int.TryParse(code[prefix.Length..], out var n) ? n : 0)
            .DefaultIfEmpty(0)
            .Max();

        return $"{prefix}{max + 1:D4}";
    }

    /// <summary>
    /// Tính phí trả thiết bị trễ hạn (LATE_RETURN = 150% đơn giá ngày × số ngày trễ sau EndDate)
    /// cho các máy thuộc phiếu thu hồi NORMAL. Chống tạo trùng nếu thiết bị đó đã có khoản LATE_RETURN.
    /// </summary>
    private async Task<decimal> ApplyLateReturnChargesIfOverdueAsync(
        RentalContract contract,
        List<Guid> returnedEquipmentIds,
        DateOnly returnedDate,
        Guid actorUserId,
        string returnCode,
        CancellationToken ct)
    {
        if (returnedEquipmentIds.Count == 0 || returnedDate <= contract.EndDate)
            return 0m;

        var lateDays = returnedDate.DayNumber - contract.EndDate.DayNumber;
        if (lateDays <= 0) return 0m;

        var contractItems = await db.RentalContractItems.AsNoTracking()
            .Where(i => i.RentalContractId == contract.Id)
            .ToListAsync(ct);

        var dailyRateByModel = contractItems
            .GroupBy(i => i.EquipmentModelId)
            .ToDictionary(
                g => g.Key,
                g =>
                {
                    var it = g.First();
                    return string.Equals(it.BillingUnit, "MONTHLY", StringComparison.OrdinalIgnoreCase)
                        ? it.AgreedUnitPrice / 30m
                        : it.AgreedUnitPrice;
                });

        var fallbackDailyRate = dailyRateByModel.Count > 0 ? dailyRateByModel.Values.First() : 0m;

        var eqInfos = await db.Equipments.AsNoTracking()
            .Where(e => returnedEquipmentIds.Contains(e.Id))
            .Select(e => new { e.Id, e.SerialNumber, e.EquipmentModelId })
            .ToListAsync(ct);

        var alreadyChargedEqIds = (await db.RentalCharges.AsNoTracking()
            .Where(rc => rc.RentalContractId == contract.Id
                         && rc.ChargeType == "LATE_RETURN"
                         && rc.EquipmentId != null
                         && returnedEquipmentIds.Contains(rc.EquipmentId.Value)
                         && rc.Status != RentalChargeStatus.Cancelled)
            .Select(rc => rc.EquipmentId!.Value)
            .ToListAsync(ct)).ToHashSet();

        var now = DateTimeOffset.UtcNow;
        var totalCreated = 0m;

        foreach (var eq in eqInfos)
        {
            if (alreadyChargedEqIds.Contains(eq.Id)) continue;

            var dailyRate = dailyRateByModel.TryGetValue(eq.EquipmentModelId, out var r) ? r : fallbackDailyRate;
            var penaltyUnitPrice = Math.Round(dailyRate * LateReturnDailyMultiplier, 0, MidpointRounding.AwayFromZero);
            var amount = penaltyUnitPrice * lateDays;
            if (amount <= 0m) continue;

            db.RentalCharges.Add(new RentalCharge
            {
                Id = Guid.CreateVersion7(),
                SourceType = "CONTRACT",
                RentalContractId = contract.Id,
                EquipmentId = eq.Id,
                ChargeType = "LATE_RETURN",
                Description = $"Phí trả thiết bị trễ hạn {lateDays} ngày (150% đơn giá ngày) — {eq.SerialNumber} ({returnCode})",
                Quantity = lateDays,
                UnitPrice = penaltyUnitPrice,
                Amount = amount,
                PeriodStart = contract.EndDate.AddDays(1),
                PeriodEnd = returnedDate,
                DueDate = returnedDate.AddDays(3),
                PaidAmount = 0m,
                Status = RentalChargeStatus.Unpaid,
                CreatedBy = actorUserId,
                CreatedAt = now,
                Note = $"Hết hạn HĐ {contract.EndDate:dd/MM/yyyy}, trả thực tế {returnedDate:dd/MM/yyyy} (trễ {lateDays} ngày).",
            });
            totalCreated += amount;
        }

        return totalCreated;
    }

}
