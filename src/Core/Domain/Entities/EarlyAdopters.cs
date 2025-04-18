
using System.ComponentModel.DataAnnotations;
using System.Text.Json.Serialization;

namespace Domain.Entities;

public class EarlyAdopters
{
    [Key]
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid ApplicationUserId { get; set; }

    [JsonIgnore]
    public ApplicationUser ApplicationUser { get; set; }
}
