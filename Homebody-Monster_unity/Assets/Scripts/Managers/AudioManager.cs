using UnityEngine;
using System.Collections;
using System.Collections.Generic;

/// <summary>
/// 게임 전체의 BGM·SFX를 관리하는 오디오 매니저.
///
/// ─ 설계 원칙 ──────────────────────────────────────────────────
///  • DontDestroyOnLoad 싱글톤 — 씬 전환 시에도 BGM이 끊기지 않음
///  • BGM : AudioSource 2개 크로스페이드 (로비↔인게임 전환 자연스럽게)
///  • SFX : AudioSource Pool (동일 프레임 여러 효과음 동시 재생)
///  • SettingsManager 연동 — BGM/SFX 볼륨 슬라이더 즉시 반영
///  • 스킬 40종 × 음원 자동 매핑 (AudioClip 미할당 시 조용히 스킵)
///
/// ─ Inspector 연결 체크리스트 ──────────────────────────────────
///  [BGM]
///    bgmLobby      : 로비 배경음악
///    bgmInGame     : 인게임 배경음악
///    bgmResult     : 결과화면 배경음악
///  [UI SFX]
///    sfxButtonClick  : 버튼 클릭
///    sfxMatchFound   : 매칭 완료 효과음
///    sfxRevive       : 부활 효과음
///    sfxDailyReward  : 출석 보상 팝업
///  [전투 SFX]
///    sfxAttackHit    : 평타 피격
///    sfxSkillCast    : 스킬 시전 (공용)
///    sfxDeath        : 사망
///    sfxCritical     : 크리티컬 / 상성 발동
///    sfxKillFeed     : 킬 알림
///    sfxHeal         : 회복
///    sfxShield       : 실드 활성
///  [스킬별 SFX — 선택, 비워두면 sfxSkillCast 사용]
///    sfxSkillOverrides : ActiveSkillType 키 → AudioClip 매핑 배열
/// </summary>
public class AudioManager : MonoBehaviour
{
    public static AudioManager Instance { get; private set; }

    // ── BGM ───────────────────────────────────────────────────
    [Header("BGM 클립")]
    public AudioClip bgmLobby;
    public AudioClip bgmInGame;
    public AudioClip bgmResult;

    [Header("BGM 설정")]
    [Range(0f, 3f)] public float bgmFadeDuration = 1.2f;

    // ── UI SFX ────────────────────────────────────────────────
    [Header("UI 효과음")]
    public AudioClip sfxButtonClick;
    public AudioClip sfxMatchFound;
    public AudioClip sfxRevive;
    public AudioClip sfxDailyReward;

    // ── 전투 SFX ──────────────────────────────────────────────
    [Header("전투 효과음")]
    public AudioClip sfxAttackHit;
    public AudioClip sfxSkillCast;   // 스킬별 오버라이드 없으면 이걸 사용
    public AudioClip sfxDeath;
    public AudioClip sfxCritical;
    public AudioClip sfxKillFeed;
    public AudioClip sfxHeal;
    public AudioClip sfxShield;

    // ── 스킬별 SFX 오버라이드 ─────────────────────────────────
    [System.Serializable]
    public struct SkillSoundEntry
    {
        public ActiveSkillType skill;
        public AudioClip       clip;
    }

    [Header("스킬별 효과음 오버라이드 (비워두면 sfxSkillCast 사용)")]
    public SkillSoundEntry[] sfxSkillOverrides;

    // ── SFX 풀 설정 ───────────────────────────────────────────
    [Header("SFX 풀")]
    [Tooltip("동시 재생 가능한 최대 SFX 채널 수 (기본 8)")]
    [Range(4, 16)] public int sfxPoolSize = 8;

    // ─── 내부 상태 ────────────────────────────────────────────
    private AudioSource   _bgmA;
    private AudioSource   _bgmB;
    private bool          _bgmUsingA = true;
    private Coroutine     _fadeCoroutine;

    private AudioSource[] _sfxPool;
    private int           _sfxPoolIndex = 0;

    private readonly Dictionary<ActiveSkillType, AudioClip> _skillSoundMap
        = new Dictionary<ActiveSkillType, AudioClip>();

    // ════════════════════════════════════════════════════════════
    //  Unity 생명주기
    // ════════════════════════════════════════════════════════════

    private void Awake()
    {
        if (Instance != null && Instance != this) { Destroy(gameObject); return; }
        Instance = this;
        DontDestroyOnLoad(gameObject);

        BuildAudioSources();
        BuildSkillSoundMap();
        ApplyVolumesFromSettings();
    }

    private void BuildAudioSources()
    {
        _bgmA = gameObject.AddComponent<AudioSource>();
        _bgmB = gameObject.AddComponent<AudioSource>();
        foreach (var src in new[] { _bgmA, _bgmB })
        {
            src.loop        = true;
            src.playOnAwake = false;
            src.volume      = 0f;
        }

        _sfxPool = new AudioSource[sfxPoolSize];
        for (int i = 0; i < sfxPoolSize; i++)
        {
            var src = gameObject.AddComponent<AudioSource>();
            src.loop        = false;
            src.playOnAwake = false;
            _sfxPool[i]     = src;
        }
    }

    private void BuildSkillSoundMap()
    {
        _skillSoundMap.Clear();
        if (sfxSkillOverrides == null) return;
        foreach (var entry in sfxSkillOverrides)
        {
            if (entry.clip != null)
                _skillSoundMap[entry.skill] = entry.clip;
        }
    }

    // ════════════════════════════════════════════════════════════
    //  BGM 공개 API
    // ════════════════════════════════════════════════════════════

    public void PlayLobbyBGM()  => CrossFadeBGM(bgmLobby);
    public void PlayInGameBGM() => CrossFadeBGM(bgmInGame);
    public void PlayResultBGM() => CrossFadeBGM(bgmResult);
    public void StopBGM()       => CrossFadeBGM(null);

    // ════════════════════════════════════════════════════════════
    //  SFX 공개 API
    // ════════════════════════════════════════════════════════════

    public void PlayButtonClick() => PlaySFX(sfxButtonClick);
    public void PlayAttackHit()   => PlaySFX(sfxAttackHit);
    public void PlayDeath()       => PlaySFX(sfxDeath);
    public void PlayCritical()    => PlaySFX(sfxCritical);
    public void PlayKillFeed()    => PlaySFX(sfxKillFeed);
    public void PlayHeal()        => PlaySFX(sfxHeal);
    public void PlayShield()      => PlaySFX(sfxShield);
    public void PlayMatchFound()  => PlaySFX(sfxMatchFound);
    public void PlayRevive()      => PlaySFX(sfxRevive);
    public void PlayDailyReward() => PlaySFX(sfxDailyReward);

    public void PlaySkillSound(ActiveSkillType skill)
    {
        if (_skillSoundMap.TryGetValue(skill, out AudioClip clip) && clip != null)
            PlaySFX(clip);
        else
            PlaySFX(sfxSkillCast);
    }

    // ════════════════════════════════════════════════════════════
    //  볼륨 설정 (SettingsManager 연동)
    // ════════════════════════════════════════════════════════════

    public void SetBgmVolume(float volume)
    {
        volume = Mathf.Clamp01(volume);
        ActiveBGM().volume = volume;
    }

    public void SetSfxVolume(float volume)
    {
        volume = Mathf.Clamp01(volume);
        foreach (var src in _sfxPool)
            if (src != null) src.volume = volume;
    }

    // ════════════════════════════════════════════════════════════
    //  내부 — BGM 크로스페이드
    // ════════════════════════════════════════════════════════════

    private void CrossFadeBGM(AudioClip newClip)
    {
        AudioSource current = ActiveBGM();
        if (current.clip == newClip && current.isPlaying) return;

        if (_fadeCoroutine != null) StopCoroutine(_fadeCoroutine);
        _fadeCoroutine = StartCoroutine(FadeRoutine(newClip));
    }

    private IEnumerator FadeRoutine(AudioClip newClip)
    {
        float bgmVol   = SettingsManager.Instance?.BgmVolume ?? 1f;
        AudioSource fadeOut = ActiveBGM();
        AudioSource fadeIn  = InactiveBGM();

        fadeIn.clip   = newClip;
        fadeIn.volume = 0f;
        if (newClip != null) fadeIn.Play();

        float elapsed = 0f;
        while (elapsed < bgmFadeDuration)
        {
            elapsed += Time.unscaledDeltaTime;
            float t = Mathf.Clamp01(elapsed / bgmFadeDuration);
            fadeOut.volume = Mathf.Lerp(bgmVol, 0f, t);
            if (newClip != null)
                fadeIn.volume = Mathf.Lerp(0f, bgmVol, t);
            yield return null;
        }

        fadeOut.Stop();
        fadeOut.clip   = null;
        fadeOut.volume = 0f;
        if (newClip != null) fadeIn.volume = bgmVol;

        _bgmUsingA = !_bgmUsingA;
        _fadeCoroutine = null;
    }

    // ════════════════════════════════════════════════════════════
    //  내부 — SFX 풀
    // ════════════════════════════════════════════════════════════

    private void PlaySFX(AudioClip clip)
    {
        if (clip == null) return;

        float sfxVol = SettingsManager.Instance?.SfxVolume ?? 1f;
        if (sfxVol <= 0f) return;

        AudioSource src = GetFreeSFXSource();
        src.clip   = clip;
        src.volume = sfxVol;
        src.Play();
    }

    private AudioSource GetFreeSFXSource()
    {
        foreach (var src in _sfxPool)
            if (src != null && !src.isPlaying) return src;

        _sfxPoolIndex = (_sfxPoolIndex + 1) % _sfxPool.Length;
        return _sfxPool[_sfxPoolIndex];
    }

    // ════════════════════════════════════════════════════════════
    //  내부 유틸
    // ════════════════════════════════════════════════════════════

    private AudioSource ActiveBGM()   => _bgmUsingA ? _bgmA : _bgmB;
    private AudioSource InactiveBGM() => _bgmUsingA ? _bgmB : _bgmA;

    private void ApplyVolumesFromSettings()
    {
        if (SettingsManager.Instance == null) return;
        SetBgmVolume(SettingsManager.Instance.BgmVolume);
        SetSfxVolume(SettingsManager.Instance.SfxVolume);
    }
}
