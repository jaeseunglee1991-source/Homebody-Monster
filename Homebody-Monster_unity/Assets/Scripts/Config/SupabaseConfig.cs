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

    [Tooltip("예: eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9...")]
    public string SupabaseAnonKey;
}
