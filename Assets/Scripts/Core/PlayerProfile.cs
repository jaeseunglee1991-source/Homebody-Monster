using Supabase.Postgrest.Attributes;
using Supabase.Postgrest.Models;

// Supabase의 'profiles' 테이블과 유기적으로 연결됩니다.
[Table("profiles")]
public class PlayerProfile : BaseModel
{
    [PrimaryKey("id", false)] 
    public string Id { get; set; }

    [Column("nickname")]
    public string Nickname { get; set; }

    [Column("win_count")]
    public int WinCount { get; set; }

    [Column("lose_count")]
    public int LoseCount { get; set; }
}
