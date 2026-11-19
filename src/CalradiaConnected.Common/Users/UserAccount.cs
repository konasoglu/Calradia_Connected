using System;
using Newtonsoft.Json;

namespace Common.Users
{
    public class UserAccount
    {
        [JsonProperty("username")]
        public string Username { get; set; } = string.Empty;

        [JsonProperty("passwordHash")]
        public string PasswordHash { get; set; } = string.Empty;

        [JsonProperty("salt")]
        public string Salt { get; set; } = string.Empty;

        [JsonProperty("accountId")]
        public int AccountId { get; set; }

        [JsonProperty("isAdmin")]
        public bool IsAdmin { get; set; }

        [JsonProperty("isBanned")]
        public bool IsBanned { get; set; }

        [JsonProperty("banReason")]
        public string BanReason { get; set; } = string.Empty;

        [JsonProperty("createdAt")]
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

        [JsonProperty("lastLogin")]
        public DateTime? LastLogin { get; set; }

        [JsonProperty("heroName")]
        public string HeroName { get; set; } = string.Empty;

        [JsonProperty("heroId")]
        public string HeroId { get; set; } = string.Empty;

        [JsonProperty("clanName")]
        public string ClanName { get; set; } = string.Empty;
    }
}
