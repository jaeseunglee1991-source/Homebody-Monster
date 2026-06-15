# Pass C — NGO 완전 제거 실행계획 (의존성 클러스터 순서)

> 실측: `Unity.Netcode` import 14파일 + NGO 타입 참조 총 **517곳 / 46파일**. 삭제 ~25 + 편집 ~15.
> **단일 파일 고립 삭제 불가** — 삭제 대상끼리 + 유지 파일(InGameHUD/SkillSystem/CombatSystem)이
> 서로 얽혀 있어, **편집↔삭제를 클러스터로 맞물려** 진행해야 중간 컴파일이 된다.
> ⚠️ 각 단계 후 **컴파일 확인 필수**. 시작 전 **현재 Pass B 작업을 커밋**(복구 지점)할 것.

---

## 분류

### A. 삭제 (NGO 전용 — Fusion이 완전 대체)
- `Network/`: PlayerNetworkSync, NetworkSpawnManager, NetworkProjectile, ClientNetworkTransform, ServerValidator, NetworkPingMonitor
- `Core/`: PlayerController, PlayerVisibility, PingAdaptiveCombat, PlayerWorldUI, CameraFollowLocalPlayer, StatusEffectSystem
- `Managers/`: AppNetworkManager, SpectatorManager, ReconnectManager, NetworkRoomManager, SupabaseManager_Ping, **InGameManager**(→ NetMatch가 대체)
- `Utils/`: StandaloneTestBootstrap
- `UI/`: PingHUD, ReconnectUIBinder, ResultController(별도 ResultScene 흐름이면 유지 검토)
- `Editor/`: InGameArenaSetup(NGO 아레나), JobAttackTransitionFix(PlayerController 의존이면)

### B. 유지하되 NGO 부분 제거 (Fusion 재사용)
- `Core/GameData.cs` — `using Unity.Netcode` + NetworkVariable/INetworkSerializable 제거. CharacterData/JobSkillPool 유지.
- `Core/SkillSystem.cs` — `GetCooldown`(순수)·스킬 데이터 유지. NGO 오케스트레이터(ActivateSkillServer·ClientRpc·NetworkProjectile 스폰·InGameManager) 제거.
- `Core/CombatSystem.cs` — `TryTenacity`/`TryGuardianAngel`(StatusEffectSystem 인자) 제거. CalculateDamage/RegenerationRoutine 유지.
- `Managers/CharacterRerollSystem.cs` — 경제·`GetSkillDisplayName`·`RerollWindowSecs` 유지, NGO 참조 제거.
- `Managers/GameManager.cs`, `MatchmakingManager.cs` — NetworkManager.Singleton·NGO 큐 경로 제거(Fusion 분기 유지).
- `UI/`: LobbyUIController, MatchmakingUX, LobbySettingsPanel — NGO 참조 가드 제거.
- `Managers/LeaderboardManager.cs`, `SupabaseManager.cs`, `Core/GameBalanceConfig.cs`, `Core/CharacterLabels.cs` — 잔여 NGO 참조만 정리.
- `Editor/FusionInGameCutover.cs` — CameraFollowLocalPlayer 비활성 단계 제거(삭제 후엔 불필요).

---

## 실행 순서 (컴파일 체크포인트 = ✅)

**0. 커밋** — 현재 Pass B 작업 커밋(복구 지점). 권장: Pass C용 브랜치.

**1. UI/HUD 디커플 (InGameHUD)**
- InGameHUD에서 NGO 의존 메서드/필드 제거: `InitPlayerUI(PlayerController)`, `SetupSkillButtons`, `OnSkillClicked`(localPlayer.UseSkill), `localPlayer`, `ShowReviveUI(PlayerNetworkSync)`, `UpdateReviveInfoText`(InGameManager), `ShowSurrenderConfirm`의 networkSync 호출.
  → 표시 메서드(UpdateHealthBar/Survivor/Timer/KillFeed/EndBanner)와 패널/버튼 **필드는 유지**(브리지가 사용).
- 동시에 그 메서드를 호출하던 **PlayerController 삭제**(아래 2와 함께). ✅

**2. 플레이어/네트워크 클러스터 삭제 + 참조 정리**
- 삭제: PlayerController, PlayerNetworkSync, NetworkSpawnManager, NetworkProjectile, ClientNetworkTransform, ServerValidator, PlayerVisibility, PlayerWorldUI, CameraFollowLocalPlayer, StandaloneTestBootstrap.
- 동시 정리: CombatSystem(TryTenacity/TryGuardianAngel 제거 → StatusEffectSystem 참조 끊기), StatusEffectSystem 삭제, SkillSystem NGO 오케스트레이터 제거, FusionInGameCutover에서 CameraFollowLocalPlayer 비활성 제거. ✅

**3. 매니저/진단 클러스터 삭제 + 정리**
- 삭제: AppNetworkManager, SpectatorManager, ReconnectManager, NetworkRoomManager, NetworkPingMonitor, PingAdaptiveCombat, SupabaseManager_Ping, PingHUD, ReconnectUIBinder, InGameManager.
- 정리: GameManager/MatchmakingManager NGO 경로 제거, 위 타입을 참조하던 UI(LobbyUIController/MatchmakingUX/LobbySettingsPanel) 참조 제거. ✅

**4. 코어 데이터 정리**
- GameData/CharacterRerollSystem/GameBalanceConfig/CharacterLabels/LeaderboardManager의 잔여 `Unity.Netcode` 제거. ✅
- `grep "Unity.Netcode"` → 0 확인.

**5. 씬 정리**
- InGameScene: 비활성해 둔 NGO 오브젝트(NetworkManager/NetworkSpawnManager 등) 삭제 — 스크립트 삭제로 missing-script 된 것들.
- Login/Lobby 씬의 NGO 오브젝트도 정리.

**6. 패키지 제거**
- `Packages/manifest.json`에서 `com.unity.netcode.gameobjects` 제거(+ 미사용 시 Transport). ✅
- FusionPoC 씬·PoC 스크립트 정리(PoCNetworkCallbacks는 실매치 콜백이라 유지).

---

## 실행 방식 권장
- **블라인드 일괄 금지** — 위 1~4는 각 단계 후 컴파일을 봐야 다음 단계의 에러를 격리할 수 있다.
- 추천: 단계마다 (a) 내가 편집/삭제 → (b) 당신이 Unity에서 컴파일 → (c) 에러 보고 → (d) 내가 수정. 1~2단계씩.
- 각 단계는 git 커밋으로 끊어 두면 문제 시 그 단계만 되돌릴 수 있다.
