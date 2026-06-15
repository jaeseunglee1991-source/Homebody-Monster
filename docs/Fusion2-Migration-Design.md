# Photon Fusion 2 마이그레이션 상세 설계서

> 대상 프로젝트: Homebody-Monster (Unity 2D 실시간 PvP 배틀로얄)
> 현재 네트워킹: **Unity Netcode for GameObjects (NGO) 2.11.0**, 데디케이티드 서버 + 서버 권위
> 목표: **Photon Fusion 2 / Host Mode 출시**, 추후 **Server Mode(데디) 전환 가능**하도록 설계
> Photon Cloud 앱: Fusion, 100 CCU 무료, `HomeBodyMonster`, App ID `ec3cc7fe-b…`, 300GB 트래픽
> 작성일: 2026-06-09

---

## 0. 이 문서의 결론 (먼저 읽기)

1. 이건 **설정 교체가 아니라 네트워크 레이어 재작성**이다. 게임 로직(데미지 공식·스탯)·Supabase·UI 레이아웃은 보존되지만, NGO API(NetworkVariable/ServerRpc/ClientRpc/lifecycle)에 묶인 부분은 전부 Fusion API로 다시 써야 한다.
2. 착수 전 **반드시 합의해야 할 설계 결정 2개**가 있다 — (A) 이동 모델, (B) 매치메이킹 방식. 이게 작업량을 좌우한다 (§2).
3. **Host↔Server 전환 비용은 3가지 규칙만 지키면 거의 0** (§2.1). Fusion이 이걸 위해 설계됐다.
4. 가장 어려운 신규 작업은 **호스트 마이그레이션**(§2.5). 데디 모델엔 없던 것.

---

## 1. 현재 아키텍처 실측 (인벤토리)

코드 전수 조사 결과:

| 항목 | 수량 | 위치 |
|---|---|---|
| `NetworkBehaviour` 클래스 | 6 | PlayerNetworkSync, PlayerController, NetworkSpawnManager, NetworkProjectile, NetworkPingMonitor, PlayerVisibility |
| `NetworkVariable<T>` | 10 | PlayerNetworkSync 9 + PlayerController 1(NetworkIsChasing) |
| `[ServerRpc]` / `[ClientRpc]` | 40 | PlayerNetworkSync 32, NetworkSpawnManager 4, NetworkPingMonitor 2, NetworkProjectile 1, PlayerController 1 |
| `INetworkSerializable` 구조체 | 2 | NetworkCharacterData, DamageResult |

파일별 네트워크 API 결합도 (touch points, 높을수록 작업량 큼):

| 파일 | 결합도 | 변경 유형 |
|---|---:|---|
| `Network/PlayerNetworkSync.cs` (1,691줄) | **223** | ★전면 재작성 (작업 핵심) |
| `Core/SkillSystem.cs` (952줄) | **113** | ★재작성 (서버측 오케스트레이터 — 아래 주의) |
| `Core/PlayerController.cs` (912줄) | 68 | 재작성 (입력/이동 모델 전환) |
| `Managers/InGameManager.cs` (688줄) | 63 | 대폭 수정 (매치 오케스트레이션) |
| `Network/NetworkProjectile.cs` | 40 | 재작성 (예측 스폰) |
| `Network/NetworkSpawnManager.cs` | 34 | 재작성 (OnPlayerJoined 스폰) |
| `Managers/AppNetworkManager.cs` | 33 | 재작성 → `NetworkRunner` 부트스트랩 |
| `Network/NetworkPingMonitor.cs` | 26 | 삭제 후보 (Fusion 내장 RTT) |
| `Core/StatusEffectSystem.cs` | 14 | 소폭 수정 |
| `Managers/CharacterRerollSystem.cs` | 13 | 수정 (재제출 RPC) |
| `Network/ServerValidator.cs` | 11 | 단순화/대폭 축소 (이동모델 전환 시) |
| `Utils/StandaloneTestBootstrap.cs` | 9 | 재작성 (Host PoC 참고용) |
| `UI/InGameHUD.cs` | 8 | 수정 (RPC 호출부 재배선) |
| `Core/PingAdaptiveCombat.cs` | 6 | 삭제 후보 (Fusion LagCompensation) |
| `Managers/MatchmakingManager.cs` | 5 | **대폭 삭제** → Photon 세션 |
| `Core/PlayerWorldUI.cs` | 4 | 수정 (Networked 구독) |
| `Network/ClientNetworkTransform.cs` | 1 | **삭제** (Fusion NetworkTransform) |
| `Core/CombatSystem.cs` | **1** | ✅ 사실상 무관 — 거의 그대로 |

> **중요 발견**: `CombatSystem`(데미지 공식)은 네트워크와 무관(touch point 1)하지만, `SkillSystem`은 113곳에서 네트워크 레이어를 호출하는 **서버측 오케스트레이터**다. `ActivateSkillServer()`가 `networkSync.ApplyDamageServer/ApplyStatusEffectServer/ForcePositionClientRpc`, `ServerValidator.RegisterSkillTeleport`, `NetworkProjectile` 스폰 등을 직접 호출한다. "게임 로직은 다 재사용"이라는 일반론에서 **SkillSystem은 예외** — 호출하는 네트워크 메서드 시그니처가 전부 바뀌므로 상당한 재배선이 필요하다.

### 현재 동작 흐름 (요약)
- **접속**: 데디 서버가 `StartServer` → 클라가 매칭으로 받은 `IP:Port`로 `StartClient`.
- **매칭**: Supabase `matchmaking_queue` 폴링 → 서버가 `server_assign_match` RPC로 `IP:Port` 배정 → 클라가 접속.
- **스폰**: `HandleClientConnected` → `SpawnAsPlayerObject(clientId)`. 게임시작 후 접속은 차단(`DisconnectClient`).
- **이동**: ⚠️ **클라이언트 권한** (`ClientNetworkTransform.OnIsServerAuthoritative()=false`). Owner가 위치를 서버로 보고. 서버는 `ServerValidator`로 사후 검증 후 위반 시 `ForcePositionClientRpc`로 롤백.
- **전투**: 서버 권위. 평타(`RequestAttackServerRpc`)·스킬(`RequestUseSkillServerRpc`) → 서버에서 `CombatSystem` 계산 → `NetworkHp` 갱신 → `NotifyHitClientRpc` 연출.
- **부활권/결과 저장**: 데디 서버는 Supabase `auth.uid()`가 없어, **인증된 Owner 클라에 위임**(서버→클라 RPC→클라가 Supabase 호출→서버로 보고). 이 패턴은 Host/Server 양쪽 모두 유지.
- **이동 동반 스킬**: 클라권한 트랜스폼이라 서버가 직접 위치를 못 바꿈 → `ForcePositionClientRpc`/`ForceMoveClientRpc`/`ForceKnockbackClientRpc`로 Owner에게 지시.

---

## 2. 먼저 합의해야 할 핵심 설계 결정

### 2.1 토폴로지: Host Mode + 단일 GameMode 주입 (전환 규칙)

출시 = `GameMode.Host`. 추후 데디 = `GameMode.Server`. 전환 비용을 0에 가깝게 유지하는 **3대 규칙**:

1. **권위 판정은 오직 `Object.HasStateAuthority` / `HasInputAuthority`** 사용.
   현 `IsServer`→`HasStateAuthority`, `IsOwner`→`HasInputAuthority`. **"호스트=플레이어" 가정 코드 금지.**
2. **`GameMode`는 단일 진입점에서 주입** (`-server` 커맨드라인 인자 또는 빌드 디파인). 나머지 코드는 모드를 몰라야 함.
3. **플레이어 스폰은 `OnPlayerJoined(runner, player)`에서.** Host 모드는 호스트 자신도 join → 캐릭터 생성, Server 모드는 서버 peer는 join 없음 → 캐릭터 없음. 분기 코드 없이 양쪽 자동 충족.

전환 시 바뀌는 것은 **`GameMode` 값 + 보안 등급(호스트 치팅 가능 여부)뿐**, 게임 코드는 안 바뀐다.

### 2.2 ★이동 모델 전환 (최대 결정 — 작업량/체감 좌우)

**현재**: 클라이언트 권한 트랜스폼 + 서버 사후검증 + RPC 롤백 + RTT 유예/스킬 텔레포트 면제. (NGO의 한계를 우회하느라 누적된 복잡도가 큼)

**Fusion 권장안 (옵션 A — 강력 추천)**: **입력 권한 + 서버/호스트 시뮬레이션 + 클라 예측(Prediction)**.
- 클라는 `OnInput`에서 입력 구조체(`INetworkInput`)만 채움 → 권위 peer가 `FixedUpdateNetwork`에서 `NetworkRigidbody2D` 이동 시뮬레이션 → 예측+보간으로 모바일에서도 부드럽게.
- 이 전환으로 **삭제 가능**: `ClientNetworkTransform`, `UpdateMoveDirServerRpc`/`NetworkMoveDir`, `Force*ClientRpc` 롤백 3종(안티치트 용도), `ServerValidator`의 위치 검증 대부분(서버가 시뮬하므로 위치 위조 자체가 불가), `PingAdaptiveCombat`(Fusion LagCompensation으로 대체).
- **대가**: 이동 "느낌"이 바뀌어 예측 파라미터 재튜닝 필요. 이동 동반 스킬(ShadowRaid 순간이동·돌진·넉백)은 `Force*ClientRpc` 대신 **서버가 직접 `NetworkRigidbody2D`를 조작**하면 됨(오히려 단순).

**옵션 B (현 동작 보존, 재작업 최소)**: Fusion에서도 입력권한 클라가 `NetworkTransform`을 직접 움직이게. → 현재 구조와 유사하나 Fusion의 예측/안티치트 이점을 못 살리고, 결국 `Force*` 롤백 복잡도가 그대로 남음.

> **권고: 옵션 A.** 이 프로젝트가 NGO에서 겪은 버그·우회 코드의 상당수가 "클라권한 트랜스폼 + 서버 롤백" 모델에서 비롯됐다. Fusion의 틱 기반 입력권한 모델은 정확히 이 문제를 없애려고 만들어졌다. 단 이동 체감 재튜닝은 별도 QA 항목으로 잡아야 함.

### 2.3 정체성(Identity) 전환

| 현재 (NGO) | Fusion 2 |
|---|---|
| `ulong OwnerClientId` (식별자 전반) | `PlayerRef` |
| RPC에 `ulong NetworkObjectId` 전달 후 `SpawnManager.SpawnedObjects.TryGetValue`로 조회 | **RPC에 `NetworkObject`/`NetworkBehaviour` 참조 직접 전달** (조회 불필요 — 단순화) |
| `NetworkManager.Singleton` | `NetworkRunner` (주입 필요) |

영향 범위가 넓다: `OwnerClientId`를 키로 쓰는 `InGameManager._playerFinalRanks/_playerDeathTimes`, `NetworkSpawnManager._players`, `ServerValidator._records` 등이 전부 `PlayerRef` 기반으로 바뀐다.

### 2.4 매치메이킹: Supabase 큐 → Photon 세션 (대폭 삭제)

**삭제 대상**: `MatchmakingManager`의 서버 루프(`RunServerLoop`/`ExecuteServerMatch`), `IP:Port` 감지·배정, Supabase `matchmaking_queue` 테이블·`server_assign_match` RPC, `AppNetworkManager.ConnectToGameServer`/`StartAsDedicatedServer`, `ReconnectManager`의 IP 재접속 로직.

**대체**: `runner.StartGame(StartGameArgs{ GameMode, SessionName, PlayerCount, ... })` 로 Photon 세션 매칭. 인원/대기 로직은 Photon 룸 속성·`SessionLobby`로.

**유지**: Supabase는 **인증/리더보드/상점/부활권/일일보상**에 계속 사용 (네트워크와 무관).

> 결정 필요: "Photon 완전 자동 매칭"으로 갈지, 아니면 "커스텀 매칭 로직(Supabase로 SessionName만 조율)"을 유지할지. 전자가 단순. 현재 큐는 단순 인원 매칭이므로 **전자 권장**.

### 2.5 호스트 마이그레이션 (신규 작업 — 가장 어려움)

데디 모델엔 없던 개념. Host Mode에서 **호스트(=플레이어)가 이탈하면** 매치가 죽는다. Fusion 2의 호스트 마이그레이션 API로 다른 클라가 권위를 승계하도록 구현해야 함:
- 상태 스냅샷 → 새 호스트가 `StartGameArgs.HostMigrationResume`로 재개 → 각 `NetworkBehaviour`가 스냅샷에서 `[Networked]` 상태 복원.
- 진행 중 매치(HP·생존자·부활 카운트·타이머)를 끊김 없이 넘기는 것이 핵심 난관.
- **MVP 절충안**: 1차 출시에선 "호스트 이탈 시 매치 종료 + 결과 처리"로 단순화하고, 마이그레이션은 2차로. (소수 유저·짧은 매치면 수용 가능)

### 2.6 랙 보정: 커스텀 → Fusion 내장

`NetworkPingMonitor`(커스텀 RTT) + `PingAdaptiveCombat`(위치 스냅샷 되감기)는 Fusion의 `Runner.LagCompensation.Raycast` + 내장 RTT로 대체 가능 → **삭제 후보**. 평타/투사체 적중 판정을 LagCompensation으로 옮기면 정확도↑·코드↓.

---

## 3. NGO → Fusion 2 API 매핑표

| NGO (현재) | Fusion 2 | 비고 |
|---|---|---|
| `NetworkManager.Singleton` | `NetworkRunner` | DI/싱글톤 래퍼 필요 |
| `: NetworkBehaviour` (NGO) | `: NetworkBehaviour` (Fusion) | 동명·완전 다른 API |
| `NetworkVariable<T> x = new(...)` | `[Networked] public T X { get; set; }` | §7 |
| `x.OnValueChanged += H` | `[Networked, OnChangedRender(nameof(H))]` 또는 `ChangeDetector` (`Render()`) | 콜백 모델 변경 |
| `[ServerRpc]` | `[Rpc(RpcSources.InputAuthority, RpcTargets.StateAuthority)]` | §6 |
| `[ClientRpc]` (broadcast) | `[Rpc(RpcSources.StateAuthority, RpcTargets.All)]` | |
| `[ClientRpc]` + `ClientRpcParams`(owner만) | `[Rpc(...)]` + `[RpcTarget] PlayerRef target` | 타겟 지정 |
| `OnNetworkSpawn()` / `OnNetworkDespawn()` | `Spawned()` / `Despawned(runner, hasState)` | |
| `IsServer` | `Object.HasStateAuthority` / `Runner.IsServer` | §2.1 |
| `IsOwner` | `Object.HasInputAuthority` | |
| `IsHost` / `IsClient` | `Runner.GameMode` / `Runner.IsClient` | 가정 코드 제거 |
| `FixedUpdate`(서버 검증/전투) | `FixedUpdateNetwork()` | 틱 기반 |
| `Update`(입력 수집) | `OnInput(runner, input)` + `Update`(UI) | |
| `Instantiate + netObj.SpawnAsPlayerObject(clientId)` | `Runner.Spawn(prefab, pos, rot, inputAuthority: player)` | `OnPlayerJoined`에서 |
| `netObj.SpawnWithOwnership(clientId)` | `Runner.Spawn(..., inputAuthority: player)` | |
| `netObj.Spawn()` (서버소유/봇) | `Runner.Spawn(..., inputAuthority: default)` | 봇=입력권한 없음 |
| `netObj.Despawn()` | `Runner.Despawn(obj)` | |
| `SpawnManager.SpawnedObjects.TryGetValue(id)` | `Runner.TryFindObject(id)` 또는 참조 직접 전달 | |
| `NetworkManager.SceneManager.LoadScene` | `Runner.LoadScene(SceneRef)` (NetworkSceneManager) | |
| `OnClientConnectedCallback` | `INetworkRunnerCallbacks.OnPlayerJoined` | |
| `OnClientDisconnectCallback` | `OnPlayerLeft` | |
| `NetworkManager.DisconnectClient(id)` | `Runner.Disconnect(player)` | |
| `ServerTime.TimeAsFloat` | `Runner.SimulationTime` / `Runner.Tick` | 결정적 시간 |
| `UnityTransport.SetConnectionData(ip,port)` | (불필요 — Photon Cloud 릴레이) | IP 개념 제거 |
| `INetworkSerializable` 구조체 | `INetworkStruct` (unmanaged) | §5 |
| RPC `string` 파라미터 | `NetworkString<_32>` (권장) | §5 |

---

## 4. 파일별 변경 명세

### 재작성 (Rewrite)
- **`Network/PlayerNetworkSync.cs`** — 핵심. NetworkVariable 9개→`[Networked]`, RPC 32개→`[Rpc]`, lifecycle, 권위 판정 전부. 부활권 위임 패턴은 구조 유지(타겟을 `[RpcTarget] PlayerRef`로). 봇 초기화(`DebugInitializeAsBot`) 유지.
- **`Core/PlayerController.cs`** — 입력을 `OnInput`으로, 이동을 `FixedUpdateNetwork`로(옵션 A). `NetworkIsChasing`/`SetChasingServerRpc`/`NetworkMoveDir` 관련 정리. 애니메이션/외형(`UpdateVisualByJob`)은 `Render()`/OnChanged로. 봇 가드(`_isBot`) 유지.
- **`Core/SkillSystem.cs`** — `ActivateSkillServer`가 호출하는 모든 네트워크 메서드(ApplyDamageServer/ApplyStatusEffectServer/Force*/RegisterSkillTeleport/투사체 스폰) 시그니처 변경에 맞춰 재배선. 데미지/효과 "계산" 로직 자체는 보존.
- **`Network/NetworkProjectile.cs`** — Fusion 예측 스폰 또는 서버권위 `NetworkRigidbody2D`. `OnTriggerEnter2D` 판정을 LagCompensation 기반으로 옮기는 것 검토.
- **`Network/NetworkSpawnManager.cs`** — `OnPlayerJoined`에서 `Runner.Spawn`. 게임시작 후 입장 차단 로직은 세션 `IsOpen=false`로.
- **`Managers/AppNetworkManager.cs`** — `NetworkRunner` 부트스트랩 + `StartGameArgs`(GameMode 단일 주입) + `INetworkRunnerCallbacks` 구현. 로비 채팅(Supabase Realtime) 부분은 그대로 유지.
- **`Utils/StandaloneTestBootstrap.cs`** — `StartHost`→`GameMode.Host` PoC. 봇 스폰을 `Runner.Spawn(inputAuthority:default)`로.

### 대폭 수정 (Major edit)
- **`Managers/InGameManager.cs`** — `OwnerClientId`→`PlayerRef` 키 전환, `ServerTime`→`Runner.SimulationTime`, RPC 브로드캐스트 방식(`FindObjectsByType<PlayerNetworkSync>` 순회→Fusion 타겟 RPC) 정리, 씬 로드를 `Runner.LoadScene`로.
- **`Managers/MatchmakingManager.cs`** — 서버 루프·IP 배정 삭제, Photon 세션 매칭으로 축소 (§2.4).
- **`Managers/ReconnectManager.cs`** — IP 재접속 삭제, Fusion 재접속/호스트 마이그레이션 연동.
- **`Network/NetworkPingMonitor.cs`, `Core/PingAdaptiveCombat.cs`, `Network/ServerValidator.cs`** — 이동모델 옵션 A 채택 시 대부분 삭제/축소 (§2.2, §2.6).

### 소폭 수정 (Minor)
- `UI/InGameHUD.cs` — 스킬/부활/항복 버튼의 RPC 호출부 재배선.
- `Core/StatusEffectSystem.cs` — `ApplyEffectServer`/`ApplyEffectNetwork` 호출 경로의 권위 판정.
- `Managers/CharacterRerollSystem.cs` — `ResubmitCharacterData`(재제출 RPC) 경로.
- `Core/PlayerWorldUI.cs` — NetworkJob/Affinity/Grade를 `[Networked]` OnChanged 구독으로.
- `Core/CameraFollowLocalPlayer.cs` — 로컬 플레이어 탐색(`HasInputAuthority`).

### 삭제 (Delete)
- `Network/ClientNetworkTransform.cs` — Fusion `NetworkTransform`/`NetworkRigidbody2D`로 대체.
- (옵션 A 시) `NetworkPingMonitor`, `PingAdaptiveCombat`, `ServerValidator` 위치검증부.
- Supabase `matchmaking_queue` 관련 마이그레이션/RPC.

### 거의 유지 (Keep)
- `Core/CombatSystem.cs` — 데미지 공식. 네트워크 무관. ✅
- `Core/StatCalculator.cs`, `Core/GameBalanceConfig.cs` — 순수 로직. ✅
- `Managers/SupabaseManager*.cs` 전체 — 인증/리더보드/상점/부활권. ✅
- `Core/GameData.cs` — `CharacterData`는 유지, `DamageResult`/`NetworkCharacterData`는 `INetworkStruct`화(§5).

---

## 5. 데이터 구조 변환

### INetworkSerializable → INetworkStruct
두 구조체 모두 unmanaged(블리터블) 필드만 있어 변환 용이:

- **`NetworkCharacterData`**: 전부 `int`/`float` → `INetworkStruct`로 바로 전환. (NGO 직렬화 필드 순서 의존 주석 있음 — Fusion에선 메모리 레이아웃 기반이라 무관해지나, 출시 후 필드 추가 정책은 동일하게 주의)
- **`DamageResult`**: `float` 1 + `bool` 9 → `INetworkStruct`. Fusion에서 `bool`은 `NetworkBool` 권장.

### string RPC 파라미터 → NetworkString
`string`을 RPC로 넘기는 곳(킬피드 이름, 닉네임, 카운트다운 메시지 등 다수)은 Fusion에서 GC 유발 → `NetworkString<_32>`(또는 `_64`) 권장. 닉네임은 이미 `FixedString64Bytes` 사용 중이라 매핑 자연스러움.

영향 RPC 예: `BroadcastKillFeedClientRpc(string,string)`, `DeathMarkKillFeedClientRpc(string,string)`, `ShowCountdownOwnerClientRpc(string)`, `ShowCountdownClientRpc(string)`.

---

## 6. RPC 40개 → Fusion `[Rpc]` 매핑

방향·타겟별 분류 (Fusion 속성):

| 현재 RPC 유형 | 예시 | Fusion `[Rpc(sources, targets)]` |
|---|---|---|
| Client→Server 요청 | `RequestAttackServerRpc`, `RequestUseSkillServerRpc`, `RequestReviveServerRpc`, `RequestGiveUpServerRpc`, `RequestSurrenderServerRpc`, `ReportReviveTicketResultServerRpc`, `SubmitCharacterDataServerRpc`, `UpdateMoveDirServerRpc`(삭제예정), `SetChasingServerRpc`, `PingServerRpc` | `InputAuthority → StateAuthority` |
| Server→전체 브로드캐스트 | `BroadcastKillFeedClientRpc`, `DeclareDeathClientRpc`, `NotifyHitClientRpc`, `BroadcastSkillVisualsClientRpc`, `SyncStatusEffectClientRpc`, `SpawnTrapVisualClientRpc`, `SpawnHitEffectClientRpc`, `BeginGameClientRpc`, `RemoveAllDebuffsClientRpc`, `DeathMarkKillFeedClientRpc` | `StateAuthority → All` (또는 `Proxies`) |
| Server→특정 Owner | `OfferReviveClientRpc`, `RequestTicketDeductionClientRpc`, `ExecuteReviveClientRpc`, `ReviveDeniedClientRpc`, `ForceLoadResultSceneClientRpc`, `NotifyMatchResultClientRpc`, `NotifyGameStartedOwnerClientRpc`, `ShowCountdownOwnerClientRpc`, `HideCountdownOwnerClientRpc`, `ShowLuckyPopupClientRpc`, `ForcePositionClientRpc`, `ForceMoveClientRpc`, `ForceKnockbackClientRpc`, `PingResponseClientRpc` | `StateAuthority → InputAuthority` 또는 `[RpcTarget] PlayerRef` |

> `Force*ClientRpc` 3종은 이동모델 옵션 A 채택 시 **삭제**(서버가 NetworkRigidbody2D 직접 조작). `Ping*`는 Fusion 내장 RTT로 **삭제** 가능.

---

## 7. NetworkVariable 10개 → `[Networked]`

| 변수 | 타입 | OnChanged 핸들러 | 비고 |
|---|---|---|---|
| `NetworkHp` | float | HandleHpChanged → HUD 체력바 | `[Networked, OnChangedRender]` |
| `NetworkMaxHp` | float | — | |
| `NetworkKillCount` | int | HandleKillCountChanged | |
| `NetworkIsDead` | NetworkBool | HandleDeadChanged | |
| `NetworkNickname` | FixedString64 → `NetworkString<_64>` | HandleNicknameChanged | 머리위 닉네임 |
| `NetworkJob` | int | OnNetworkJobChanged → 외형 | PlayerController가 구독 |
| `NetworkAffinity` | int | PlayerWorldUI | |
| `NetworkGrade` | int | PlayerWorldUI | |
| `NetworkMoveDir` | Vector2 | flipX 갱신 | 옵션 A 시 **삭제**(입력에서 파생) |
| `NetworkIsChasing` (PlayerController) | NetworkBool | 애니메이션 | 옵션 A 시 **삭제** 가능 |

전부 현재 **서버 쓰기 권한**이므로 Fusion에선 StateAuthority가 쓰는 `[Networked]`로 자연 매핑(클라 위변조 차단 동일).

---

## 8. 단계별 실행 계획

각 단계는 **완료 기준(DoD)** 충족 후 다음으로.

### Phase 0 — SDK + 연결 PoC
- (사용자) Fusion 2 SDK 임포트 + App ID `ec3cc7fe-b…` 설정 (§11).
- `FusionLauncher`(토폴로지 무관, GameMode 단일 주입) + 최소 `INetworkRunnerCallbacks`.
- 캡슐 1개 + `NetworkTransform` 테스트 씬.
- **DoD**: 두 인스턴스가 Host/Client로 Photon Cloud 경유 접속, 서로 이동이 보임. 모바일 빌드에서 호스트 동작·핑 체감 확인.

### Phase 1 — 플레이어 동기화 (★핵심)
- `PlayerController` + `PlayerNetworkSync` 포팅: `[Networked]` 9개, `[Rpc]` 32개, lifecycle, 권위 판정.
- 이동 모델 옵션 A: `OnInput` + `FixedUpdateNetwork` + `NetworkRigidbody2D` + 예측.
- 평타·스킬 요청 RPC → 서버 `CombatSystem`/`SkillSystem` 호출(로직 보존) → HP 동기화 → 피격 연출.
- **DoD**: 2인 매치에서 이동/평타/HP감소/사망/킬피드/외형 동기화 정상.

### Phase 2 — 스폰 / 투사체 / 효과
- `NetworkSpawnManager` → `OnPlayerJoined` 스폰. `NetworkProjectile` 예측 스폰. `StatusEffectSystem` 경로.
- **DoD**: 투사체 스킬·상태이상·덫이 모든 클라에서 동기화.

### Phase 3 — 매치메이킹 교체
- Supabase 큐·IP 배정 삭제 → Photon 세션 매칭. Supabase는 인증/영속성만.
- **DoD**: 로비→매칭→인게임 씬 진입까지 Photon 세션으로 완주.

### Phase 4 — 부활/결과/안티치트/재접속/호스트 마이그레이션
- 부활권 위임 패턴 포팅(타겟 RPC). 결과 저장 위임 포팅. 안티치트 축소(옵션 A). 재접속.
- 호스트 마이그레이션 (또는 MVP 절충: 호스트 이탈 시 매치 종료).
- **DoD**: 부활/결과저장/이탈처리 정상. 호스트 이탈 시 정의된 동작.

### Phase 5 — UI 재배선 + 통합 QA
- HUD/Result가 `[Networked]` 구독. 전체 통합 테스트(매칭·전투·부활·킬피드·이탈·재접속·이동 체감 재튜닝).
- **DoD**: 출시 후보 빌드 안정 동작.

> 작업량: 네트워크 레이어 **수 주** 규모. Phase 1·4가 대부분.

---

## 9. Server Mode 전환 체크리스트 (추후)

전환 시 **바뀌는 것**:
- `StartGameArgs.GameMode` = `GameMode.Server`.
- 헤드리스 빌드를 돌릴 머신 1대 (오라클 Arm Ampere 등 — Fusion은 아웃바운드 접속이라 공인IP/포트개방 불필요).
- 보안 등급↑ (중립 서버 → 호스트 치팅 불가).

전환 시 **안 바뀌는 것** (§2.1 규칙 준수 시):
- 모든 게임 로직·`[Networked]`·`[Rpc]`·권위 판정 코드.
- 호스트 마이그레이션은 불필요해짐(서버가 안 떠남).

---

## 10. 리스크 / 미해결 질문

1. **이동 체감 재튜닝** (옵션 A): 모바일에서 예측/보간 파라미터 튜닝이 별도 QA 필요.
2. **호스트 마이그레이션**: 가장 어려운 신규 작업. MVP에서 "매치 종료" 절충 여부 결정 필요.
3. **매치메이킹 정책**: Photon 완전 자동 vs Supabase 조율 유지 (§2.4) — 결정 필요.
4. **호스트 성능**: 모바일 기기가 N명 매치를 호스팅할 때 성능/배터리 — Phase 0에서 실측 필요.
5. **NGO 동시 존치**: 마이그레이션 중 NGO와 Fusion 패키지 공존 가능(네임스페이스 분리). 검증 후 NGO 제거.
6. **결정적 시간**: `ServerTime`→`Runner.SimulationTime` 전환 시 타이머/부활 60초 판정 재확인.

---

## 11. 사용자 선행 작업 (코드 착수 전 필수)

Fusion 2 SDK는 계정 인증이 걸려 Unity 에디터에서 직접 임포트해야 함:
1. Photon 대시보드 → **SDK** → Fusion 2 최신 `.unitypackage` 다운로드.
2. Unity 에디터 `Assets > Import Package`로 임포트.
3. **Fusion Hub** 마법사에 App ID `ec3cc7fe-b…` 입력 → `PhotonAppSettings.asset` 생성.
4. 마이그레이션 중에는 NGO 패키지 존치(공존 가능). Fusion 검증 후 `com.unity.netcode.gameobjects` 제거.

SDK 임포트 완료 후 → **Phase 0 코드(FusionLauncher + 최소 연결 PoC)**부터 착수.
