/// <summary>
/// CombatSystem이 데미지 계산 시 참조하는 상태이상 인터페이스.
/// NGO <see cref="StatusEffectSystem"/>(기존) 과 Fusion <c>NetStatus</c>(신규) 양쪽이 구현하여
/// 동일한 CombatSystem 데미지 공식을 공유한다(하위호환).
///
/// CombatSystem.CalculateDamage / CalculateDamageWithOverride / PostDamageEffects 가 사용하는
/// 멤버만 노출한다. (TryTenacity/TryGuardianAngel 등은 별도 — 인터페이스 미포함)
/// </summary>
public interface ICombatStatus
{
    float GetAtkMultiplier();
    bool  ConsumeDivineGrace();
    bool  IsImmune { get; }
    float AbsorbWithShield(float incomingDamage);
    bool  IsInDefenseStance { get; }
    bool  IsInUndyingRage { get; }
}
