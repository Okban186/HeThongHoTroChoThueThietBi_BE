using HeThongHoTroChoThueThietBi.Features.Rental.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Entities;

[Table("rental_contracts")]
public class RentalContract
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("contract_code")]
    [MaxLength(50)]
    [Required]
    public string ContractCode { get; set; } = null!;

    [Column("reservation_id")]
    public Guid? ReservationId { get; set; }

    [Column("company_id")]
    public Guid CompanyId { get; set; }

    [Column("start_date")]
    public DateOnly StartDate { get; set; }

    [Column("end_date")]
    public DateOnly EndDate { get; set; }

    [Column("rental_type")]
    [MaxLength(20)]
    [Required]
    public string RentalType { get; set; } = null!;

    [Column("delivery_address")]
    public string? DeliveryAddress { get; set; }

    [Column("delivery_note")]
    public string? DeliveryNote { get; set; }

    [Column("contract_total_amount")]
    [Precision(18, 2)]
    public decimal ContractTotalAmount { get; set; }

    [Column("deposit_required_amount")]
    [Precision(18, 2)]
    public decimal DepositRequiredAmount { get; set; }

    [Column("deposit_paid_amount")]
    [Precision(18, 2)]
    public decimal DepositPaidAmount { get; set; }

    [Column("deposit_applied_amount")]
    [Precision(18, 2)]
    public decimal DepositAppliedAmount { get; set; }

    [Column("deposit_refunded_amount")]
    [Precision(18, 2)]
    public decimal DepositRefundedAmount { get; set; }

    [Column("contract_file_id")]
    public Guid? ContractFileId { get; set; }

    [Column("signed_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? SignedAt { get; set; }

    [Column("effective_from")]
    public DateOnly? EffectiveFrom { get; set; }

    [Column("effective_to")]
    public DateOnly? EffectiveTo { get; set; }

    [Column("status")]
    [MaxLength(30)]
    [Required]
    public RentalContractStatus Status { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("approved_by")]
    public Guid? ApprovedBy { get; set; }

    [Column("approved_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ApprovedAt { get; set; }

    [Column("rejection_reason")]
    public string? RejectionReason { get; set; }

    [Column("cancellation_reason")]
    public string? CancellationReason { get; set; }

    [Column("cancellation_requested_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? CancellationRequestedAt { get; set; }

    [Column("cancellation_requested_by")]
    public Guid? CancellationRequestedBy { get; set; }

    [Column("cancelled_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? CancelledAt { get; set; }

    [Column("cancelled_by")]
    public Guid? CancelledBy { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
