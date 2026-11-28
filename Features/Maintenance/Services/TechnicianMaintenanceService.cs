using HeThongHoTroChoThueThietBi.Common.Exceptions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Abstractions;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Extensions;
using HeThongHoTroChoThueThietBi.Common.Notifications.Abstractions;
using HeThongHoTroChoThueThietBi.Common.Persistence;
using HeThongHoTroChoThueThietBi.Features.Equipment.Enums;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using HeThongHoTroChoThueThietBi.Features.Maintenance.DTOs;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using HeThongHoTroChoThueThietBi.Features.Operations.Enums;
using HeThongHoTroChoThueThietBi.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Services;

/// <summary>
/// Quản lý phiếu kiểm tra / bảo trì thiết bị (MAINTENANCE) cho Kỹ thuật viên:
/// xem danh sách máy chờ kiểm tra/sửa chữa, bắt đầu thực hiện, ghi nhận công việc + linh kiện + ảnh minh chứng
/// và cập nhật trạng thái máy (AVAILABLE / DAMAGED / tiếp tục MAINTENANCE).
/// </summary>
public sealed class TechnicianMaintenanceService(
    AppDbContext db,
    IFileUploadService uploadService,
    INotificationDispatcher notificationDispatcher)
{
    private static readonly string[] AllowedImageFormats = ["jpg", "jpeg", "png", "webp"];
    private static readonly string[] AllowedTaskTypes = ["INSPECTION", "PREVENTIVE", "CORRECTIVE"];

    public async Task<List<MaintenanceTaskSummaryResponse>> GetTasksAsync(
        string? status,
        string? type,
        string? keyword,
        Guid? currentUserId = null,
        bool isTechnicianOnly = false,
        CancellationToken ct = default)
    {
        Guid? currentStaffId = null;
        if (currentUserId.HasValue)
        {
            currentStaffId = await db.Staff.AsNoTracking()
                .Where(s => s.UserId == currentUserId.Value)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
        }

        var query =
            from t in db.MaintenanceTasks.AsNoTracking()
            join eq in db.Equipments.AsNoTracking() on t.EquipmentId equals eq.Id
            join m in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals m.Id
            join w in db.Warehouses.AsNoTracking() on eq.WarehouseId equals w.Id
            select new { t, eq, m, WarehouseName = w.Name };

        if (isTechnicianOnly && currentStaffId.HasValue)
        {
            // KTV chỉ thấy phiếu CHƯA AI NHẬN (AssignedTechnicianStaffId == null) hoặc DO CHÍNH MÌNH PHỤ TRÁCH
            query = query.Where(x =>
                x.t.AssignedTechnicianStaffId == null ||
                x.t.AssignedTechnicianStaffId == currentStaffId.Value);
        }

        if (!string.IsNullOrWhiteSpace(status))
        {
            var parsed = ParseTaskStatus(status);
            query = query.Where(x => x.t.Status == parsed);
        }

        if (!string.IsNullOrWhiteSpace(type))
        {
            var upperType = type.Trim().ToUpperInvariant();
            query = query.Where(x => x.t.Type == upperType);
        }

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim().ToLowerInvariant();
            query = query.Where(x =>
                x.eq.SerialNumber.ToLower().Contains(kw) ||
                x.m.Code.ToLower().Contains(kw) ||
                x.m.Name.ToLower().Contains(kw) ||
                x.t.Description.ToLower().Contains(kw));
        }

        var rows = await query
            .OrderByDescending(x => x.t.Status == MaintenanceTaskStatus.InProgress)
            .ThenByDescending(x => x.t.Status == MaintenanceTaskStatus.Pending)
            .ThenByDescending(x => x.t.CreatedAt)
            .ToListAsync(ct);

        if (rows.Count == 0) return [];

        var taskIds = rows.Select(x => x.t.Id).ToList();

        var attCounts = await db.MaintenanceTaskAttachments.AsNoTracking()
            .Where(a => taskIds.Contains(a.MaintenanceTaskId))
            .GroupBy(a => a.MaintenanceTaskId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var recCounts = await db.MaintenanceRecords.AsNoTracking()
            .Where(r => taskIds.Contains(r.MaintenanceTaskId))
            .GroupBy(r => r.MaintenanceTaskId)
            .Select(g => new { g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Key, x => x.Count, ct);

        var assignedStaffIds = rows
            .Where(x => x.t.AssignedTechnicianStaffId.HasValue)
            .Select(x => x.t.AssignedTechnicianStaffId!.Value)
            .Distinct()
            .ToList();

        var staffNames = assignedStaffIds.Count == 0
            ? new Dictionary<Guid, string>()
            : await (
                from s in db.Staff.AsNoTracking()
                join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                where assignedStaffIds.Contains(s.Id)
                select new { s.Id, u.FullName }
            ).ToDictionaryAsync(x => x.Id, x => x.FullName, ct);

        return rows.Select(x => new MaintenanceTaskSummaryResponse(
            x.t.Id,
            x.eq.Id,
            x.eq.SerialNumber,
            x.m.Id,
            x.m.Code,
            x.m.Name,
            x.WarehouseName,
            EnumName.ToDatabaseValue(x.eq.Status),
            x.eq.Condition,
            x.t.Type,
            x.t.Description,
            EnumName.ToDatabaseValue(x.t.Status),
            x.t.ScheduledAt,
            x.t.StartedAt,
            x.t.CompletedAt,
            x.t.Result,
            attCounts.GetValueOrDefault(x.t.Id, 0),
            recCounts.GetValueOrDefault(x.t.Id, 0),
            x.t.CreatedAt,
            x.t.AssignedTechnicianStaffId,
            x.t.AssignedTechnicianStaffId.HasValue
                ? staffNames.GetValueOrDefault(x.t.AssignedTechnicianStaffId.Value)
                : null,
            currentStaffId.HasValue && x.t.AssignedTechnicianStaffId == currentStaffId.Value)).ToList();
    }

    public async Task<MaintenanceTaskDetailResponse> GetTaskDetailAsync(
        Guid id,
        Guid? currentUserId = null,
        bool isTechnicianOnly = false,
        CancellationToken ct = default)
    {
        Guid? currentStaffId = null;
        if (currentUserId.HasValue)
        {
            currentStaffId = await db.Staff.AsNoTracking()
                .Where(s => s.UserId == currentUserId.Value)
                .Select(s => (Guid?)s.Id)
                .FirstOrDefaultAsync(ct);
        }

        var row = await (
            from t in db.MaintenanceTasks.AsNoTracking()
            join eq in db.Equipments.AsNoTracking() on t.EquipmentId equals eq.Id
            join m in db.EquipmentModels.AsNoTracking() on eq.EquipmentModelId equals m.Id
            join w in db.Warehouses.AsNoTracking() on eq.WarehouseId equals w.Id
            where t.Id == id
            select new { t, eq, m, WarehouseName = w.Name }
        ).FirstOrDefaultAsync(ct)
            ?? throw new AppException("MAINTENANCE_TASK_NOT_FOUND", "Không tìm thấy phiếu bảo trì.", StatusCodes.Status404NotFound);

        if (isTechnicianOnly && currentStaffId.HasValue
            && row.t.AssignedTechnicianStaffId.HasValue
            && row.t.AssignedTechnicianStaffId.Value != currentStaffId.Value)
        {
            throw new AppException(
                "MAINTENANCE_OWNED_BY_ANOTHER_TECHNICIAN",
                "Phiếu bảo trì này đã được Kỹ thuật viên khác tiếp nhận phụ trách.",
                StatusCodes.Status403Forbidden);
        }

        var files = await (
            from att in db.MaintenanceTaskAttachments.AsNoTracking()
            join f in db.StorageFiles.AsNoTracking() on att.StorageFileId equals f.Id
            where att.MaintenanceTaskId == id
            orderby att.CreatedAt
            select f
        ).ToListAsync(ct);

        var attachmentUrls = files.Select(f => uploadService.GetPrivateImageUrl(
            $"{f.BucketName}/{f.ObjectKey}",
            f.ResourceType.ToCloudinaryNetResourceType(),
            f.ContentType ?? "jpg")).ToList();

        var recRows = await (
            from r in db.MaintenanceRecords.AsNoTracking()
            join s in db.Staff.AsNoTracking() on r.PerformedBy equals s.Id
            join u in db.Users.AsNoTracking() on s.UserId equals u.Id
            where r.MaintenanceTaskId == id
            orderby r.CompletedAt descending
            select new { r, PerformedByName = u.FullName }
        ).ToListAsync(ct);

        var recIds = recRows.Select(x => x.r.Id).ToList();
        var parts = recIds.Count == 0
            ? []
            : await db.MaintenanceParts.AsNoTracking()
                .Where(p => recIds.Contains(p.MaintenanceRecordId))
                .OrderBy(p => p.Name)
                .ToListAsync(ct);

        var recordDtos = recRows.Select(x =>
        {
            var recParts = parts
                .Where(p => p.MaintenanceRecordId == x.r.Id)
                .Select(p => new MaintenancePartResponse(
                    p.Id, p.Name, p.Quantity, p.UnitCost, p.TotalCost, p.Note))
                .ToList();

            return new MaintenanceRecordResponse(
                x.r.Id,
                x.r.PerformedBy,
                x.PerformedByName,
                x.r.Description,
                x.r.LaborCost,
                recParts.Sum(p => p.TotalCost),
                x.r.CompletedAt,
                x.r.Note,
                recParts);
        }).ToList();

        string? assignedName = null;
        if (row.t.AssignedTechnicianStaffId.HasValue)
        {
            assignedName = await (
                from s in db.Staff.AsNoTracking()
                join u in db.Users.AsNoTracking() on s.UserId equals u.Id
                where s.Id == row.t.AssignedTechnicianStaffId.Value
                select u.FullName
            ).FirstOrDefaultAsync(ct);
        }

        var summary = new MaintenanceTaskSummaryResponse(
            row.t.Id,
            row.eq.Id,
            row.eq.SerialNumber,
            row.m.Id,
            row.m.Code,
            row.m.Name,
            row.WarehouseName,
            EnumName.ToDatabaseValue(row.eq.Status),
            row.eq.Condition,
            row.t.Type,
            row.t.Description,
            EnumName.ToDatabaseValue(row.t.Status),
            row.t.ScheduledAt,
            row.t.StartedAt,
            row.t.CompletedAt,
            row.t.Result,
            attachmentUrls.Count,
            recordDtos.Count,
            row.t.CreatedAt,
            row.t.AssignedTechnicianStaffId,
            assignedName,
            currentStaffId.HasValue && row.t.AssignedTechnicianStaffId == currentStaffId.Value);

        var incidentContext = await GetLinkedIncidentContextAsync(row.eq.Id, ct);
        return new MaintenanceTaskDetailResponse(summary, attachmentUrls, recordDtos, incidentContext);
    }

    public async Task<MaintenanceTaskDetailResponse> CreateTaskAsync(
        CreateMaintenanceTaskRequest request,
        CancellationToken ct = default)
    {
        var equipment = await db.Equipments.FirstOrDefaultAsync(e => e.Id == request.EquipmentId!.Value, ct)
            ?? throw new AppException("EQUIPMENT_NOT_FOUND", "Không tìm thấy thiết bị.", StatusCodes.Status404NotFound);

        if (equipment.Status is EquipmentStatus.Rented or EquipmentStatus.Reserved or EquipmentStatus.Disposed or EquipmentStatus.Lost)
        {
            throw new AppException(
                "EQUIPMENT_NOT_ELIGIBLE_FOR_MAINTENANCE",
                $"Thiết bị {equipment.SerialNumber} đang ở trạng thái {EnumName.ToDatabaseValue(equipment.Status)}, không thể tạo phiếu bảo trì tại kho.",
                StatusCodes.Status409Conflict);
        }

        var normalizedType = (request.Type ?? "INSPECTION").Trim().ToUpperInvariant();
        if (!AllowedTaskTypes.Contains(normalizedType))
        {
            throw new AppException(
                "INVALID_MAINTENANCE_TYPE",
                "Loại phiếu bảo trì phải là INSPECTION, PREVENTIVE hoặc CORRECTIVE.",
                StatusCodes.Status400BadRequest);
        }

        var now = DateTimeOffset.UtcNow;
        var task = new MaintenanceTask
        {
            Id = Guid.CreateVersion7(),
            EquipmentId = equipment.Id,
            Type = normalizedType,
            Description = request.Description.Trim(),
            ScheduledAt = request.ScheduledAt?.ToUniversalTime() ?? now,
            Status = MaintenanceTaskStatus.Pending,
            CreatedAt = now,
            UpdatedAt = now,
        };
        db.MaintenanceTasks.Add(task);

        if (equipment.Status != EquipmentStatus.Maintenance)
        {
            equipment.Status = EquipmentStatus.Maintenance;
            equipment.UpdatedAt = now;
        }

        await db.SaveChangesAsync(ct);
        return await GetTaskDetailAsync(task.Id, ct: ct);
    }

    /// <summary>KTV tự tiếp nhận phiếu bảo trì chưa ai nhận (khóa dòng FOR UPDATE chống 2 KTV nhận trùng).</summary>
    public async Task<MaintenanceTaskDetailResponse> ClaimTaskAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var task = await db.MaintenanceTasks
            .FromSqlInterpolated($"SELECT * FROM maintenance_tasks WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("MAINTENANCE_TASK_NOT_FOUND", "Không tìm thấy phiếu bảo trì.", StatusCodes.Status404NotFound);

        if (task.Status is not (MaintenanceTaskStatus.Pending or MaintenanceTaskStatus.InProgress))
        {
            throw new AppException(
                "INVALID_STATUS",
                "Chỉ có thể tiếp nhận phiếu bảo trì đang mở.",
                StatusCodes.Status409Conflict);
        }

        if (task.AssignedTechnicianStaffId.HasValue && task.AssignedTechnicianStaffId.Value != staffId)
        {
            throw new AppException(
                "ALREADY_CLAIMED_BY_ANOTHER_TECHNICIAN",
                "Phiếu bảo trì này đã được Kỹ thuật viên khác tiếp nhận trước đó.",
                StatusCodes.Status409Conflict);
        }

        if (!task.AssignedTechnicianStaffId.HasValue)
        {
            task.AssignedTechnicianStaffId = staffId;
            task.UpdatedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(ct);
        }

        await transaction.CommitAsync(ct);
        return await GetTaskDetailAsync(id, currentUserId, isTechnicianOnly: true, ct: ct);
    }

    /// <summary>Manager chỉ định đích danh KTV phụ trách phiếu bảo trì gấp/khó.</summary>
    public async Task<MaintenanceTaskDetailResponse> AssignTaskByManagerAsync(
        Guid id,
        Guid technicianStaffId,
        CancellationToken ct = default)
    {
        var task = await db.MaintenanceTasks.FirstOrDefaultAsync(t => t.Id == id, ct)
            ?? throw new AppException("MAINTENANCE_TASK_NOT_FOUND", "Không tìm thấy phiếu bảo trì.", StatusCodes.Status404NotFound);

        if (task.Status is MaintenanceTaskStatus.Completed or MaintenanceTaskStatus.Cancelled)
        {
            throw new AppException("INVALID_STATUS", "Không thể phân công phiếu bảo trì đã đóng.", StatusCodes.Status409Conflict);
        }

        var staff = await db.Staff.AsNoTracking().FirstOrDefaultAsync(s => s.Id == technicianStaffId, ct)
            ?? throw new AppException("STAFF_NOT_FOUND", "Không tìm thấy hồ sơ kỹ thuật viên.", StatusCodes.Status404NotFound);

        task.AssignedTechnicianStaffId = staff.Id;
        task.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);

        var serial = await db.Equipments.AsNoTracking()
            .Where(e => e.Id == task.EquipmentId)
            .Select(e => e.SerialNumber)
            .FirstOrDefaultAsync(ct) ?? "";

        await notificationDispatcher.NotifyUsersAsync(
            [staff.UserId],
            $"Quản lý phân công bảo trì ({serial})",
            $"Bạn được Quản lý chỉ định phụ trách phiếu bảo trì thiết bị {serial}: {task.Description}",
            "MAINTENANCE_ASSIGNED",
            cancellationToken: ct);

        return await GetTaskDetailAsync(id, ct: ct);
    }

    public async Task<MaintenanceTaskDetailResponse> StartTaskAsync(
        Guid id,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var task = await db.MaintenanceTasks
            .FromSqlInterpolated($"SELECT * FROM maintenance_tasks WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("MAINTENANCE_TASK_NOT_FOUND", "Không tìm thấy phiếu bảo trì.", StatusCodes.Status404NotFound);

        if (task.AssignedTechnicianStaffId.HasValue && task.AssignedTechnicianStaffId.Value != staffId)
        {
            throw new AppException(
                "MAINTENANCE_OWNED_BY_ANOTHER_TECHNICIAN",
                "Phiếu bảo trì này đã được Kỹ thuật viên khác tiếp nhận phụ trách.",
                StatusCodes.Status403Forbidden);
        }

        if (task.Status != MaintenanceTaskStatus.Pending)
        {
            throw new AppException(
                "INVALID_STATUS",
                "Chỉ có thể bắt đầu phiếu bảo trì đang ở trạng thái chờ xử lý (PENDING).",
                StatusCodes.Status409Conflict);
        }

        var equipment = await db.Equipments.FirstOrDefaultAsync(e => e.Id == task.EquipmentId, ct)
            ?? throw new AppException("EQUIPMENT_NOT_FOUND", "Không tìm thấy thiết bị của phiếu bảo trì.", StatusCodes.Status404NotFound);

        if (equipment.Status is EquipmentStatus.Rented or EquipmentStatus.Reserved or EquipmentStatus.Disposed or EquipmentStatus.Lost)
        {
            throw new AppException(
                "EQUIPMENT_STATUS_CONFLICT",
                $"Thiết bị {equipment.SerialNumber} hiện đang {EnumName.ToDatabaseValue(equipment.Status)}, không thể bắt đầu bảo trì.",
                StatusCodes.Status409Conflict);
        }

        var now = DateTimeOffset.UtcNow;
        task.AssignedTechnicianStaffId ??= staffId;
        task.Status = MaintenanceTaskStatus.InProgress;
        task.StartedAt ??= now;
        task.UpdatedAt = now;

        if (equipment.Status != EquipmentStatus.Maintenance)
        {
            equipment.Status = EquipmentStatus.Maintenance;
            equipment.UpdatedAt = now;
        }

        if (task.ServiceAssignmentId.HasValue)
        {
            var assign = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == task.ServiceAssignmentId.Value, ct);
            if (assign is not null && assign.Status == ServiceAssignmentStatus.Pending)
            {
                assign.Status = ServiceAssignmentStatus.InProgress;
                assign.StartedAt ??= now;
                assign.UpdatedAt = now;
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return await GetTaskDetailAsync(id, currentUserId, isTechnicianOnly: true, ct: ct);
    }

    public async Task<CompleteMaintenanceTaskResponse> CompleteTaskAsync(
        Guid id,
        CompleteMaintenanceTaskRequest request,
        Guid currentUserId,
        CancellationToken ct = default)
    {
        var staffId = await GetCurrentStaffIdAsync(currentUserId, ct);
        await EnsureValidUploadedImagesAsync(request.AttachmentFileIds.ToHashSet(), currentUserId, ct);

        var outcome = request.Outcome.Trim().ToUpperInvariant();
        if (outcome is not ("AVAILABLE" or "DAMAGED" or "KEEP_MAINTENANCE"))
        {
            throw new AppException(
                "INVALID_OUTCOME",
                "Kết quả bảo trì phải là AVAILABLE (đưa về kho), DAMAGED (hỏng nặng) hoặc KEEP_MAINTENANCE (tiếp tục sửa chữa).",
                StatusCodes.Status400BadRequest);
        }

        await using var transaction = await db.Database.BeginTransactionAsync(ct);

        var task = await db.MaintenanceTasks
            .FromSqlInterpolated($"SELECT * FROM maintenance_tasks WHERE id = {id} FOR UPDATE")
            .FirstOrDefaultAsync(ct)
            ?? throw new AppException("MAINTENANCE_TASK_NOT_FOUND", "Không tìm thấy phiếu bảo trì.", StatusCodes.Status404NotFound);

        if (task.AssignedTechnicianStaffId.HasValue && task.AssignedTechnicianStaffId.Value != staffId)
        {
            throw new AppException(
                "MAINTENANCE_OWNED_BY_ANOTHER_TECHNICIAN",
                "Phiếu bảo trì này đã được Kỹ thuật viên khác tiếp nhận phụ trách, bạn không thể hoàn tất.",
                StatusCodes.Status403Forbidden);
        }

        task.AssignedTechnicianStaffId ??= staffId;

        if (task.Status is not (MaintenanceTaskStatus.Pending or MaintenanceTaskStatus.InProgress))
        {
            throw new AppException(
                "INVALID_STATUS",
                "Chỉ có thể hoàn tất phiếu bảo trì đang chờ xử lý hoặc đang thực hiện.",
                StatusCodes.Status409Conflict);
        }

        var equipment = await db.Equipments.FirstOrDefaultAsync(e => e.Id == task.EquipmentId, ct)
            ?? throw new AppException("EQUIPMENT_NOT_FOUND", "Không tìm thấy thiết bị.", StatusCodes.Status404NotFound);

        var now = DateTimeOffset.UtcNow;

        // 1. Lưu biên bản thực hiện (MaintenanceRecord) & linh kiện thay thế (MaintenanceParts)
        var record = new MaintenanceRecord
        {
            Id = Guid.CreateVersion7(),
            MaintenanceTaskId = task.Id,
            PerformedBy = staffId,
            Description = request.Description.Trim(),
            LaborCost = request.LaborCost,
            CompletedAt = now,
            Note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim(),
        };
        db.MaintenanceRecords.Add(record);

        var partsTotal = 0m;
        foreach (var part in request.Parts)
        {
            var lineTotal = Math.Round(part.Quantity * part.UnitCost, 2);
            partsTotal += lineTotal;
            db.MaintenanceParts.Add(new MaintenancePart
            {
                Id = Guid.CreateVersion7(),
                MaintenanceRecordId = record.Id,
                Name = part.Name.Trim(),
                Quantity = part.Quantity,
                UnitCost = part.UnitCost,
                TotalCost = lineTotal,
                Note = string.IsNullOrWhiteSpace(part.Note) ? null : part.Note.Trim(),
            });
        }

        // 2. Lưu ảnh minh chứng bảo trì
        var existingFileIds = (await db.MaintenanceTaskAttachments
            .Where(a => a.MaintenanceTaskId == task.Id)
            .Select(a => a.StorageFileId)
            .ToListAsync(ct)).ToHashSet();

        foreach (var fileId in request.AttachmentFileIds.Distinct())
        {
            if (existingFileIds.Add(fileId))
            {
                db.MaintenanceTaskAttachments.Add(new MaintenanceTaskAttachment
                {
                    Id = Guid.CreateVersion7(),
                    MaintenanceTaskId = task.Id,
                    StorageFileId = fileId,
                    CreatedAt = now,
                });
            }
        }

        // 3. Chốt trạng thái phiếu & thiết bị
        task.StartedAt ??= now;
        task.CompletedAt = now;
        task.Status = MaintenanceTaskStatus.Completed;
        task.Result = request.Result.Trim();
        task.UpdatedAt = now;

        Guid? followUpTaskId = null;
        string finalCondition;

        if (outcome == "AVAILABLE")
        {
            finalCondition = (request.NewCondition ?? "GOOD").Trim().ToUpperInvariant() switch
            {
                "FAIR" => "FAIR",
                _ => "GOOD",
            };
            equipment.Status = EquipmentStatus.Available;
            equipment.Condition = finalCondition;
        }
        else if (outcome == "DAMAGED")
        {
            finalCondition = "DAMAGED";
            equipment.Status = EquipmentStatus.Damaged;
            equipment.Condition = finalCondition;
        }
        else // KEEP_MAINTENANCE
        {
            finalCondition = (request.NewCondition ?? "DAMAGED").Trim().ToUpperInvariant() switch
            {
                "GOOD" => "GOOD",
                "FAIR" => "FAIR",
                _ => "DAMAGED",
            };
            equipment.Status = EquipmentStatus.Maintenance;
            equipment.Condition = finalCondition;

            var followUp = new MaintenanceTask
            {
                Id = Guid.CreateVersion7(),
                EquipmentId = equipment.Id,
                Type = "CORRECTIVE",
                Description = $"Sửa chữa tiếp theo (chuyển từ kiểm tra trước): {request.Result.Trim()}",
                ScheduledAt = now,
                Status = MaintenanceTaskStatus.Pending,
                CreatedAt = now,
                UpdatedAt = now,
            };
            db.MaintenanceTasks.Add(followUp);
            followUpTaskId = followUp.Id;
        }

        equipment.UpdatedAt = now;

        // 3b. Bước 1 quy trình sự cố: nếu máy này đang thuộc một sự cố RETURN_REQUIRED chưa chốt trách nhiệm
        //     -> lưu nguyên nhân (TECHNICAL_FAULT / CUSTOMER_FAULT) và tạo đề xuất incident_charges (PENDING_APPROVAL) nếu lỗi do khách.
        var incidentNotifyAction = (Type: "", ContractManagerId: (Guid?)null, IncidentCode: "", ProposedAmount: 0m, ContractCode: "");

        var linkedIncidentItem = await (
            from ii in db.EquipmentIncidentItems
            join inc in db.EquipmentIncidents on ii.IncidentId equals inc.Id
            where ii.EquipmentId == equipment.Id
                  && inc.Status == EquipmentIncidentStatus.ReturnRequired
            orderby inc.ReportedAt descending
            select new { Item = ii, Incident = inc }
        ).FirstOrDefaultAsync(ct);

        if (linkedIncidentItem is not null && !string.IsNullOrWhiteSpace(request.IncidentFaultType))
        {
            var faultType = request.IncidentFaultType.Trim().ToUpperInvariant();
            if (faultType is not ("TECHNICAL_FAULT" or "CUSTOMER_FAULT"))
            {
                throw new AppException(
                    "INVALID_FAULT_TYPE",
                    "Nguyên nhân lỗi sự cố phải là TECHNICAL_FAULT (lỗi kỹ thuật/hao mòn) hoặc CUSTOMER_FAULT (lỗi do khách hàng).",
                    StatusCodes.Status400BadRequest);
            }

            linkedIncidentItem.Item.FaultType = faultType;
            linkedIncidentItem.Item.Assessment = string.IsNullOrWhiteSpace(linkedIncidentItem.Item.Assessment)
                ? request.Result.Trim()
                : $"{linkedIncidentItem.Item.Assessment} | Kiểm tra kho: {request.Result.Trim()}";

            var contractInfo = await (
                from hn in db.HandoverNotes.AsNoTracking()
                join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
                join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
                where hi.EquipmentId == equipment.Id && c.CompanyId == linkedIncidentItem.Incident.CompanyId
                orderby hn.CreatedAt descending
                select new { c.Id, c.ContractCode, c.ApprovedBy }
            ).FirstOrDefaultAsync(ct);

            if (faultType == "TECHNICAL_FAULT")
            {
                // Hủy các đề xuất phí cũ (nếu có) và mở khóa giao máy thay thế miễn phí ship ngay
                var oldPendingProposals = await db.IncidentCharges
                    .Where(ic => ic.IncidentItemId == linkedIncidentItem.Item.Id
                                 && ic.Status == IncidentChargeStatus.PendingApproval)
                    .ToListAsync(ct);
                foreach (var p in oldPendingProposals)
                    p.Status = IncidentChargeStatus.Cancelled;

                incidentNotifyAction = ("TECHNICAL_FAULT", contractInfo?.ApprovedBy, linkedIncidentItem.Incident.IncidentCode, 0m, contractInfo?.ContractCode ?? "");
            }
            else // CUSTOMER_FAULT
            {
                var proposedAmount = request.ProposedChargeAmount ?? (request.LaborCost + partsTotal);
                if (proposedAmount <= 0)
                {
                    throw new AppException(
                        "PROPOSED_CHARGE_REQUIRED",
                        "Khi xác định lỗi do khách hàng (CUSTOMER_FAULT), vui lòng nhập chi phí nhân công/linh kiện hoặc số tiền đề xuất bồi thường lớn hơn 0 đ.",
                        StatusCodes.Status400BadRequest);
                }

                var chargeType = (request.ProposedChargeType ?? (outcome == "DAMAGED" ? "REPLACEMENT" : "REPAIR"))
                    .Trim().ToUpperInvariant();
                if (chargeType is not ("REPAIR" or "REPLACEMENT" or "OTHER"))
                    chargeType = "REPAIR";

                var existingProposal = await db.IncidentCharges
                    .FirstOrDefaultAsync(ic => ic.IncidentItemId == linkedIncidentItem.Item.Id
                                               && ic.Status == IncidentChargeStatus.PendingApproval, ct);

                var desc = !string.IsNullOrWhiteSpace(request.ProposedChargeDescription)
                    ? request.ProposedChargeDescription.Trim()
                    : $"Đề xuất phí {(chargeType == "REPLACEMENT" ? "bồi thường thiết bị" : "sửa chữa sự cố")} {linkedIncidentItem.Incident.IncidentCode} - {equipment.SerialNumber}: {request.Result.Trim()}";

                if (existingProposal is not null)
                {
                    existingProposal.ChargeType = chargeType;
                    existingProposal.Amount = proposedAmount;
                    existingProposal.Description = desc;
                    existingProposal.ProposedBy = currentUserId;
                }
                else
                {
                    db.IncidentCharges.Add(new IncidentCharge
                    {
                        Id = Guid.CreateVersion7(),
                        IncidentItemId = linkedIncidentItem.Item.Id,
                        ChargeType = chargeType,
                        Description = desc,
                        Amount = proposedAmount,
                        Status = IncidentChargeStatus.PendingApproval,
                        ProposedBy = currentUserId,
                        CreatedAt = now,
                    });
                }

                incidentNotifyAction = ("CUSTOMER_FAULT", contractInfo?.ApprovedBy, linkedIncidentItem.Incident.IncidentCode, proposedAmount, contractInfo?.ContractCode ?? "");
            }
        }

        // Đóng ServiceAssignment liên kết (nếu có và không còn task bảo trì mở nào khác)
        if (task.ServiceAssignmentId.HasValue)
        {
            var hasOtherOpen = await db.MaintenanceTasks.AsNoTracking()
                .AnyAsync(t => t.ServiceAssignmentId == task.ServiceAssignmentId.Value
                               && t.Id != task.Id
                               && t.Status != MaintenanceTaskStatus.Completed
                               && t.Status != MaintenanceTaskStatus.Cancelled, ct);
            if (!hasOtherOpen)
            {
                var assign = await db.ServiceAssignments.FirstOrDefaultAsync(a => a.Id == task.ServiceAssignmentId.Value, ct);
                if (assign is not null && assign.Status != ServiceAssignmentStatus.Completed)
                {
                    assign.Status = ServiceAssignmentStatus.Completed;
                    assign.CompletedAt = now;
                    assign.UpdatedAt = now;
                }
            }
        }

        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);

        // Thông báo cho Thủ kho trưởng & Quản lý khi máy sẵn sàng lại hoặc bị đánh dấu hỏng nặng
        if (outcome is "AVAILABLE" or "DAMAGED")
        {
            var notifyRoleCodes = outcome == "DAMAGED"
                ? new[] { "MANAGER", "LEAD_STOREKEEPER" }
                : new[] { "LEAD_STOREKEEPER" };

            var recipientIds = await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where notifyRoleCodes.Contains(r.Code)
                select ur.UserId
            ).Distinct().ToListAsync(ct);

            if (recipientIds.Count > 0)
            {
                var title = outcome == "AVAILABLE"
                    ? $"Hoàn tất bảo trì — Máy sẵn sàng ({equipment.SerialNumber})"
                    : $"Thiết bị hỏng nặng sau kiểm tra ({equipment.SerialNumber})";
                var content = outcome == "AVAILABLE"
                    ? $"KTV đã hoàn tất bảo trì máy {equipment.SerialNumber} (tình trạng {finalCondition}). Máy đã quay về trạng thái Sẵn sàng cho thuê (AVAILABLE)."
                    : $"KTV kết luận máy {equipment.SerialNumber} hỏng nặng không thể sửa tại chỗ ({request.Result.Trim()}). Máy đã chuyển sang trạng thái DAMAGED để Kho/Quản lý xử lý.";

                await notificationDispatcher.NotifyUsersAsync(
                    recipientIds,
                    title,
                    content,
                    outcome == "AVAILABLE" ? "MAINTENANCE_COMPLETED" : "EQUIPMENT_DAMAGED",
                    cancellationToken: ct);
            }
        }

        // Thông báo liên quan đến phân định trách nhiệm sự cố (Bước 1 → Bước 2/3)
        if (incidentNotifyAction.Type == "TECHNICAL_FAULT")
        {
            var notifyIds = await (
                from ur in db.UserRoles.AsNoTracking()
                join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                where r.Code == "LEAD_STOREKEEPER" || r.Code == "STOREKEEPER"
                select ur.UserId
            ).Distinct().ToListAsync(ct);
            if (incidentNotifyAction.ContractManagerId.HasValue)
                notifyIds.Add(incidentNotifyAction.ContractManagerId.Value);

            if (notifyIds.Count > 0)
            {
                await notificationDispatcher.NotifyUsersAsync(
                    notifyIds.Distinct(),
                    $"Lỗi kỹ thuật — Mở giao thay thế miễn phí ({incidentNotifyAction.IncidentCode})",
                    $"KTV kết luận máy {equipment.SerialNumber} ({incidentNotifyAction.IncidentCode}) hỏng do lỗi kỹ thuật/hao mòn tự nhiên (0 đ phí khách). Kho và Quản lý đã có thể lập phiếu & xếp lịch giao máy thay thế (miễn phí ship).",
                    "INCIDENT_REPLACEMENT_READY",
                    cancellationToken: ct);
            }
        }
        else if (incidentNotifyAction.Type == "CUSTOMER_FAULT")
        {
            var mgrIds = new List<Guid>();
            if (incidentNotifyAction.ContractManagerId.HasValue)
            {
                mgrIds.Add(incidentNotifyAction.ContractManagerId.Value);
            }
            else
            {
                mgrIds.AddRange(await (
                    from ur in db.UserRoles.AsNoTracking()
                    join r in db.Roles.AsNoTracking() on ur.RoleId equals r.Id
                    where r.Code == "MANAGER"
                    select ur.UserId
                ).Distinct().ToListAsync(ct));
            }

            if (mgrIds.Count > 0)
            {
                await notificationDispatcher.NotifyUsersAsync(
                    mgrIds.Distinct(),
                    $"Đề xuất phạt sự cố chờ duyệt ({incidentNotifyAction.IncidentCode})",
                    $"KTV đánh giá máy {equipment.SerialNumber} ({incidentNotifyAction.ContractCode}) hư do lỗi khách hàng và đề xuất khoản phí {incidentNotifyAction.ProposedAmount:N0} đ. Vui lòng vào mục Duyệt yêu cầu để xét duyệt & xử lý cấn trừ cọc.",
                    "INCIDENT_CHARGE_PENDING_APPROVAL",
                    cancellationToken: ct);
            }
        }

        var totalCost = request.LaborCost + partsTotal;
        var msg = outcome switch
        {
            "AVAILABLE" => $"Đã hoàn tất bảo trì máy {equipment.SerialNumber} và đưa về trạng thái Sẵn sàng (AVAILABLE).",
            "DAMAGED" => $"Đã chốt kết quả máy {equipment.SerialNumber} hỏng nặng (DAMAGED) và gửi thông báo cho Kho/Quản lý.",
            _ => $"Đã lưu nhật ký kiểm tra và mở phiếu sửa chữa tiếp theo cho máy {equipment.SerialNumber}.",
        };

        return new CompleteMaintenanceTaskResponse(
            task.Id,
            equipment.Id,
            equipment.SerialNumber,
            EnumName.ToDatabaseValue(equipment.Status),
            equipment.Condition,
            totalCost,
            followUpTaskId,
            msg);
    }

    private async Task<MaintenanceIncidentContextDto?> GetLinkedIncidentContextAsync(
        Guid equipmentId,
        CancellationToken ct)
    {
        var row = await (
            from ii in db.EquipmentIncidentItems.AsNoTracking()
            join inc in db.EquipmentIncidents.AsNoTracking() on ii.IncidentId equals inc.Id
            join co in db.Companies.AsNoTracking() on inc.CompanyId equals co.Id
            where ii.EquipmentId == equipmentId
                  && inc.Status == EquipmentIncidentStatus.ReturnRequired
            orderby inc.ReportedAt descending
            select new { ii, inc, CompanyName = co.Name }
        ).FirstOrDefaultAsync(ct);

        if (row is null) return null;

        var contractInfo = await (
            from hn in db.HandoverNotes.AsNoTracking()
            join hi in db.HandoverItems.AsNoTracking() on hn.Id equals hi.HandoverNoteId
            join c in db.RentalContracts.AsNoTracking() on hn.RentalContractId equals c.Id
            where hi.EquipmentId == equipmentId && c.CompanyId == row.inc.CompanyId
            orderby hn.CreatedAt descending
            select new { c.Id, c.ContractCode }
        ).FirstOrDefaultAsync(ct);

        var latestCharge = await db.IncidentCharges.AsNoTracking()
            .Where(ic => ic.IncidentItemId == row.ii.Id && ic.Status != IncidentChargeStatus.Cancelled)
            .OrderByDescending(ic => ic.CreatedAt)
            .FirstOrDefaultAsync(ct);

        return new MaintenanceIncidentContextDto(
            row.inc.Id,
            row.inc.IncidentCode,
            row.ii.Id,
            contractInfo?.Id,
            contractInfo?.ContractCode,
            row.CompanyName,
            row.inc.Description,
            row.ii.FaultType,
            latestCharge is null ? null : EnumName.ToDatabaseValue(latestCharge.Status),
            latestCharge?.Amount);
    }

    private async Task<Guid> GetCurrentStaffIdAsync(Guid userId, CancellationToken ct)
    {
        var staffId = await db.Staff.AsNoTracking()
            .Where(s => s.UserId == userId)
            .Select(s => (Guid?)s.Id)
            .FirstOrDefaultAsync(ct);

        return staffId ?? throw new AppException(
            "STAFF_PROFILE_NOT_FOUND",
            "Tài khoản chưa có hồ sơ nhân viên (Staff).",
            StatusCodes.Status403Forbidden);
    }

    private async Task EnsureValidUploadedImagesAsync(HashSet<Guid> fileIds, Guid currentUserId, CancellationToken ct)
    {
        if (fileIds.Count == 0) return;

        var files = await db.StorageFiles.AsNoTracking()
            .Where(f => fileIds.Contains(f.Id))
            .Select(f => new { f.Id, f.UploadedBy, f.ContentType })
            .ToListAsync(ct);

        if (files.Count != fileIds.Count || files.Any(f => f.UploadedBy != currentUserId))
            throw new AppException("ATTACHMENT_INVALID", "Ảnh minh chứng không hợp lệ hoặc không phải do bạn tải lên.", StatusCodes.Status400BadRequest);

        if (files.Any(f => !AllowedImageFormats.Contains((f.ContentType ?? "").ToLowerInvariant())))
            throw new AppException("ATTACHMENT_FORMAT_INVALID", "Ảnh minh chứng phải có định dạng jpg, jpeg, png hoặc webp.", StatusCodes.Status400BadRequest);
    }

    private static MaintenanceTaskStatus ParseTaskStatus(string status)
    {
        try
        {
            return EnumName.FromDatabaseValue<MaintenanceTaskStatus>(status.Trim().ToUpperInvariant());
        }
        catch (ArgumentException)
        {
            throw new AppException("INVALID_STATUS", "Trạng thái phiếu bảo trì không hợp lệ.", StatusCodes.Status400BadRequest);
        }
    }
}
