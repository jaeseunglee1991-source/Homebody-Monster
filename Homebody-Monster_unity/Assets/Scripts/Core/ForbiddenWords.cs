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

    /// <summary>
    /// L-5: 닉네임 사칭 방지 전용 정확 일치 차단 목록.
    /// `List`의 단어 중 짧고 영문 일반어와 충돌하기 쉬운 항목(admin/gm/master/system 등)이
    /// `Contains` 부분 문자열 매칭으로 "Masterpiece", "Systematic", "Administrator" 같은 정상 닉네임까지
    /// 차단하던 버그를 해소하기 위해, 닉네임 검사에서는 이 리스트로 정확 일치(소문자 normalize 후) 비교한다.
    /// 욕설(한글/영문)은 부분 문자열 회피가 쉬우므로 기존 `List` Contains 방식을 그대로 유지한다.
    /// </summary>
    public static readonly string[] NicknameExactBlockList =
    {
        "운영자","관리자","운영진","admin","gm","master","system",
        "owner","staff","support","official","mod","moderator",
        "homebodymonster","homebody"
    };
}
