using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Operations.Entities;

[Table("service_assignment_members")]
public class ServiceAssignmentMember
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("service_assignment_id")]
    public Guid ServiceAssignmentId { get; set; }

    [Column("staff_id")]
    public Guid StaffId { get; set; }

    [Column("role")]
    [MaxLength(20)]
    [Required]
    public string Role { get; set; } = null!;

}
