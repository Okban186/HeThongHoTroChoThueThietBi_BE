using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using HeThongHoTroChoThueThietBi.Features.Finance.Enums;
using Microsoft.EntityFrameworkCore;

namespace HeThongHoTroChoThueThietBi.Features.Finance.Entities;

[Table("payment_transactions")]
public class PaymentTransaction
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("rental_charge_id")]
    public Guid? RentalChargeId { get; set; }

    [Column("incident_charge_id")]
    public Guid? IncidentChargeId { get; set; }

    [Column("target_type")]
    [MaxLength(30)]
    [Required]
    public PaymentTargetType TargetType { get; set; }

    [Column("payment_method")]
    [MaxLength(20)]
    [Required]
    public PaymentMethod PaymentMethod { get; set; }

    [Column("txn_ref")]
    [MaxLength(100)]
    [Required]
    public string TxnRef { get; set; } = null!;

    [Column("amount")]
    [Precision(18, 2)]
    public decimal Amount { get; set; }

    [Column("order_info")]
    [Required]
    public string OrderInfo { get; set; } = null!;

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public PaymentTransactionStatus Status { get; set; }

    [Column("payment_url")]
    public string? PaymentUrl { get; set; }

    [Column("expires_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ExpiresAt { get; set; }

    [Column("vnp_transaction_no")]
    [MaxLength(100)]
    public string? VnpTransactionNo { get; set; }

    [Column("vnp_bank_code")]
    [MaxLength(50)]
    public string? VnpBankCode { get; set; }

    [Column("vnp_response_code")]
    [MaxLength(20)]
    public string? VnpResponseCode { get; set; }

    [Column("vnp_pay_date")]
    [MaxLength(20)]
    public string? VnpPayDate { get; set; }

    [Column("receipt_file_id")]
    public Guid? ReceiptFileId { get; set; }

    [Column("created_by")]
    public Guid CreatedBy { get; set; }

    [Column("verified_by")]
    public Guid? VerifiedBy { get; set; }

    [Column("verified_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? VerifiedAt { get; set; }

    [Column("rejected_reason")]
    public string? RejectedReason { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }
}
