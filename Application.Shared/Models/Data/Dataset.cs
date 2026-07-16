using Microsoft.EntityFrameworkCore;
using System;
using System.Collections.Generic;
using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Application.Shared.Enums;

namespace Application.Shared.Models;

public class Dataset
{

    public string CompanyId { get; set; }
    public Company? Company { get; set; }

    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public string? Id { get; set; }

    [Required(ErrorMessage = "Dataset name is required")]
    [StringLength(100, ErrorMessage = "Dataset name cannot exceed 100 characters")]
    [MaxLength(100)]
    [MinLength(3, ErrorMessage = "Dataset name must be at least 3 characters long")]
    [RegularExpression(@"^[a-zA-Z0-9\s]+$", ErrorMessage = "Dataset name can only contain letters, numbers, and spaces")]
    [Display(Name = "Dataset Name")]
    public string? Name { get; set; }


    [Required(ErrorMessage = "Dataset description is required")]
    [StringLength(500, ErrorMessage = "Dataset description cannot exceed 500 characters")]
    [MaxLength(500)]
    [MinLength(3, ErrorMessage = "Dataset description must be at least 3 characters long")]
    [Display(Name = "Description")]
    public string? Description { get; set; }

    // Where the dataset's tables live. Local = its own DuckDB file; External = backed by a connected
    // Database entity and queried live (the DuckDB file then only holds saved snapshots).
    public DatasetSourceType SourceType { get; set; } = DatasetSourceType.Local;

    // For External datasets, the id of the Database-type MonitoredAsset (Status side) whose saved
    // connection is used to run queries. Stored as a plain string — it crosses DbContexts, so there is
    // no FK (same cross-context pattern as IngestionSource.SourceEntityId).
    [MaxLength(450)]
    public string? SourceEntityId { get; set; }

    // Directory where this dataset's DuckDB file ({Id}.duckdb) is stored. Chosen by the user at
    // create/edit; when blank we fall back to the app-wide Duckdb:DuckdbFilePath.
    [MaxLength(500)]
    public string? Path { get; set; } = "C:/duckdb";

    // True for datasets created by an end-user through the app (vs system/seeded). Defaults to true.
    public bool IsUserDataset { get; set; } = true;

    // User who created the dataset
    public string? CreatedBy { get; set; }
    
    // Creation and modification timestamps
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime? ModifiedAt { get; set; }

}
