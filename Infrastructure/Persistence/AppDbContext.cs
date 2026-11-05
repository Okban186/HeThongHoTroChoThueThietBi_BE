using HeThongHoTroChoThueThietBi.Common.Persistence;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using HeThongHoTroChoThueThietBi.Features.Identity.Entities;
using HeThongHoTroChoThueThietBi.Features.Organization.Entities;
using HeThongHoTroChoThueThietBi.Features.Equipment.Entities;
using HeThongHoTroChoThueThietBi.Features.Rental.Entities;
using HeThongHoTroChoThueThietBi.Features.Operations.Entities;
using HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;
using HeThongHoTroChoThueThietBi.Features.Finance.Entities;
using HeThongHoTroChoThueThietBi.Features.Notification.Entities;
using HeThongHoTroChoThueThietBi.Storage.Entities;
using Microsoft.EntityFrameworkCore;
using HeThongHoTroChoThueThietBi.Common.FileStorage.Entities;
using HeThongHoTroChoThueThietBi.Features.Notifications;

namespace HeThongHoTroChoThueThietBi.Infrastructure.Persistence;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Role> Roles => Set<Role>();
    public DbSet<UserRole> UserRoles => Set<UserRole>();
    public DbSet<Staff> Staff => Set<Staff>();
    public DbSet<Company> Companies => Set<Company>();
    public DbSet<CompanyUser> CompanyUsers => Set<CompanyUser>();
    public DbSet<Notification> Notifications => Set<Notification>();
    public DbSet<StorageFile> StorageFiles => Set<StorageFile>();
    public DbSet<Warehouse> Warehouses => Set<Warehouse>();
    public DbSet<Category> Categories => Set<Category>();
    public DbSet<EquipmentModel> EquipmentModels => Set<EquipmentModel>();
    public DbSet<EquipmentModelMedia> EquipmentModelMedia => Set<EquipmentModelMedia>();
    public DbSet<EquipmentModelCategory> EquipmentModelCategories => Set<EquipmentModelCategory>();
    public DbSet<Equipment> Equipments => Set<Equipment>();
    public DbSet<Reservation> Reservations => Set<Reservation>();
    public DbSet<ReservationItem> ReservationItems => Set<ReservationItem>();
    public DbSet<RentalContract> RentalContracts => Set<RentalContract>();
    public DbSet<RentalContractItem> RentalContractItems => Set<RentalContractItem>();
    public DbSet<RentalContractService> RentalContractServices => Set<RentalContractService>();
    public DbSet<ContractStatusHistory> ContractStatusHistory => Set<ContractStatusHistory>();
    public DbSet<ServiceAssignment> ServiceAssignments => Set<ServiceAssignment>();
    public DbSet<ServiceAssignmentMember> ServiceAssignmentMembers => Set<ServiceAssignmentMember>();
    public DbSet<HandoverNote> HandoverNotes => Set<HandoverNote>();
    public DbSet<HandoverItem> HandoverItems => Set<HandoverItem>();
    public DbSet<HandoverIssue> HandoverIssues => Set<HandoverIssue>();
    public DbSet<HandoverIssueItem> HandoverIssueItems => Set<HandoverIssueItem>();
    public DbSet<EquipmentReturn> EquipmentReturns => Set<EquipmentReturn>();
    public DbSet<EquipmentReturnItem> EquipmentReturnItems => Set<EquipmentReturnItem>();
    public DbSet<RentalCharge> RentalCharges => Set<RentalCharge>();
    public DbSet<EquipmentIncident> EquipmentIncidents => Set<EquipmentIncident>();
    public DbSet<EquipmentIncidentItem> EquipmentIncidentItems => Set<EquipmentIncidentItem>();
    public DbSet<IncidentCharge> IncidentCharges => Set<IncidentCharge>();
    public DbSet<ReplacementRequest> ReplacementRequests => Set<ReplacementRequest>();
    public DbSet<ReplacementRequestItem> ReplacementRequestItems => Set<ReplacementRequestItem>();
    public DbSet<MaintenanceTask> MaintenanceTasks => Set<MaintenanceTask>();
    public DbSet<MaintenanceRecord> MaintenanceRecords => Set<MaintenanceRecord>();
    public DbSet<MaintenancePart> MaintenanceParts => Set<MaintenancePart>();
    public DbSet<EquipmentDisposal> EquipmentDisposals => Set<EquipmentDisposal>();
    public DbSet<RentalContractRepresentative> RentalContractRepresentatives => Set<RentalContractRepresentative>();
    public DbSet<RentalContractDocument> RentalContractDocuments => Set<RentalContractDocument>();
    public DbSet<ServiceAssignmentAttachment> ServiceAssignmentAttachments => Set<ServiceAssignmentAttachment>();
    public DbSet<HandoverNoteAttachment> HandoverNoteAttachments => Set<HandoverNoteAttachment>();
    public DbSet<HandoverIssueAttachment> HandoverIssueAttachments => Set<HandoverIssueAttachment>();
    public DbSet<EquipmentIncidentAttachment> EquipmentIncidentAttachments => Set<EquipmentIncidentAttachment>();
    public DbSet<MaintenanceTaskAttachment> MaintenanceTaskAttachments => Set<MaintenanceTaskAttachment>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<RevokedToken> RevokedTokens => Set<RevokedToken>();

    public DbSet<UploadSession> UploadSessions => Set<UploadSession>();

    public DbSet<UserDevice> UserDevices => Set<UserDevice>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.Username).HasColumnType("varchar(100)").IsRequired();
            entity.Property(x => x.PasswordHash).HasColumnType("text").IsRequired();
            entity.Property(x => x.FullName).HasColumnType("varchar(150)").IsRequired();
            entity.Property(x => x.Email).HasColumnType("varchar(255)");
            entity.Property(x => x.Phone).HasColumnType("varchar(20)");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'ACTIVE'").IsRequired();
            entity.Property(x => x.LastLoginAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();

            entity.HasIndex(x => x.Username).IsUnique();

            entity.Property(x => x.AvatarStorageFileId).HasColumnName("avatar_storage_file_id");

            entity.HasIndex(x => x.AvatarStorageFileId);

            entity.HasOne(x => x.AvatarStorageFile)
                .WithMany()
                .HasForeignKey(x => x.AvatarStorageFileId)
                .OnDelete(DeleteBehavior.SetNull);




        });
        modelBuilder.Entity<Role>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.Code).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.Name).HasColumnType("varchar(100)").IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.HasIndex(x => x.Code).IsUnique();
        });
        modelBuilder.Entity<User>()
      .HasMany(u => u.Roles)
      .WithMany()
      .UsingEntity<UserRole>(
          right => right
              .HasOne<Role>()
              .WithMany()
              .HasForeignKey(x => x.RoleId),

          left => left
              .HasOne<User>()
              .WithMany()
              .HasForeignKey(x => x.UserId),

          join =>
          {
              join.ToTable("user_roles");

              join.HasKey(x => new { x.UserId, x.RoleId });

              join.Property(x => x.AssignedAt)
                  .HasColumnType("timestamp with time zone")
                  .HasDefaultValueSql("NOW()")
                  .IsRequired();
          });
        modelBuilder.Entity<Staff>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.UserId).IsRequired();
            entity.Property(x => x.EmployeeCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.Position).HasColumnType("varchar(100)");
            entity.Property(x => x.HireDate);
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'ACTIVE'").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.UserId).IsUnique();
            entity.HasIndex(x => x.EmployeeCode).IsUnique();
            entity.HasOne<User>().WithOne(u => u.Staff).HasForeignKey<Staff>(s => s.UserId);
        });
        modelBuilder.Entity<Company>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.CompanyCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.Name).HasColumnType("varchar(255)").IsRequired();
            entity.Property(x => x.TaxCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.Address).HasColumnType("text");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'ACTIVE'").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.CompanyCode).IsUnique();
            entity.HasIndex(x => x.TaxCode).IsUnique();
        });
        modelBuilder.Entity<CompanyUser>(entity =>
        {
            entity.HasKey(x => x.UserId);
            entity.Property(x => x.UserId).IsRequired();
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            entity.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        });
        modelBuilder.Entity<Notification>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.UserId).IsRequired();
            entity.Property(x => x.Title).HasColumnType("varchar(255)").IsRequired();
            entity.Property(x => x.Content).HasColumnType("text").IsRequired();
            entity.Property(x => x.Type).HasColumnType("varchar(50)");
            entity.Property(x => x.IsRead).HasDefaultValueSql("FALSE").IsRequired();
            entity.Property(x => x.ReadAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });
        modelBuilder.Entity<StorageFile>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ObjectKey).HasColumnType("text").IsRequired();
            entity.Property(x => x.BucketName).HasColumnType("varchar(100)").IsRequired();
            entity.Property(x => x.OriginalName).HasColumnType("varchar(255)").IsRequired();
            entity.Property(x => x.ContentType).HasColumnType("varchar(150)");
            entity.Property(x => x.SizeBytes).IsRequired();
            entity.Property(x => x.ResourceType)
            .HasColumnName("resource_type")
            .HasConversion<string>()
            .HasMaxLength(20)
            .IsRequired();
            entity.Property(x => x.Metadata).HasColumnType("jsonb");
            entity.Property(x => x.UploadedBy);
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.ObjectKey).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UploadedBy);
        });
        modelBuilder.Entity<Warehouse>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.Code).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.Name).HasColumnType("varchar(150)").IsRequired();
            entity.Property(x => x.Address).HasColumnType("text");
            entity.Property(x => x.Phone).HasColumnType("varchar(20)");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'ACTIVE'").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });
        modelBuilder.Entity<Category>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ParentId);
            entity.Property(x => x.Code).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.Name).HasColumnType("varchar(150)").IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'ACTIVE'").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasOne<Category>().WithMany().HasForeignKey(x => x.ParentId);
        });
        modelBuilder.Entity<EquipmentModel>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.Code).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.Name).HasColumnType("varchar(255)").IsRequired();
            entity.Property(x => x.Manufacturer).HasColumnType("varchar(150)");
            entity.Property(x => x.ModelNumber).HasColumnType("varchar(100)");
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.EstimatedInstallationMinutes).HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Specifications).HasColumnType("jsonb");
            entity.Property(x => x.Unit).HasColumnType("varchar(30)").HasDefaultValueSql("'UNIT'").IsRequired();
            entity.Property(x => x.RentalPriceDaily).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.RentalPriceMonthly).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'ACTIVE'").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.Code).IsUnique();
        });
        modelBuilder.Entity<EquipmentModelMedia>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.EquipmentModelId).IsRequired();
            entity.Property(x => x.StorageFileId).IsRequired();
            entity.Property(x => x.MediaType).HasColumnType("varchar(20)").HasDefaultValueSql("'IMAGE'").IsRequired();
            entity.Property(x => x.DisplayOrder).HasDefaultValueSql("1").IsRequired();
            entity.Property(x => x.IsPrimary).HasDefaultValueSql("FALSE").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => new { x.EquipmentModelId, x.StorageFileId }).IsUnique();
            entity.HasOne<EquipmentModel>().WithMany().HasForeignKey(x => x.EquipmentModelId);
            entity.HasOne<StorageFile>().WithMany().HasForeignKey(x => x.StorageFileId);
        });
        modelBuilder.Entity<EquipmentModelCategory>(entity =>
        {
            entity.HasKey(x => new { x.EquipmentModelId, x.CategoryId });
            entity.Property(x => x.EquipmentModelId).IsRequired();
            entity.Property(x => x.CategoryId).IsRequired();
            entity.HasOne<EquipmentModel>().WithMany().HasForeignKey(x => x.EquipmentModelId);
            entity.HasOne<Category>().WithMany().HasForeignKey(x => x.CategoryId);
        });
        modelBuilder.Entity<Equipment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.EquipmentModelId).IsRequired();
            entity.Property(x => x.WarehouseId).IsRequired();
            entity.Property(x => x.SerialNumber).HasColumnType("varchar(100)").IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'AVAILABLE'").IsRequired();
            entity.Property(x => x.Condition).HasColumnType("varchar(20)").HasDefaultValueSql("'GOOD'").IsRequired();
            entity.Property(x => x.AcquiredAt);
            entity.Property(x => x.AcquisitionCost).HasColumnType("numeric(18,2)");
            entity.Property(x => x.Note).HasColumnType("text");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.SerialNumber).IsUnique();
            entity.HasOne<EquipmentModel>().WithMany().HasForeignKey(x => x.EquipmentModelId);
            entity.HasOne<Warehouse>().WithMany().HasForeignKey(x => x.WarehouseId);
        });
        modelBuilder.Entity<Reservation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ReservationCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.RequestedFrom).IsRequired();
            entity.Property(x => x.RequestedTo).IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'PENDING'").IsRequired();
            entity.Property(x => x.CreatedBy).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.ReservationCode).IsUnique();
            entity.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy);
        });
        modelBuilder.Entity<ReservationItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ReservationId).IsRequired();
            entity.Property(x => x.EquipmentModelId).IsRequired();
            entity.Property(x => x.Quantity).IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId);
            entity.HasOne<EquipmentModel>().WithMany().HasForeignKey(x => x.EquipmentModelId);
        });
        modelBuilder.Entity<RentalContract>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ContractCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.ReservationId);
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.StartDate).IsRequired();
            entity.Property(x => x.EndDate).IsRequired();
            entity.Property(x => x.RentalType).HasColumnType("varchar(20)").IsRequired();
            entity.Property(x => x.DeliveryAddress).HasColumnType("text");
            entity.Property(x => x.DeliveryNote).HasColumnType("text");
            entity.Property(x => x.ContractTotalAmount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.DepositRequiredAmount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.DepositPaidAmount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.DepositAppliedAmount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.DepositRefundedAmount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.ContractFileId);
            entity.Property(x => x.SignedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.EffectiveFrom);
            entity.Property(x => x.EffectiveTo);
            entity.Property(x => x.Status).HasColumnType("varchar(30)").HasDefaultValueSql("'DRAFT'").IsRequired();
            entity.Property(x => x.CreatedBy).IsRequired();
            entity.Property(x => x.ApprovedBy);
            entity.Property(x => x.ApprovedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.RejectionReason).HasColumnType("text");
            entity.Property(x => x.CancellationReason).HasColumnType("text");
            entity.Property(x => x.CancellationRequestedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CancellationRequestedBy);
            entity.Property(x => x.CancelledAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CancelledBy);
            entity.Property(x => x.Note).HasColumnType("text");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.ContractCode).IsUnique();
            entity.HasIndex(x => x.ReservationId).IsUnique();
            entity.HasOne<Reservation>().WithMany().HasForeignKey(x => x.ReservationId);
            entity.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
            entity.HasOne<StorageFile>().WithMany().HasForeignKey(x => x.ContractFileId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedBy);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CancellationRequestedBy);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CancelledBy);
        });
        modelBuilder.Entity<RentalContractItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.RentalContractId).IsRequired();
            entity.Property(x => x.EquipmentModelId).IsRequired();
            entity.Property(x => x.Quantity).IsRequired();
            entity.Property(x => x.BillingUnit).HasColumnType("varchar(20)").IsRequired();
            entity.Property(x => x.AgreedUnitPrice).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
            entity.HasOne<EquipmentModel>().WithMany().HasForeignKey(x => x.EquipmentModelId);
        });
        modelBuilder.Entity<RentalContractService>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.RentalContractId).IsRequired();
            entity.Property(x => x.ServiceType).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.ProvidedBy).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.Quantity).HasDefaultValueSql("1").IsRequired();
            entity.Property(x => x.UnitPrice).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Amount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
        });
        modelBuilder.Entity<ContractStatusHistory>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.RentalContractId).IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.PreviousStatus).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.ChangedBy).IsRequired();
            entity.Property(x => x.ChangedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.ChangedBy);
        });
        modelBuilder.Entity<ServiceAssignment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.Type).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.Title).HasColumnType("varchar(255)").IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'PENDING'").IsRequired();
            entity.Property(x => x.AssignedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ScheduledStartAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.EstimatedDurationMinutes);
            entity.Property(x => x.StartedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CreatedBy).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy);
        });
        modelBuilder.Entity<ServiceAssignmentMember>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ServiceAssignmentId).IsRequired();
            entity.Property(x => x.StaffId).IsRequired();
            entity.Property(x => x.Role).HasColumnType("varchar(20)").IsRequired();
            entity.HasIndex(x => new { x.ServiceAssignmentId, x.StaffId }).IsUnique();
            entity.HasOne<ServiceAssignment>().WithMany().HasForeignKey(x => x.ServiceAssignmentId);
            entity.HasOne<Staff>().WithMany().HasForeignKey(x => x.StaffId);
        });
        modelBuilder.Entity<HandoverNote>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.HandoverCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.RentalContractId).IsRequired();
            entity.Property(x => x.ServiceAssignmentId);
            entity.Property(x => x.HandoverDate).IsRequired();
            entity.Property(x => x.ReplacementRequestId);
            entity.Property(x => x.DeliveryAttemptNumber).HasDefaultValueSql("1").IsRequired();
            entity.Property(x => x.FailedReason).HasColumnType("text");
            entity.Property(x => x.FailedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ReceivedByName).HasColumnType("varchar(150)").IsRequired();
            entity.Property(x => x.ReceivedByPhone).HasColumnType("varchar(20)");
            entity.Property(x => x.Note).HasColumnType("text");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'DRAFT'").IsRequired();
            entity.Property(x => x.CreatedBy).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.HandoverCode).IsUnique();
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
            entity.HasOne<ServiceAssignment>().WithMany().HasForeignKey(x => x.ServiceAssignmentId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy);
            entity.HasOne<ReplacementRequest>().WithMany().HasForeignKey(x => x.ReplacementRequestId);
        });
        modelBuilder.Entity<HandoverItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.HandoverNoteId).IsRequired();
            entity.Property(x => x.EquipmentId).IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasIndex(x => new { x.HandoverNoteId, x.EquipmentId }).IsUnique();
            entity.HasOne<HandoverNote>().WithMany().HasForeignKey(x => x.HandoverNoteId);
            entity.HasOne<Equipment>().WithMany().HasForeignKey(x => x.EquipmentId);
        });
        modelBuilder.Entity<HandoverIssue>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.HandoverNoteId).IsRequired();
            entity.Property(x => x.Description).HasColumnType("text").IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'OPEN'").IsRequired();
            entity.Property(x => x.CreatedBy).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.ResolvedAt).HasColumnType("timestamp with time zone");
            entity.HasOne<HandoverNote>().WithMany().HasForeignKey(x => x.HandoverNoteId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy);
        });
        modelBuilder.Entity<HandoverIssueItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.HandoverIssueId).IsRequired();
            entity.Property(x => x.EquipmentId).IsRequired();
            entity.Property(x => x.IssueType).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'OPEN'").IsRequired();
            entity.HasOne<HandoverIssue>().WithMany().HasForeignKey(x => x.HandoverIssueId);
            entity.HasOne<Equipment>().WithMany().HasForeignKey(x => x.EquipmentId);
        });
        modelBuilder.Entity<EquipmentReturn>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ReturnCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.RentalContractId).IsRequired();
            entity.Property(x => x.ReturnedAt).HasColumnType("timestamp with time zone").IsRequired();
            entity.Property(x => x.ReceivedBy).IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'DRAFT'").IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.ReturnCode).IsUnique();
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.ReceivedBy);
        });
        modelBuilder.Entity<EquipmentReturnItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.EquipmentReturnId).IsRequired();
            entity.Property(x => x.EquipmentId).IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'RETURNED'").IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasIndex(x => new { x.EquipmentReturnId, x.EquipmentId }).IsUnique();
            entity.HasOne<EquipmentReturn>().WithMany().HasForeignKey(x => x.EquipmentReturnId);
            entity.HasOne<Equipment>().WithMany().HasForeignKey(x => x.EquipmentId);
        });
        modelBuilder.Entity<RentalCharge>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.SourceType).HasColumnType("varchar(20)").HasDefaultValueSql("'CONTRACT'").IsRequired();
            entity.Property(x => x.RentalContractId);
            entity.Property(x => x.EquipmentId);
            entity.Property(x => x.ChargeType).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.Quantity).HasColumnType("numeric(18,2)").HasDefaultValueSql("1").IsRequired();
            entity.Property(x => x.UnitPrice).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Amount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.PeriodStart);
            entity.Property(x => x.PeriodEnd);
            entity.Property(x => x.DueDate);
            entity.Property(x => x.PaidAmount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'UNPAID'").IsRequired();
            entity.Property(x => x.CreatedBy).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.PaidAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
            entity.HasOne<Equipment>().WithMany().HasForeignKey(x => x.EquipmentId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.CreatedBy);
        });
        modelBuilder.Entity<EquipmentIncident>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.IncidentCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.CompanyId).IsRequired();
            entity.Property(x => x.Description).HasColumnType("text").IsRequired();
            entity.Property(x => x.ReportedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.ResolvedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.Status).HasColumnType("varchar(30)").HasDefaultValueSql("'OPEN'").IsRequired();
            entity.Property(x => x.Resolution).HasColumnType("text");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.IncidentCode).IsUnique();
            entity.HasOne<Company>().WithMany().HasForeignKey(x => x.CompanyId);
        });
        modelBuilder.Entity<EquipmentIncidentItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.IncidentId).IsRequired();
            entity.Property(x => x.EquipmentId).IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.Assessment).HasColumnType("text");
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasOne<EquipmentIncident>().WithMany().HasForeignKey(x => x.IncidentId);
            entity.HasOne<Equipment>().WithMany().HasForeignKey(x => x.EquipmentId);
        });
        modelBuilder.Entity<IncidentCharge>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.IncidentItemId).IsRequired();
            entity.Property(x => x.ChargeType).HasColumnType("varchar(20)").IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.Amount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'PENDING'").IsRequired();
            entity.Property(x => x.RentalChargeId);
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasOne<EquipmentIncidentItem>().WithMany().HasForeignKey(x => x.IncidentItemId);
            entity.HasOne<RentalCharge>().WithMany().HasForeignKey(x => x.RentalChargeId);
        });
        modelBuilder.Entity<ReplacementRequest>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.RequestCode).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.IncidentId);
            entity.Property(x => x.RentalContractId).IsRequired();
            entity.Property(x => x.HandoverId);
            entity.Property(x => x.Reason).HasColumnType("text").IsRequired();
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'PENDING'").IsRequired();
            entity.Property(x => x.RequestedBy).IsRequired();
            entity.Property(x => x.ApprovedBy);
            entity.Property(x => x.ApprovedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.RequestCode).IsUnique();
            entity.HasOne<EquipmentIncident>().WithMany().HasForeignKey(x => x.IncidentId);
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
            entity.HasOne<HandoverNote>().WithMany().HasForeignKey(x => x.HandoverId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.RequestedBy);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedBy);
        });
        modelBuilder.Entity<ReplacementRequestItem>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ReplacementRequestId).IsRequired();
            entity.Property(x => x.OldEquipmentId).IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => new { x.ReplacementRequestId, x.OldEquipmentId }).IsUnique();
            entity.HasOne<ReplacementRequest>().WithMany().HasForeignKey(x => x.ReplacementRequestId);
            entity.HasOne<Equipment>().WithMany().HasForeignKey(x => x.OldEquipmentId);
        });
        modelBuilder.Entity<MaintenanceTask>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.EquipmentId).IsRequired();
            entity.Property(x => x.ServiceAssignmentId);
            entity.Property(x => x.Type).HasColumnType("varchar(20)").IsRequired();
            entity.Property(x => x.Description).HasColumnType("text").IsRequired();
            entity.Property(x => x.ScheduledAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.StartedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.Status).HasColumnType("varchar(20)").HasDefaultValueSql("'PENDING'").IsRequired();
            entity.Property(x => x.Result).HasColumnType("text");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.UpdatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasOne<Equipment>().WithMany().HasForeignKey(x => x.EquipmentId);
            entity.HasOne<ServiceAssignment>().WithMany().HasForeignKey(x => x.ServiceAssignmentId);
        });
        modelBuilder.Entity<MaintenanceRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.MaintenanceTaskId).IsRequired();
            entity.Property(x => x.PerformedBy).IsRequired();
            entity.Property(x => x.Description).HasColumnType("text").IsRequired();
            entity.Property(x => x.LaborCost).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.CompletedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasOne<MaintenanceTask>().WithMany().HasForeignKey(x => x.MaintenanceTaskId);
            entity.HasOne<Staff>().WithMany().HasForeignKey(x => x.PerformedBy);
        });
        modelBuilder.Entity<MaintenancePart>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.MaintenanceRecordId).IsRequired();
            entity.Property(x => x.Name).HasColumnType("varchar(255)").IsRequired();
            entity.Property(x => x.Quantity).HasColumnType("numeric(18,2)").IsRequired();
            entity.Property(x => x.UnitCost).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.TotalCost).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Note).HasColumnType("text");
            entity.HasOne<MaintenanceRecord>().WithMany().HasForeignKey(x => x.MaintenanceRecordId);
        });
        modelBuilder.Entity<EquipmentDisposal>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.EquipmentId).IsRequired();
            entity.Property(x => x.Reason).HasColumnType("text").IsRequired();
            entity.Property(x => x.DisposalDate).IsRequired();
            entity.Property(x => x.Method).HasColumnType("varchar(20)").IsRequired();
            entity.Property(x => x.Amount).HasColumnType("numeric(18,2)").HasDefaultValueSql("0").IsRequired();
            entity.Property(x => x.Description).HasColumnType("text");
            entity.Property(x => x.ApprovedBy).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.EquipmentId).IsUnique();
            entity.HasOne<Equipment>().WithMany().HasForeignKey(x => x.EquipmentId);
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.ApprovedBy);
        });
        modelBuilder.Entity<RentalContractRepresentative>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.RentalContractId).IsRequired();
            entity.Property(x => x.FullName).HasColumnType("varchar(150)").IsRequired();
            entity.Property(x => x.IdNumber).HasColumnType("varchar(50)").IsRequired();
            entity.Property(x => x.Phone).HasColumnType("varchar(20)");
            entity.Property(x => x.Email).HasColumnType("varchar(255)");
            entity.Property(x => x.RepresentativeType).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => x.RentalContractId).IsUnique();
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
        });
        modelBuilder.Entity<RentalContractDocument>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.RentalContractId).IsRequired();
            entity.Property(x => x.RepresentativeId);
            entity.Property(x => x.DocumentType).HasColumnType("varchar(30)").IsRequired();
            entity.Property(x => x.StorageFileId).IsRequired();
            entity.Property(x => x.DisplayOrder).HasDefaultValueSql("1").IsRequired();
            entity.Property(x => x.DocumentNumber).HasColumnType("varchar(100)");
            entity.Property(x => x.IssuedDate);
            entity.Property(x => x.Note).HasColumnType("text");
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasOne<RentalContract>().WithMany().HasForeignKey(x => x.RentalContractId);
            entity.HasOne<RentalContractRepresentative>().WithMany().HasForeignKey(x => x.RepresentativeId);
            entity.HasOne<StorageFile>().WithMany().HasForeignKey(x => x.StorageFileId);
        });
        modelBuilder.Entity<ServiceAssignmentAttachment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.ServiceAssignmentId).IsRequired();
            entity.Property(x => x.StorageFileId).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => new { x.ServiceAssignmentId, x.StorageFileId }).IsUnique();
            entity.HasOne<ServiceAssignment>().WithMany().HasForeignKey(x => x.ServiceAssignmentId);
            entity.HasOne<StorageFile>().WithMany().HasForeignKey(x => x.StorageFileId);
        });
        modelBuilder.Entity<HandoverNoteAttachment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.HandoverNoteId).IsRequired();
            entity.Property(x => x.StorageFileId).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => new { x.HandoverNoteId, x.StorageFileId }).IsUnique();
            entity.HasOne<HandoverNote>().WithMany().HasForeignKey(x => x.HandoverNoteId);
            entity.HasOne<StorageFile>().WithMany().HasForeignKey(x => x.StorageFileId);
        });
        modelBuilder.Entity<HandoverIssueAttachment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.HandoverIssueId).IsRequired();
            entity.Property(x => x.StorageFileId).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => new { x.HandoverIssueId, x.StorageFileId }).IsUnique();
            entity.HasOne<HandoverIssue>().WithMany().HasForeignKey(x => x.HandoverIssueId);
            entity.HasOne<StorageFile>().WithMany().HasForeignKey(x => x.StorageFileId);
        });
        modelBuilder.Entity<EquipmentIncidentAttachment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.IncidentId).IsRequired();
            entity.Property(x => x.StorageFileId).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => new { x.IncidentId, x.StorageFileId }).IsUnique();
            entity.HasOne<EquipmentIncident>().WithMany().HasForeignKey(x => x.IncidentId);
            entity.HasOne<StorageFile>().WithMany().HasForeignKey(x => x.StorageFileId);
        });
        modelBuilder.Entity<MaintenanceTaskAttachment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).HasDefaultValueSql("gen_random_uuid()").IsRequired();
            entity.Property(x => x.MaintenanceTaskId).IsRequired();
            entity.Property(x => x.StorageFileId).IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasIndex(x => new { x.MaintenanceTaskId, x.StorageFileId }).IsUnique();
            entity.HasOne<MaintenanceTask>().WithMany().HasForeignKey(x => x.MaintenanceTaskId);
            entity.HasOne<StorageFile>().WithMany().HasForeignKey(x => x.StorageFileId);
        });
        modelBuilder.Entity<RefreshToken>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Id).IsRequired();
            entity.Property(x => x.UserId).IsRequired();
            entity.Property(x => x.TokenHash).HasColumnType("varchar(64)").IsRequired();
            entity.Property(x => x.FamilyId).IsRequired();
            entity.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
            entity.Property(x => x.CreatedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone");
            entity.Property(x => x.ReplacedByTokenId);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
            entity.HasOne<RefreshToken>().WithMany().HasForeignKey(x => x.ReplacedByTokenId);
        });
        modelBuilder.Entity<RevokedToken>(entity =>
        {
            entity.HasKey(x => x.Jti);
            entity.Property(x => x.Jti).IsRequired();
            entity.Property(x => x.UserId).IsRequired();
            entity.Property(x => x.ExpiresAt).HasColumnType("timestamp with time zone").IsRequired();
            entity.Property(x => x.RevokedAt).HasColumnType("timestamp with time zone").HasDefaultValueSql("NOW()").IsRequired();
            entity.HasOne<User>().WithMany().HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<UploadSession>(entity =>
        {
            entity.Property(x => x.Folder)
                .HasConversion<string>();

            entity.Property(x => x.ResourceType)
                .HasConversion<string>();

            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(20);

            entity.HasIndex(x => new
            {
                x.UploadedBy,
                x.Status,
                x.ExpiresAt
            });
        });

        modelBuilder.Entity<UserDevice>(entity =>
        {
            entity.ToTable("user_devices");

            entity.HasKey(e => e.Id);

            entity.Property(e => e.Id)
                .HasColumnName("id")
                .HasColumnType("uuid")
                .HasDefaultValueSql("gen_random_uuid()")
                .ValueGeneratedOnAdd();

            entity.Property(e => e.UserId)
                .HasColumnName("user_id")
                .HasColumnType("uuid")
                .IsRequired();

            entity.Property(e => e.FcmToken)
                .HasColumnName("fcm_token")
                .HasColumnType("text")
                .IsRequired();

            entity.Property(e => e.DeviceId)
                .HasColumnName("device_id")
                .HasMaxLength(255);

            entity.Property(e => e.Platform)
                .HasColumnName("platform")
                .HasMaxLength(20)
                .IsRequired();

            entity.Property(e => e.CreatedAt)
                .HasColumnName("created_at")
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");

            entity.Property(e => e.UpdatedAt)
                .HasColumnName("updated_at")
                .HasColumnType("timestamptz")
                .HasDefaultValueSql("NOW()");

            entity.HasIndex(e => e.FcmToken)
                .IsUnique()
                .HasDatabaseName("uq_user_devices_fcm_token");

            entity.HasIndex(e => e.UserId)
                .HasDatabaseName("idx_user_devices_user_id");

            entity.HasIndex(e => e.DeviceId)
                .HasDatabaseName("idx_user_devices_device_id");

            entity.HasOne(e => e.User)
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        foreach (var entityType in modelBuilder.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties())
            {
                var propertyType = Nullable.GetUnderlyingType(property.ClrType) ?? property.ClrType;
                if (propertyType.IsEnum)
                {
                    var converterType = typeof(EnumToUpperStringConverter<>).MakeGenericType(propertyType);
                    property.SetValueConverter((ValueConverter)Activator.CreateInstance(converterType)!);
                }
            }
        }

        base.OnModelCreating(modelBuilder);
    }
}
