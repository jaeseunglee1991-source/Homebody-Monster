using UnityEngine;

/// <summary>
/// Supabase 프로젝트 설정을 저장하는 ScriptableObject.
///
/// 사용법:
///  1. Unity 메뉴 → Assets → Create → HomebodyMonster → Supabase Config
///  2. Assets/Resources/ 폴더에 "SupabaseConfig.asset" 으로 저장
///  3. Inspector에서 SupabaseUrl, SupabaseAnonKey 입력
///
/// 보안 주의:
///  - Assets/Resources/SupabaseConfig.asset 을 .gitignore에 추가하세요!
///  - CI/CD 빌드 파이프라인에서 환경변수로 자동 생성하는 스크립트 사용을 권장합니다.
/// </summary>
[CreateAssetMenu(fileName = "SupabaseConfig", menuName = "HomebodyMonster/Supabase Config")]
public class SupabaseConfig : ScriptableObject
{
    [Header("Supabase 프로젝트 설정")]
    [Tooltip("예: https://abcdefghijkl.supabase.co")]
    public string SupabaseUrl;

    // ⚠️ 보안: anon key는 클라이언트에 배포되지만, 그래도 git에 평문 커밋하면
    //   ① 키 회전이 어려워지고 ② 외부 fork 시 의도치 않게 노출된다.
    //   반드시 Resources/SupabaseConfig.asset 을 .gitignore에 추가하고,
    //   로컬 개발/빌드 파이프라인에서 환경변수로 주입하는 방식을 사용한다.
    [Tooltip("예: eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...  (커밋 금지!)")]
    public string SupabaseAnonKey;
}
