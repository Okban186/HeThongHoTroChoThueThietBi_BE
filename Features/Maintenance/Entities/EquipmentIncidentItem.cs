using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Maintenance.Entities;

[Table("equipment_incident_items")]
public class EquipmentIncidentItem
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("incident_id")]
    public Guid IncidentId { get; set; }

    [Column("equipment_id")]
    public Guid EquipmentId { get; set; }

    [Column("description")]
    public string? Description { get; set; }

    [Column("assessment")]
    public string? Assessment { get; set; }

    /// <summary>TECHNICAL_FAULT (lỗi kỹ thuật/hao mòn tự nhiên) | CUSTOMER_FAULT (lỗi do khách hàng).</summary>
    [Column("fault_type")]
    [MaxLength(30)]
    public string? FaultType { get; set; }

    /// <summary>Phiếu bàn giao đổi/bù sự cố (INCIDENT_REPLACEMENT) tương ứng nếu đã lập.</summary>
    [Column("replacement_handover_note_id")]
    public Guid? ReplacementHandoverNoteId { get; set; }

    [Column("note")]
    public string? Note { get; set; }
}
