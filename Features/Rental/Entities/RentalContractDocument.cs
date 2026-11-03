using System;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace HeThongHoTroChoThueThietBi.Features.Rental.Entities;

[Table("rental_contract_documents")]
public class RentalContractDocument
{
    [Key]
    [Column("id")]
    public Guid Id { get; set; }

    [Column("rental_contract_id")]
    public Guid RentalContractId { get; set; }

    [Column("representative_id")]
    public Guid? RepresentativeId { get; set; }

    [Column("document_type")]
    [MaxLength(30)]
    [Required]
    public string DocumentType { get; set; } = null!;

    [Column("storage_file_id")]
    public Guid StorageFileId { get; set; }

    [Column("display_order")]
    public int DisplayOrder { get; set; }

    [Column("document_number")]
    [MaxLength(100)]
    public string? DocumentNumber { get; set; }

    [Column("issued_date")]
    public DateOnly? IssuedDate { get; set; }

    [Column("note")]
    public string? Note { get; set; }

    [Column("created_at", TypeName = "timestamp with time zone")]
    public DateTimeOffset CreatedAt { get; set; }

}
