using System.Collections;
using System.Collections.Generic;
using Unity.Netcode;
using UnityEngine;

/// <summary>
/// 매칭/로그인 흐름 없이 InGameScene 을 단독으로 실행하기 위한 부트스트랩.
/// 캐릭터 외형 / 이동 / 자동평타 / 스킬 / 이펙트 / UI 를 빠르게 반복 테스트할 때 사용.
///
/// ── 사용법 (한 번만 세팅) ─────────────────────────────────────
/// 1) InGameScene 에 빈 GameObject 추가 → 이 컴포넌트 부착.
/// 2) Login_Scene 의 NetworkManager (NetworkObject + UnityTransport 가 붙은 것) 프리팹/오브젝트를
///    InGameScene 으로 복사. (이 부트스트랩은 NetworkManager 를 직접 생성하지 않음.)
/// 3) Inspector 에서 테스트할 직업·등급·스킬·더미 적 수를 설정.
/// 4) Play 버튼.
///
/// ── 자동 차단 ────────────────────────────────────────────────
/// GameManager.myCharacterData 가 이미 채워져 있으면 (= 정상 매칭 흐름) 부트스트랩은 자동 비활성.
/// 따라서 이 컴포넌트가 출시 빌드에 섞여 들어가더라도 실 매칭에 영향 없음.
/// </summary>
public class StandaloneTestBootstrap : MonoBehaviour
{
    [Header("활성 조건")]
    [Tooltip("이미 매칭/로그인 흐름이 진행 중이면 자동으로 비활성. 실 매치 빌드에 들어가도 안전.")]
    public bool autoDisableIfNormalFlow = true;

    [Header("로컬 플레이어")]
    public JobType      forceJob      = JobType.Warrior;
    public AffinityType forceAffinity = AffinityType.Spicy;
    public GradeTier    forceGrade    = GradeTier.Legendary;
    public string       testNickname  = "TestHero";

    [Tooltip("비워두면 직업별 액티브 풀에서 무작위 배정. 슬롯 4칸 한도.")]
    public List<ActiveSkillType>  forceActiveSkills  = new List<ActiveSkillType>();
    [Tooltip("비워두면 무작위 0~2 개. 슬롯 4칸 한도.")]
    public List<PassiveSkillType> forcePassiveSkills = new List<PassiveSkillType>();

    [Header("스탯 강제 (0 = 자동)")]
    public float forceMaxHp   = 0f;
    public float forceBaseAtk = 0f;

    [Header("더미 적")]
    [Tooltip("스킬/평타 표적용 더미 적 수. 0 이면 안 만듦.")]
    [Range(0, 7)] public int dummyCount = 1;
    [Tooltip("JobVisualRegistry 에 등록된 직업으로 두는 게 좋음 (예: Warrior, Archer). " +
             "미등록 직업이면 기본 흰 Circle 스프라이트로 표시되어 화면이 깜빡거리듯 보임.")]
    public JobType dummyJob   = JobType.Archer;
    [Tooltip("너무 크면 데미지 변화가 시각적으로 안 보임 (예: 9999 HP 에 5 데미지 = 0.05%). " +
             "테스트 가시성 위해 200 권장. 죽는 게 싫으면 1000~2000 정도.")]
    public float   dummyMaxHp = 200f;
    public float   dummyBaseAtk = 1f;
    [Tooltip("폴백용 — NSM 스폰 포인트가 모자라거나 미설정일 때 본인 기준 원형 배치 반경. 현재 맵 스케일(±500u)에 맞춰 큼지막하게.")]
    public float   dummyRadius = 80f;
    [Tooltip("켜면 NetworkSpawnManager 의 spawnPoints 를 우선 사용하여 더미를 분산 배치. 끄면 무조건 본인 주변 원형.")]
    public bool    useNetworkSpawnPoints = true;

    [Header("디버그 편의")]
    [Tooltip("ServerValidator(안티치트)를 비활성. 매칭 시작 시 위치 보정으로 인한 '속도핵 감지' 오탐 + 위치 롤백 방지.")]
    public bool disableServerValidator = true;

    [Header("빠른 시작")]
    [Tooltip("InGameManager.minPlayers / maxPlayers 를 1 로 강제. 카운트다운 5초 후 즉시 시작.")]
    public bool fastStart = true;

    [Tooltip("StartHost 후 N초 뒤 모든 PlayerController 의 movementLocked/attackLocked 를 강제 해제. " +
             "InGameManager.Start 가 StartHost 이전에 실행돼 GameStartSequence 가 영영 실행되지 않는 " +
             "단독 테스트 모드 한정 버그를 우회. 카운트다운 5초가 부담스러우면 짧게.")]
    public float forceUnlockAfterSeconds = 5f;

    [Header("로그")]
    public bool verbose = true;

    // ════════════════════════════════════════════════════════════
    //  생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (autoDisableIfNormalFlow
            && GameManager.Instance != null
            && GameManager.Instance.myCharacterData != null)
        {
            Log("매칭 흐름 감지 → 부트스트랩 스킵.");
            enabled = false;
            return;
        }
        Log("InGameScene 단독 실행 감지 → 테스트 모드 시작.");
    }

    private void Start()
    {
        if (!enabled) return;
        StartCoroutine(BootstrapRoutine());
    }

    // ════════════════════════════════════════════════════════════
    //  부트스트랩
    // ════════════════════════════════════════════════════════════

    private IEnumerator BootstrapRoutine()
    {
        var netMgr = NetworkManager.Singleton;
        if (netMgr == null)
        {
            Debug.LogError(
                "[StandaloneTest] NetworkManager.Singleton == null.\n" +
                " → Login_Scene 의 NetworkManager 오브젝트(NetworkObject + UnityTransport) 를 " +
                "InGameScene 으로 복사하거나, Login_Scene 부터 시작하세요.");
            yield break;
        }

        EnsureGameManager();

        var data = CreateTestCharacter();
        GameManager.Instance.myCharacterData      = data;
        GameManager.Instance.currentPlayerNickname = testNickname;
        // PlayerNetworkSync.IsValidUuid 통과 위해 유효한 UUID 필요.
        GameManager.Instance.currentPlayerId = System.Guid.NewGuid().ToString();

        if (fastStart)
        {
            yield return null; // InGameManager.Awake 이후 실행 보장
            if (InGameManager.Instance != null)
            {
                InGameManager.Instance.minPlayers = 1;
                InGameManager.Instance.maxPlayers = 1;
            }
        }

        NetworkSpawnManager.PendingExpectedPlayerCount = 1;

        // [중요] StartHost 이전에 ServerValidator 비활성화.
        // 호스트 초기 스폰 (0,0) → spawn point (±500u) 텔레포트가 감지되어 위치 롤백 → 이동 불가.
        if (disableServerValidator && ServerValidator.Instance != null)
        {
            ServerValidator.Instance.enabled = false;
            Log("ServerValidator 사전 비활성 (StartHost 이전).");
        }

        if (!netMgr.IsListening)
        {
            bool ok = netMgr.StartHost();
            if (!ok)
            {
                Debug.LogError("[StandaloneTest] NetworkManager.StartHost() 실패. " +
                               "UnityTransport 가 NetworkManager 와 같은 오브젝트에 붙어있는지 확인.");
                yield break;
            }
            Log("Host 시작 완료.");
        }

        if (dummyCount > 0)
        {
            // 본인 플레이어 스폰 + NetworkSpawnManager 초기화 대기.
            yield return new WaitForSeconds(2f);
            SpawnDummies();
        }

        // [버그 수정] InGameManager.Start 가 Bootstrap.StartHost 보다 먼저 실행돼
        // netMgr.IsServer == false 로 판정 → GameStartSequence 가 영영 시작되지 않음.
        // 결과: 모든 PlayerController 의 movementLocked / attackLocked 가 true 인 채로 영구 잠금.
        // 매칭 흐름에서는 발생하지 않는 단독 테스트 모드 한정 문제이므로 부트스트랩에서 직접 해제.
        if (forceUnlockAfterSeconds > 0f)
        {
            yield return new WaitForSeconds(forceUnlockAfterSeconds);
            int unlocked = 0;
            foreach (var p in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            {
                if (p == null || p.IsDead) continue;
                p.SetMovementLocked(false);
                p.SetAttackLocked(false);
                unlocked++;
            }
            Log($"강제 잠금 해제 적용 (대상 {unlocked}명). 이동/평타/스킬 가능.");
        }

        Log("부트스트랩 완료.");
    }

    private void EnsureGameManager()
    {
        if (GameManager.Instance != null) return;
        var go = new GameObject("GameManager (Test)");
        go.AddComponent<GameManager>();
        Log("GameManager 즉석 생성.");
    }

    private CharacterData CreateTestCharacter()
    {
        // StatCalculator.GenerateCharacter 는 forceJob 적용 + 등급/상성/스킬을 자체 랜덤.
        var data = StatCalculator.GenerateCharacter(testNickname, forceJob);
        data.affinity = forceAffinity;
        data.grade    = forceGrade;

        if (forceMaxHp > 0f)   { data.maxHp = forceMaxHp; data.currentHp = forceMaxHp; }
        if (forceBaseAtk > 0f) data.baseAtk = forceBaseAtk;

        if (forceActiveSkills != null && forceActiveSkills.Count > 0)
        {
            data.activeSkills.Clear();
            for (int i = 0; i < forceActiveSkills.Count && i < 4; i++)
                data.activeSkills.Add(forceActiveSkills[i]);
        }
        if (forcePassiveSkills != null && forcePassiveSkills.Count > 0)
        {
            data.passiveSkills.Clear();
            for (int i = 0; i < forcePassiveSkills.Count && i < 4; i++)
                data.passiveSkills.Add(forcePassiveSkills[i]);
        }
        return data;
    }

    // ════════════════════════════════════════════════════════════
    //  더미 적
    // ════════════════════════════════════════════════════════════

    private void SpawnDummies()
    {
        var nsm = NetworkSpawnManager.Instance;
        if (nsm == null || nsm.playerPrefab == null)
        {
            Debug.LogError("[StandaloneTest] NetworkSpawnManager 또는 playerPrefab 미설정 → 더미 스폰 불가.");
            return;
        }

        Vector2 origin = Vector2.zero;
        var me = FindLocalPlayer();
        if (me != null) origin = (Vector2)me.transform.position;

        // [개선] NetworkSpawnManager.GetNextSpawnPoint() 우선 사용.
        // 인게임씬의 spawnPoints(Point1~Point8)는 ±500u 범위로 분산되어 있어
        // 현재 맵 스케일에 적합. 폴백은 본인 주변 80u 원형 배치.
        bool useNSM = useNetworkSpawnPoints && nsm.spawnPoints != null && nsm.spawnPoints.Length > 0;

        for (int i = 0; i < dummyCount; i++)
        {
            Vector2 pos;
            if (useNSM)
            {
                pos = nsm.GetNextSpawnPoint();
                // 본인 위치와 동일/근접하면 폴백 사용 (호스트가 같은 포인트를 이미 점유한 경우).
                if (me != null && Vector2.Distance(pos, origin) < 1f)
                    pos = origin + RingOffset(i);
            }
            else
            {
                pos = origin + RingOffset(i);
            }

            var dummy = Instantiate(nsm.playerPrefab, pos, Quaternion.identity);

            // [중요] _isBot 을 Spawn() 호출 *이전* 에 미리 설정.
            // 호스트 환경에서 서버 소유 더미도 IsOwner=true 이므로 Spawn() 내부의
            //   PlayerNetworkSync.OnNetworkSpawn → SubmitCharacterDataServerRpc (host 데이터 전송)
            //   → 서버 처리 → NetworkHp.Value 설정 → HandleHpChanged 콜백
            //   → 이 시점에 _isBot 검사가 실행되는데, 이때까지 false 면 더미가 InitPlayerUI 에 등록되어
            //     localPlayer = 더미 가 되어버림. 이후 사용자가 스킬 버튼을 누르면 더미.UseSkill 이 호출되어
            //     skillCount=0 으로 거부됨.
            // → Spawn() 이전에 미리 봇 플래그를 켜두어 OnNetworkSpawn / HandleHpChanged 가
            //   IsBot 가드로 본인 HUD 흐름을 건드리지 않게 함.
            var dummyController = dummy.GetComponent<PlayerController>();
            if (dummyController != null) dummyController.SetAsBot(true);

            var netObj = dummy.GetComponent<NetworkObject>();
            if (netObj == null) { Destroy(dummy); continue; }
            netObj.Spawn(); // 서버 소유 (Owner 없음)

            // Instantiate 직후 위치가 ClientNetworkTransform 의 초기 동기화로 (0,0) 등으로
            // 리셋되는 경우가 있어 명시적으로 한 번 더 위치를 박아준다.
            dummy.transform.position = pos;
            var rb = dummy.GetComponent<Rigidbody2D>();
            if (rb != null) rb.position = pos;

            var sync = dummy.GetComponent<PlayerNetworkSync>();
            if (sync != null)
            {
                var data = StatCalculator.GenerateCharacter($"Dummy{i + 1}", dummyJob);
                data.maxHp     = Mathf.Max(1f, dummyMaxHp);
                data.currentHp = data.maxHp;
                data.baseAtk   = Mathf.Max(0.5f, dummyBaseAtk);
                // 더미는 일부러 스킬 비움 → AI 없는 순수 표적.
                data.activeSkills.Clear();
                // [중요] 더미의 패시브도 모두 제거.
                //   • Thorns 보유 시: 본인 공격이 반사되어 본인이 피격 모션을 받음 (혼란).
                //   • GiantKiller 등 본인이 보유한 패시브와 더미의 거대 HP 가 결합하면
                //     데미지가 수십배로 증폭되어 균형이 무너짐.
                // 테스트 가시성을 위해 더미는 패시브 없이 순수 샌드백으로 운영.
                data.passiveSkills.Clear();
                sync.DebugInitializeAsBot(data, $"Dummy{i + 1}");
            }

            Log($"더미 #{i+1} 스폰 위치: {pos}");
        }

        Log($"더미 적 {dummyCount}명 스폰 완료. (origin={origin}, useNSM={useNSM})");
    }

    /// <summary>본인 위치 기준 원형 분산 오프셋 계산.</summary>
    private Vector2 RingOffset(int i)
    {
        float angle = (360f / Mathf.Max(1, dummyCount)) * i * Mathf.Deg2Rad;
        return new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * dummyRadius;
    }

    private PlayerController FindLocalPlayer()
    {
        foreach (var pc in FindObjectsByType<PlayerController>(FindObjectsSortMode.None))
            if (pc != null && pc.IsLocalPlayer) return pc;
        return null;
    }

    private void Log(string msg)
    {
        if (verbose) Debug.Log("[StandaloneTest] " + msg);
    }
}
