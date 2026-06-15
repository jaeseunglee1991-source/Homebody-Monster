# InGameScene → Fusion 전환(cutover) 수동 가이드

> 목표: 기존 **InGameScene의 배경·아레나·HUD·Cinemachine·아트 자산을 그대로 유지**하고,
> 그 밑의 네트워크 레이어만 NGO → Fusion(이미 검증된 NetPlayer/NetMatch/FusionLauncher)으로 교체.
> PoC(FusionPoC)에서 전 기능이 검증된 부품을 실 씬에 "꽂는" 작업이라 대부분 Inspector 배선이다.
> **각 단계 후 ParrelSync 2피어로 즉시 확인**하며 진행할 것. 문제 시 직전 단계로 롤백 가능하게 1단계씩.

---

## 사전: 백업
- InGameScene과 NGO 플레이어 프리팹을 **git 커밋** 또는 복제(InGameScene_NGO_backup.unity)로 백업. cutover 중 NGO를 제거하므로.

## 1단계 — Fusion 러너/콜백 프리팹 준비 (자동)
- 메뉴 `Tools ▸ Homebody Monster ▸ Fusion ▸ Create NetPlayer & Wire (1-A)` 1회 실행
  → `FusionRunner.prefab`(+PoCNetworkCallbacks: PlayerPrefab/MatchPrefab 배선), `NetPlayer.prefab`, `NetProjectile.prefab`, `NetMatch.prefab` 생성·배선 완료.
- 이 프리팹들은 씬 독립적이라 InGameScene에서 그대로 재사용.

## 2단계 — InGameScene에 Fusion 리그 추가
InGameScene을 열고 빈 GameObject **"FusionRig"** 생성 후 컴포넌트 부착:
- **FusionLauncher** — `RunnerPrefab` = `FusionRunner.prefab`, `defaultMode` = AutoHostOrClient
- **NetFx** (데미지 팝업/킬피드)
- **NetMobileInput** (조이스틱/터치/스킬 버튼)
- **NetHUD** ⚠️필수 — 리롤 준비창·머리위 닉네임/체력바·상태 글리프·관전 라벨. (누락 시 준비시간/리롤이 안 보임. Pass B 메뉴가 보장 부착함)
- (카메라는 4단계에서 결정 — NetCameraFollow 또는 기존 Cinemachine 유지)

> FusionLauncher는 `AutoStartOnLoad`(MatchmakingManager가 매칭 시 set)로 자동 시작하므로 InGameScene 단독 Play 시엔 내부 로비 UI가 뜬다(테스트용).

## 3단계 — 스폰 포인트 연결 (6-A)
- InGameScene 빈 GameObject **"SpawnPoints"** + **NetSpawnPoints** 컴포넌트.
- `points` 배열에 **기존 NetworkSpawnManager의 spawnPoints Transform들을 그대로 드래그**(Point1..N).
  - (또는 그 Transform들을 SpawnPoints 자식으로 옮기고 이름 "Point*" 유지 → 자동 수집)
- 결과: Fusion 플레이어가 실 아레나의 디자인된 지점에 스폰.

## 4단계 — 카메라
- **옵션 A(간단)**: FusionRig에 **NetCameraFollow** 추가(로컬 플레이어 추적·아레나 클램프). 기존 CameraFollowLocalPlayer(NGO)는 비활성.
- **옵션 B(기존 유지)**: Cinemachine 유지 + 스폰된 NetPlayer를 Follow 타겟으로 런타임 지정하는 소형 스크립트 필요(요청 시 작성).

## 5단계 — NGO 비활성화
- InGameScene(또는 부트스트랩 씬)의 **NGO `NetworkManager` 오브젝트 비활성화**(SetActive false) — Fusion과 동시 가동 방지.
- 씬의 **NGO `NetworkSpawnManager`** 오브젝트 비활성화(스폰은 이제 PoCNetworkCallbacks가 담당).
- 기존 NGO 플레이어 프리팹은 더 이상 스폰되지 않음.

## 6단계 — 로비 진입 배선
- **MatchmakingManager**(Login_Scene/지속 싱글톤)의 Inspector:
  - `Use Fusion Matchmaking` = ✅ (기본 true)
  - `Fusion Game Scene Name` = **"InGameScene"** (현재 "FusionPoC" → 변경)
- 결과: 로비의 기존 매칭 버튼 → Photon 세션 → **InGameScene** 자동 진입.

## 7단계 — HUD (선택: 기존 InGameHUD 재사용)
현재 Fusion 쪽 HUD는 OnGUI(NetHUD/NetMatch)다. 두 경로:
- **A(빠름)**: OnGUI 그대로 사용(NetHUD/NetMatch/NetMobileInput). 기존 InGameHUD 캔버스는 비활성. → 즉시 동작, 모양만 단순.
- **B(미려)**: 기존 InGameHUD(TMP/캔버스)를 NetPlayer 값으로 재배선. 체력바/스킬쿨다운/킬피드를 `NetPlayer.Hp/MaxHp/CooldownRemaining/LocalSkillLabel`, `NetMatch.AliveCount` 등에서 읽도록 InGameHUD 수정 필요(코드 작업 — 요청 시 어댑터 스크립트 작성).
- 권장: 일단 A로 동작 확인 → 이후 B로 교체.

## 8단계 — 검증 (ParrelSync 2피어, Login부터)
- 로비 매칭 → InGameScene 진입, 실 배경/아레나에서 2인 플레이
- 이동/평타/스킬/상태이상/투사체/은신/낙인/사망/부활/리롤/결과저장/매치종료/로비복귀 전부 정상
- 실 스폰 포인트에서 스폰되는지, 카메라/HUD 정상인지

## 9단계 — NGO 완전 제거 (검증 후)
- 비활성화했던 NGO NetworkManager/NetworkSpawnManager 오브젝트 삭제
- NGO 전용 스크립트(PlayerNetworkSync, AppNetworkManager의 NGO 부분, NetworkSpawnManager, ClientNetworkTransform, NetworkProjectile(NGO), ServerValidator/NetworkPingMonitor 등) 삭제
- `manifest.json`에서 `com.unity.netcode.gameobjects` 제거
- FusionPoC 씬·PoC 전용 스크립트 정리

---

# 패스 B — 기존 캔버스 HUD + Cinemachine 복원 (코드 완료, 자동 배선)

> 패스 A에서 OnGUI 임시 HUD로 동작을 검증한 뒤, 실제 InGameHUD(TMP 캔버스)와 Cinemachine을
> Fusion 상태로 구동하도록 어댑터를 연결한다. **스크립트는 작성 완료** — 인스펙터 배선만 남았고
> 그마저 메뉴 한 번으로 자동화했다.

## B-0 추가된 스크립트
- `Assets/Scripts/Fusion/NetHudBridge.cs` — InGameHUD(체력바/생존자/타이머/스킬버튼+쿨다운/킬피드/종료배너)를
  매 프레임 NetPlayer/NetMatch에서 읽어 구동. 스킬 버튼 onClick → `NetPlayer.UseSkillAimed`.
  `NetHudBridge.Active`(static)가 true면 OnGUI HUD들이 겹치는 부분을 스스로 숨긴다.
- `Assets/Scripts/Fusion/NetCinemachineTarget.cs` — 기존 `CinemachineCamera`가 로컬 NetPlayer를
  Follow(사망 시 생존자 관전, Tab 순환). 아레나 구도는 Cinemachine 본체 설정에 위임.
- 역할 분담(중복 자동 방지): InGameHUD(캔버스) ↔ NetHUD(머리위 미니바·리롤·부활·관전) /
  NetMatch(종료버튼) / NetFx(데미지팝업) / NetMobileInput(조이스틱·탭공격)은 그대로 유지.

## B-1 자동 배선 (권장)
1. InGameScene을 연다.
2. 메뉴 `Tools ▸ Homebody Monster ▸ Fusion ▸ Wire Open Scene for Pass B (HUD + Cinemachine)` 1회 실행.
   - InGameHUD 캔버스 재활성화 + NetHudBridge 부착(hud 연결)
   - CinemachineCamera 재활성화 + NetCinemachineTarget 부착, NGO CameraFollowLocalPlayer 비활성
   - FusionRig의 NetCameraFollow 비활성(Cinemachine과 충돌 방지)
   - 레거시 온스크린 조이스틱(VariableJoystick 등) 비활성(“background not assigned” 에러 제거)
3. Console 로그로 각 단계 결과 확인 → **Ctrl+S로 씬 저장**.

## B-1' 수동 배선 (메뉴를 안 쓸 경우)
- InGameHUD 캔버스 SetActive(true) 복원
- FusionRig에 `NetHudBridge` 추가 → `hud` = InGameHUD
- CinemachineCamera(vcam) SetActive(true) + `NetCinemachineTarget` 추가(`vcam` 연결)
- FusionRig의 `NetCameraFollow` 컴포넌트 비활성(체크 해제), vcam의 `CameraFollowLocalPlayer` 비활성
- 캔버스 내 `VariableJoystick`(조이스틱 오브젝트) 비활성

## B-2 검증 (ParrelSync 2피어, Login부터)
- 체력바·생존자·타이머·**스킬 버튼(실제 스킬명/쿨다운 fill)**·킬피드·종료배너가 **캔버스 UI**로 표시
- 스킬 버튼 탭/클릭 → Fusion 스킬 발동(조이스틱 방향 조준), 버튼 탭이 이동/공격으로 새지 않음
- 카메라가 내 캐릭터 추적, 사망 시 관전(Tab)
- 종료 시 배너에 WINNER + 내 결과(순위/킬/생존), 호스트 Restart/로비 나가기 버튼 동작

## B-3 추가 완료 (부활/포기 + 항복 캔버스 이관)
- **부활/포기 UI → InGameHUD.revivePanel**: NetHudBridge가 NetPlayer.ReviveOffered/ReviveProcessing/
  ReviveRemaining을 읽어 캔버스 부활 패널(타이머·안내·사용/포기 버튼)을 직접 구동. 버튼 onClick →
  `NetPlayer.AcceptReviveRpc/GiveUpReviveRpc`. ShowReviveUI 시그니처 변경 없이(InGameHUD 필드 직접 구동)
  처리해 NGO 코드 무영향. `NetHudBridge.ReviveActive`(static)로 NetHUD OnGUI 부활/관전 라벨 중복 방지.
- **Android Back / Esc → 항복(Surrender)**: NetHudBridge가 Esc(또는 Android Back)로 InGameHUD.surrenderConfirmPanel
  토글 → 확인 시 신설 `NetPlayer.RequestSurrenderRpc`(자진 사망, 부활 제안 없음, 매치는 계속). 준비시간/사망/부활창 중엔 무시.
  ※ Android 하드웨어 Back 키 매핑은 기기별 검증 필요(에디터 Esc는 동작).

## B-3' 남은 다듬기(선택)
- 종료 화면: 현재 배너=캔버스, Restart/나가기 버튼=OnGUI. 원하면 InGameHUD에 버튼 추가해 일원화.

---

# 패스 C — NGO 완전 제거 (⚠️ Pass B 검증 후에만)

> **파괴적 작업이므로 Pass B를 2피어로 검증한 뒤 실행**한다. 아래는 `using Unity.Netcode`를
> import하는 14개 파일을 실측·분류한 것. **GameData/SkillSystem/CharacterRerollSystem은 Fusion이
> 재사용하므로 삭제 금지** — NGO 부분만 발췌 제거.

## C-1 삭제 대상 (NGO 전용 — Fusion이 완전 대체)
- `Network/PlayerNetworkSync.cs` → NetPlayer
- `Network/NetworkSpawnManager.cs` → NetSpawnPoints + PoCNetworkCallbacks
- `Network/NetworkProjectile.cs` → NetProjectile
- `Network/ClientNetworkTransform.cs` → Fusion NetworkTransform
- `Network/ServerValidator.cs` → NetPlayer/NetMatch 서버 권위 내장
- `Network/NetworkPingMonitor.cs` → Fusion 내장 통계
- `Core/PingAdaptiveCombat.cs` → 불필요(서버 시뮬)
- `Core/PlayerVisibility.cs` → NetVisual
- `Managers/AppNetworkManager.cs` → FusionLauncher/NetworkRunner
- `Utils/StandaloneTestBootstrap.cs` → NGO 단독 테스트 부트스트랩
- `Core/PlayerController.cs` → NetPlayer (단, InGameHUD.InitPlayerUI / SpectatorManager 등 **참조처 정리 필요** — 단순 삭제 시 컴파일 깨짐)

## C-2 유지하되 NGO 부분만 제거 (Fusion 재사용)
- `Core/GameData.cs` — CharacterData/MatchResult 등 핵심 데이터. NGO 직렬화/INetworkSerializable만 제거.
- `Core/SkillSystem.cs` — 스킬 데이터 + `GetCooldown`(순수함수) 재사용. `ActivateSkillServer` 등 NGO 오케스트레이터 switch 제거(NetSkillSystem이 대체).
- `Managers/CharacterRerollSystem.cs` — 경제/`GetSkillDisplayName` 재사용. NGO 참조만 제거.

## C-3 NGO 참조 정리 (import은 없지만 NetworkManager.Singleton 등 사용)
- `Managers/InGameManager.cs`, `Managers/ReconnectManager.cs`, `Managers/MatchmakingManager.cs`(Fusion 분기 외 NGO 큐 경로),
  `Managers/GameManager.cs`, `Managers/LoadingScreenManager.cs`, `Managers/SupabaseManager_Ping.cs`,
  `UI/ResultController.cs`, `Core/PlayerWorldUI.cs` — `NetworkManager.Singleton` 가드/호출 제거 또는 Fusion 대체.

## C-4 패키지·씬 정리
- `Packages/manifest.json`에서 `com.unity.netcode.gameobjects` 제거(+ Transport 미사용 시 함께).
- 씬에서 비활성화해 둔 NGO NetworkManager/NetworkSpawnManager 오브젝트 삭제.
- `Assets/Scenes/FusionPoC.unity` + `Assets/Scripts/Fusion/PoC/` (PoC 전용) 정리. ※ `PoCNetworkCallbacks`는 실 매치 콜백으로 쓰이므로 **유지**(이름만 추후 리네이밍 권장).

> 순서 권장: C-3(참조 가드 제거) → C-1(삭제) → C-2(발췌 제거) → 컴파일 → C-4(패키지/씬). 한 번에 다 지우지 말고 컴파일 단위로.

---

## 메모
- `ICombatStatus`(CombatSystem 시밍), `StatCalculator`, `SupabaseManager`, `CharacterRerollSystem`, `JobVisualRegistry`, `GameBalanceConfig`는 NGO 무관 → **그대로 유지**.
- 호스트 마이그레이션은 미구현(MVP=호스트 이탈 시 종료). 출시 후 보강.
