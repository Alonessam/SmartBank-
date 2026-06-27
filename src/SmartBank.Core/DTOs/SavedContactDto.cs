using System;
using System.ComponentModel.DataAnnotations;

namespace SmartBank.Core.DTOs
{
    public class SavedContactDto
    {
        public Guid Id { get; set; }
        public string AccountNumber { get; set; } = string.Empty;
        public string Alias { get; set; } = string.Empty;
        public DateTime CreatedAt { get; set; }
    }

    public class CreateSavedContactDto
    {
        [Required]
        [StringLength(20, MinimumLength = 10)]
        public string AccountNumber { get; set; } = string.Empty;

        [Required]
        [StringLength(100, MinimumLength = 1)]
        public string Alias { get; set; } = string.Empty;
    }
}
