using HeThongHoTroChoThueThietBi.Features.Maintenance.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("replacement_requests")]
public class ReplacementRequest
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("request_code")]
    [MaxLength(50)]
    [Required]
    public string RequestCode { get; set; } = null!;

    [Column("incident_id")]
    public Guid? IncidentId { get; set; }

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("handover_id")]
    public Guid? HandoverId { get; set; }

    [Column("reason")]
    [Required]
    public string Reason { get; set; } = null!;

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public ReplacementRequestStatus Status { get; set; }

    [Column("requested_by")]
    public Guid RequestedBy { get; set; }

    [Column("approved_by")]
    public Guid? ApprovedBy { get; set; }

    [Column("approved_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset? ApprovedAt { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
