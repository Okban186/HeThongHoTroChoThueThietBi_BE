using HeThongHoTroChoThueThietBi.Features.Organization.Enums;
using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Organization.Entities;

[Table("staff")]
public class Staff
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("user_id")]
    public Guid UserId { get; set; }

    [Column("employee_code")]
    [MaxLength(50)]
    [Required]
    public string EmployeeCode { get; set; } = null!;

    [Column("position")]
    [MaxLength(100)]
    public string? Position { get; set; }

    [Column("hire_date")]
    public DateOnly? HireDate { get; set; }

    [Column("status")]
    [MaxLength(20)]
    [Required]
    public StaffStatus Status { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

    [Column("updated_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset UpdatedAt { get; set; }

}
