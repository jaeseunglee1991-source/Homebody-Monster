# Unity Login Screen Prefab Package

## 포함 파일
- `Assets/LoginScreen/Art/Top_BG_1080x720.png`
- `Assets/LoginScreen/Art/Center_Combat_1080x1080.png`
- `Assets/LoginScreen/Art/Bottom_BG_1080x720.png`
- `Assets/LoginScreen/Scripts/SafeAreaFitter.cs`
- `Assets/LoginScreen/Editor/LoginPrefabBuilder.cs`

## 사용 방법
1. 이 폴더의 `Assets/LoginScreen` 폴더를 Unity 프로젝트의 `Assets` 폴더 안에 복사합니다.
2. Unity 상단 메뉴에서 `Tools > Login Screen > Create Login Screen Prefab` 실행.
3. 생성된 프리팹 위치: `Assets/LoginScreen/Prefabs/LoginScreenPrefab.prefab`
4. 씬에 프리팹을 드래그해서 배치합니다.

## 프리팹 구조
Canvas
- BG_Root
  - Top_BG_Tiled
  - Bottom_BG_Stretch
  - Center_Combat_Fixed
- SafeArea_UI
  - Logo_Placeholder
  - GoogleLoginButton
  - GuestLoginButton

## 권장 수정
- `Logo_Placeholder`는 실제 로고 이미지로 교체하세요.
- 버튼의 `OnClick()`에 Google 로그인 / 게스트 로그인 함수를 연결하세요.
- 실제 서비스에서는 버튼 이미지를 별도 9-slice Sprite로 교체하는 것을 추천합니다.
