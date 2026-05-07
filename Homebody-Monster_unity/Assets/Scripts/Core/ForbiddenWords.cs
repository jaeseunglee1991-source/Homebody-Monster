/// <summary>
/// 닉네임/채팅에서 차단할 단어 목록 (욕설·운영 사칭·영문 욕설 등).
/// AuthManager / NicknameChangeUI 등에서 공통으로 참조합니다.
/// </summary>
public static class ForbiddenWords
{
    public static readonly string[] List =
    {
        "씨발","시발","씨팔","시팔","씨빨","시빨","쓰벌","ㅅㅂ",
        "개새끼","개새","개년","개놈","개쓰레기",
        "병신","벙신","ㅂㅅ",
        "보지","자지",
        "애미","애비","니애미","니애비",
        "창녀","창놈","걸레년",
        "미친놈","미친년","미친새끼",
        "꺼져","죽어","뒤져",
        "운영자","관리자","운영진","admin","gm","master","system",
        "fuck","fuk","fck","shit","bitch","asshole","bastard","cunt",
        "nigger","nigga"
    };
}
